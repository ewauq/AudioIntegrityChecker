namespace AudioIntegrityChecker.Checkers.Mp3;

/// <summary>
/// Lightweight MP3 duration extractor. Reads only the first ~10 KB of the file:
/// enough to skip the ID3v2 tag, parse the first frame header, and read the
/// Xing/Info frame count (VBR/CBR). Falls back to a bitrate-based estimate
/// for files without a Xing/Info header.
/// </summary>
internal static class Mp3MetadataReader
{
    private const int HeaderReadSize = 10_240; // 10 KB — covers ID3v2 + first frame + Xing tag

    private static readonly int[] Mpeg1L3Bitrate =
    [
        0,
        32,
        40,
        48,
        56,
        64,
        80,
        96,
        112,
        128,
        160,
        192,
        224,
        256,
        320,
        0,
    ];

    private static readonly int[] Mpeg2L3Bitrate =
    [
        0,
        8,
        16,
        24,
        32,
        40,
        48,
        56,
        64,
        80,
        96,
        112,
        128,
        144,
        160,
        0,
    ];

    private static readonly int[][] SampleRates =
    [
        [11025, 12000, 8000, 0], // 0 = MPEG 2.5
        [0, 0, 0, 0], //           1 = reserved
        [22050, 24000, 16000, 0], // 2 = MPEG 2
        [44100, 48000, 32000, 0], // 3 = MPEG 1
    ];

    internal static TimeSpan? TryReadDuration(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            long fileSize = stream.Length;
            if (fileSize < 4) return null;

            // Peek at the 10-byte ID3v2 header to get the exact tag size,
            // then seek past it so the frame buffer starts at the first audio frame.
            // This handles arbitrarily large ID3v2 tags (e.g. files with embedded artwork).
            int id3Size = 0;
            if (fileSize >= 10)
            {
                var id3Header = new byte[10];
                stream.ReadExactly(id3Header, 0, 10);
                if (id3Header[0] == (byte)'I' && id3Header[1] == (byte)'D' && id3Header[2] == (byte)'3'
                    && id3Header[3] != 0xFF && id3Header[4] != 0xFF)
                {
                    int tagSize = (id3Header[6] << 21) | (id3Header[7] << 14)
                                | (id3Header[8] << 7)  | id3Header[9];
                    bool hasFooter = (id3Header[5] & 0x10) != 0;
                    id3Size = 10 + tagSize + (hasFooter ? 10 : 0);
                }
            }

            long frameAreaStart = Math.Min(id3Size, fileSize);
            long remaining = fileSize - frameAreaStart;
            if (remaining < 4) return null;

            stream.Seek(frameAreaStart, SeekOrigin.Begin);
            int toRead = (int)Math.Min(remaining, HeaderReadSize);
            var buf = new byte[toRead];
            stream.ReadExactly(buf, 0, toRead);

            return ParseDuration(buf, fileSize, id3Size);
        }
        catch
        {
            return null;
        }
    }

    private static TimeSpan? ParseDuration(byte[] buf, long fileSize, int id3Size)
    {
        if (buf.Length < 4)
            return null;

        int pos = 0; // buf already starts at the first audio frame

        while (pos <= buf.Length - 4)
        {
            // Sync detection: 0xFF + upper 3 bits set
            if (buf[pos] != 0xFF || (buf[pos + 1] & 0xE0) != 0xE0)
            {
                pos++;
                continue;
            }

            byte h1 = buf[pos + 1];
            byte h2 = buf[pos + 2];
            byte h3 = buf.Length > pos + 3 ? buf[pos + 3] : (byte)0;

            int version = (h1 >> 3) & 0x03; // 3=MPEG1, 2=MPEG2, 0=MPEG2.5, 1=reserved
            int layer = (h1 >> 1) & 0x03; // must be 1 for Layer III
            int bitrateIdx = (h2 >> 4) & 0x0F;
            int srIdx = (h2 >> 2) & 0x03;
            int channelMode = (h3 >> 6) & 0x03; // 3 = Mono

            if (version == 1 || layer != 1 || bitrateIdx == 0 || bitrateIdx == 15 || srIdx == 3)
            {
                pos++;
                continue;
            }

            bool isMpeg1 = version == 3;
            int bitrate = (isMpeg1 ? Mpeg1L3Bitrate : Mpeg2L3Bitrate)[bitrateIdx] * 1000;
            int sampleRate = SampleRates[version][srIdx];
            if (sampleRate == 0)
            {
                pos++;
                continue;
            }

            // MPEG1 Layer3 = 1152 samples/frame, MPEG2/2.5 Layer3 = 576 samples/frame
            int samplesPerFrame = isMpeg1 ? 1152 : 576;

            // Try Xing/Info header for exact frame count
            bool isMono = channelMode == 3;
            int xingOffset = pos + XingHeaderOffset(isMpeg1, isMono);

            if (xingOffset + 12 <= buf.Length)
            {
                bool isXing =
                    buf[xingOffset] == (byte)'X'
                    && buf[xingOffset + 1] == (byte)'i'
                    && buf[xingOffset + 2] == (byte)'n'
                    && buf[xingOffset + 3] == (byte)'g';
                bool isInfo =
                    buf[xingOffset] == (byte)'I'
                    && buf[xingOffset + 1] == (byte)'n'
                    && buf[xingOffset + 2] == (byte)'f'
                    && buf[xingOffset + 3] == (byte)'o';

                if (isXing || isInfo)
                {
                    uint flags = ReadBigEndianUInt32(buf, xingOffset + 4);
                    if ((flags & 0x01) != 0) // frame count field present at offset +8
                    {
                        uint frameCount = ReadBigEndianUInt32(buf, xingOffset + 8);
                        if (frameCount > 0)
                            return TimeSpan.FromSeconds(
                                (double)frameCount * samplesPerFrame / sampleRate
                            );
                    }
                }
            }

            // CBR fallback: estimate from file size and bitrate
            long audioBytes = fileSize - id3Size;
            if (bitrate > 0 && audioBytes > 0)
                return TimeSpan.FromSeconds((double)audioBytes / (bitrate / 8.0));

            break;
        }

        return null;
    }

    private static int XingHeaderOffset(bool isMpeg1, bool isMono) =>
        (isMpeg1, isMono) switch
        {
            (true, false) => 36, // MPEG1 stereo:  4-byte header + 32-byte side info
            (true, true) => 21, //  MPEG1 mono:    4-byte header + 17-byte side info
            (false, false) => 21, // MPEG2/2.5 stereo
            (false, true) => 13, //  MPEG2/2.5 mono: 4-byte header + 9-byte side info
        };

    private static uint ReadBigEndianUInt32(byte[] buf, int offset) =>
        ((uint)buf[offset] << 24)
        | ((uint)buf[offset + 1] << 16)
        | ((uint)buf[offset + 2] << 8)
        | buf[offset + 3];
}
