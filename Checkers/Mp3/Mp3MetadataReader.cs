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

    internal static TimeSpan? TryReadDuration(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            long fileSize = stream.Length;
            if (fileSize < Mp3Format.FrameHeaderSize)
                return null;

            // Peek at the 10-byte ID3v2 header to get the exact tag size,
            // then seek past it so the frame buffer starts at the first audio frame.
            // This handles arbitrarily large ID3v2 tags (e.g. files with embedded artwork).
            int id3Size = 0;
            if (fileSize >= Mp3Format.Id3v2HeaderSize)
            {
                var id3Header = new byte[Mp3Format.Id3v2HeaderSize];
                stream.ReadExactly(id3Header, 0, Mp3Format.Id3v2HeaderSize);
                if (
                    id3Header[0] == (byte)'I'
                    && id3Header[1] == (byte)'D'
                    && id3Header[2] == (byte)'3'
                    && id3Header[3] != 0xFF
                    && id3Header[4] != 0xFF
                )
                {
                    // Syncsafe integer: 4 bytes × 7 bits each → 28-bit tag size
                    int tagSize =
                        (id3Header[6] << 21)
                        | (id3Header[7] << 14)
                        | (id3Header[8] << 7)
                        | id3Header[9];
                    bool hasFooter = (id3Header[5] & Mp3Format.Id3v2FooterFlag) != 0;
                    id3Size =
                        Mp3Format.Id3v2HeaderSize
                        + tagSize
                        + (hasFooter ? Mp3Format.Id3v2HeaderSize : 0);
                }
            }

            long frameAreaStart = Math.Min(id3Size, fileSize);
            long remaining = fileSize - frameAreaStart;
            if (remaining < Mp3Format.FrameHeaderSize)
                return null;

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
        if (buf.Length < Mp3Format.FrameHeaderSize)
            return null;

        int pos = 0; // buf already starts at the first audio frame

        while (pos <= buf.Length - Mp3Format.FrameHeaderSize)
        {
            // Sync detection: 0xFF + upper 3 bits set (11-bit sync word per ISO 11172-3)
            if (
                buf[pos] != Mp3Format.SyncByte
                || (buf[pos + 1] & Mp3Format.SyncMask) != Mp3Format.SyncMask
            )
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

            // version 1=reserved; layer must be 1 for Layer III; bitrateIdx 0=free, 15=forbidden; srIdx 3=reserved
            if (version == 1 || layer != 1 || bitrateIdx == 0 || bitrateIdx == 15 || srIdx == 3)
            {
                pos++;
                continue;
            }

            bool isMpeg1 = version == Mp3Format.Mpeg1Version;
            int bitrate =
                (isMpeg1 ? Mp3Format.Mpeg1L3Bitrate : Mp3Format.Mpeg2L3Bitrate)[bitrateIdx] * 1_000; // kbps → bps
            int sampleRate = Mp3Format.SampleRates[version][srIdx];
            if (sampleRate == 0)
            {
                pos++;
                continue;
            }

            int samplesPerFrame = isMpeg1
                ? Mp3Format.SamplesPerFrameMpeg1
                : Mp3Format.SamplesPerFrameMpeg2;

            // Try Xing/Info header for exact frame count
            int xingOffset = pos + Mp3Format.XingHeaderOffset(version, channelMode);

            if (xingOffset + 12 <= buf.Length) // 12 = 4-byte sig + 4-byte flags + 4-byte frame count
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
                    if ((flags & Mp3Format.XingFlagFrameCount) != 0) // frame count present at offset +8
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

    private static uint ReadBigEndianUInt32(byte[] buf, int offset) =>
        ((uint)buf[offset] << 24)
        | ((uint)buf[offset + 1] << 16)
        | ((uint)buf[offset + 2] << 8)
        | buf[offset + 3];
}
