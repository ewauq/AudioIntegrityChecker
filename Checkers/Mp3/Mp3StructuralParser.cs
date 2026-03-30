using System.Text;

namespace AudioIntegrityChecker.Checkers.Mp3;

/// <summary>
/// Pass 1: pure C# structural analysis of an MP3 byte stream.
/// Performs frame scanning, header validation, CRC-16 checking, and Xing/LAME tag validation.
/// No DLL required.
/// </summary>
internal static class Mp3StructuralParser
{
    // -------------------------------------------------------------------------
    // Frame structure constants
    // -------------------------------------------------------------------------

    private const int FrameHeaderSize = 4; // bytes 0–3: sync + version/layer + bitrate/sr + channels
    private const int FrameCrcSize = 2; // bytes 4–5 when protection_bit == 0
    private const int Id3v1TagSize = 128; // ID3v1 tag is always exactly 128 bytes

    // Sync word: first byte must be 0xFF, upper 3 bits of second byte must be set (0b111xxxxx).
    // Together they form the 11-bit sync pattern required by ISO 11172-3.
    private const byte SyncByte = 0xFF;
    private const byte SyncMask = 0xE0; // 0b11100000

    // -------------------------------------------------------------------------
    // Bitrate tables (kbps) for MPEG Layer III
    // Index 0 (free bitrate) and 15 (bad) are invalid — both stored as 0.
    // -------------------------------------------------------------------------

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

    // -------------------------------------------------------------------------
    // Sample rate tables (Hz) indexed by [version][srIdx]
    // version: 0=MPEG2.5, 1=reserved, 2=MPEG2, 3=MPEG1
    // -------------------------------------------------------------------------

    private static readonly int[][] SampleRates =
    [
        [11025, 12000, 8000, 0], // 0 = MPEG 2.5
        [0, 0, 0, 0], // 1 = reserved
        [22050, 24000, 16000, 0], // 2 = MPEG 2
        [44100, 48000, 32000, 0], // 3 = MPEG 1
    ];

    // -------------------------------------------------------------------------
    // Public entry point
    // -------------------------------------------------------------------------

    internal static List<(Mp3Diagnostic Diagnostic, long FrameIndex)> Scan(byte[] buf)
    {
        var diagnostics = new List<(Mp3Diagnostic, long)>();

        if (buf.Length < FrameHeaderSize)
            return diagnostics;

        int pos = SkipId3v2(buf);
        long frameCount = 0;
        int firstFramePos = -1;
        int firstVersion = 3; // default MPEG1
        int firstChannelMode = 0; // default stereo

        while (pos <= buf.Length - FrameHeaderSize)
        {
            // ----------------------------------------------------------------
            // Sync detection
            // ----------------------------------------------------------------
            if (buf[pos] != SyncByte || (buf[pos + 1] & SyncMask) != SyncMask)
            {
                if (frameCount == 0)
                {
                    // Still searching for the first frame — just advance
                    pos++;
                    continue;
                }

                // Between frames: scan for next sync
                int syncPos = FindNextSync(buf, pos);
                if (syncPos < 0)
                    break; // no more frames

                int gap = syncPos - pos;
                // Gaps of 1–3 bytes are typically alignment padding left by tag editors (JUNK_DATA).
                // Larger gaps indicate a genuine break in the frame sequence (LOST_SYNC).
                diagnostics.Add(
                    (gap <= 3 ? Mp3Diagnostic.JUNK_DATA : Mp3Diagnostic.LOST_SYNC, frameCount)
                );
                pos = syncPos;
                continue;
            }

            // ----------------------------------------------------------------
            // Parse 4-byte frame header
            // ----------------------------------------------------------------
            byte h1 = buf[pos + 1];
            byte h2 = buf[pos + 2];
            byte h3 = buf[pos + 3];

            int version = (h1 >> 3) & 0x03; // 3=MPEG1, 2=MPEG2, 0=MPEG2.5, 1=reserved
            int layer = (h1 >> 1) & 0x03; // must be 1 for Layer III (00=reserved, 01=L3, 10=L2, 11=L1)
            int protectionBit = h1 & 0x01; // 0 = CRC follows header, 1 = no CRC
            int bitrateIdx = (h2 >> 4) & 0x0F; // 0=free bitrate (unsupported), 15=bad
            int srIdx = (h2 >> 2) & 0x03; // 3=reserved
            int paddingBit = (h2 >> 1) & 0x01; // 1 = frame has one extra slot
            int channelMode = (h3 >> 6) & 0x03; // 3=Mono, 0-2=stereo variants

            // Reject reserved version (1), non-Layer-III (layer != 1),
            // free bitrate (0), forbidden bitrate (15), and reserved sample rate (3).
            if (version == 1 || layer != 1 || bitrateIdx == 0 || bitrateIdx == 15 || srIdx == 3)
            {
                // Not a valid MP3 frame header — emit BAD_HEADER and resync
                diagnostics.Add((Mp3Diagnostic.BAD_HEADER, frameCount));
                int syncPos = FindNextSync(buf, pos + 1);
                if (syncPos < 0)
                    break;
                pos = syncPos;
                continue;
            }

            int bitrate = (version == 3 ? Mpeg1L3Bitrate : Mpeg2L3Bitrate)[bitrateIdx] * 1000; // version 3 = MPEG1; table is kbps → bps
            int sampleRate = SampleRates[version][srIdx];
            // Frame size formula from ISO 11172-3: 144 = samples_per_frame / bits_per_byte = 1152 / 8
            int frameSize = (144 * bitrate / sampleRate) + paddingBit;

            if (frameSize < 4)
            {
                // Degenerate — skip one byte and resync
                pos++;
                continue;
            }

            // ----------------------------------------------------------------
            // CRC-16 check (if protection_bit == 0, CRC is present)
            // ----------------------------------------------------------------
            if (protectionBit == 0 && pos + FrameHeaderSize + FrameCrcSize - 1 < buf.Length)
            {
                // CRC is stored in bytes 4–5 (immediately after the 4-byte header)
                int crcStored = (buf[pos + FrameHeaderSize] << 8) | buf[pos + FrameHeaderSize + 1];
                int sideInfoLen = SideInfoLength(version, channelMode);

                if (pos + FrameHeaderSize + FrameCrcSize + sideInfoLen <= buf.Length)
                {
                    // CRC covers header bytes 2–3 (h2 and h3), then the side information block
                    ushort crcComputed = Crc16(buf, pos + 2, 2);
                    crcComputed = Crc16Continue(
                        crcComputed,
                        buf,
                        pos + FrameHeaderSize + FrameCrcSize,
                        sideInfoLen
                    );

                    if (crcStored != crcComputed)
                        diagnostics.Add((Mp3Diagnostic.FRAME_CRC_MISMATCH, frameCount));
                }
            }

            // ----------------------------------------------------------------
            // Record first frame info for Xing/LAME analysis
            // ----------------------------------------------------------------
            if (frameCount == 0)
            {
                firstFramePos = pos;
                firstVersion = version;
                firstChannelMode = channelMode;
            }

            // ----------------------------------------------------------------
            // Truncation check
            // ----------------------------------------------------------------
            if (pos + frameSize > buf.Length)
            {
                // Allow for trailing ID3v1 tag — if remaining bytes look like "TAG", not truncated
                int remaining = buf.Length - pos;
                bool hasId3v1Tail =
                    buf.Length >= Id3v1TagSize
                    && buf[buf.Length - Id3v1TagSize] == (byte)'T'
                    && buf[buf.Length - Id3v1TagSize + 1] == (byte)'A'
                    && buf[buf.Length - Id3v1TagSize + 2] == (byte)'G';

                if (!hasId3v1Tail || remaining < Id3v1TagSize)
                    diagnostics.Add((Mp3Diagnostic.TRUNCATED_STREAM, frameCount));

                break;
            }

            frameCount++;
            pos += frameSize;
        }

        // ----------------------------------------------------------------
        // Xing / Info / LAME tag validation (uses first frame)
        // ----------------------------------------------------------------
        if (firstFramePos >= 0 && frameCount > 0)
            CheckXingLame(
                buf,
                firstFramePos,
                firstVersion,
                firstChannelMode,
                frameCount,
                diagnostics
            );

        return diagnostics;
    }

    // -------------------------------------------------------------------------
    // Xing / LAME header validation
    // -------------------------------------------------------------------------

    private static void CheckXingLame(
        byte[] buf,
        int firstFramePos,
        int version,
        int channelMode,
        long actualFrameCount,
        List<(Mp3Diagnostic, long)> diagnostics
    )
    {
        int xingOffset = firstFramePos + XingHeaderOffset(version, channelMode);

        // Need at least the 4-byte signature + 4-byte flags field
        if (xingOffset + 8 > buf.Length)
            return;

        // Detect "Xing" (VBR) or "Info" (CBR) signature
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

        if (!isXing && !isInfo)
            return;

        // Flags field: big-endian uint32 at offset +4 (immediately after the 4-byte signature)
        uint flags = ReadBigEndianUInt32(buf, xingOffset + 4);

        // Bit 0 of flags: frame count field is present (at offset +8, after 4-byte sig + 4-byte flags)
        if ((flags & 0x01) != 0 && xingOffset + 12 <= buf.Length) // 12 = 4 sig + 4 flags + 4 frame count
        {
            uint storedFrameCount = ReadBigEndianUInt32(buf, xingOffset + 8); // offset +8: right after flags
            // Allow a difference of 1: some encoders store the frame count excluding
            // the header frame itself (the frame that contains the Xing/Info tag),
            // while our scanner counts every MPEG frame including that first one.
            long diff = (long)storedFrameCount - actualFrameCount;
            if (diff < -1 || diff > 1)
            {
                var diag = isXing
                    ? Mp3Diagnostic.XING_FRAME_COUNT_MISMATCH // VBR
                    : Mp3Diagnostic.INFO_FRAME_COUNT_MISMATCH; // CBR
                diagnostics.Add((diag, 0));
            }
        }

        // LAME tag: "LAME" signature at xingOffset + 120
        // The Xing/Info frame is structured so the LAME extension always starts at a fixed
        // offset of 120 bytes after the Xing/Info signature, regardless of VBR/CBR or version.
        int lameTagPos = xingOffset + 120;
        if (lameTagPos + 4 > buf.Length)
            return;

        bool isLame =
            buf[lameTagPos] == (byte)'L'
            && buf[lameTagPos + 1] == (byte)'A'
            && buf[lameTagPos + 2] == (byte)'M'
            && buf[lameTagPos + 3] == (byte)'E';

        if (!isLame)
            return;

        // LAME CRC covers the first 190 bytes of the info frame (bytes 0–189),
        // and is stored big-endian at bytes 190–191 of that same frame.
        const int LameCrcCoverage = 190; // bytes covered by the CRC
        const int LameCrcOffset = 190; // byte position of the stored CRC high byte
        if (firstFramePos + LameCrcOffset + 2 > buf.Length)
            return;

        int lameCrcStored =
            (buf[firstFramePos + LameCrcOffset] << 8) | buf[firstFramePos + LameCrcOffset + 1];
        ushort lameCrcComputed = CrcLameTag(buf, firstFramePos, LameCrcCoverage);

        if (lameCrcStored != lameCrcComputed)
            diagnostics.Add((Mp3Diagnostic.LAME_TAG_CRC_MISMATCH, 0));
    }

    // -------------------------------------------------------------------------
    // ID3v2 skip
    // -------------------------------------------------------------------------

    private static int SkipId3v2(byte[] buf)
    {
        const int Id3v2HeaderSize = 10; // 3 "ID3" + 2 version + 1 flags + 4 syncsafe size

        if (buf.Length < Id3v2HeaderSize)
            return 0;

        if (buf[0] != (byte)'I' || buf[1] != (byte)'D' || buf[2] != (byte)'3')
            return 0;

        // Version (buf[3]) and revision (buf[4]) bytes must not be 0xFF (invalid marker)
        if (buf[3] == 0xFF || buf[4] == 0xFF)
            return 0;

        // Syncsafe integer: each of the 4 size bytes uses only 7 bits (MSB always 0)
        // buf[6]<<21 | buf[7]<<14 | buf[8]<<7 | buf[9] reconstructs the 28-bit value
        int tagSize = (buf[6] << 21) | (buf[7] << 14) | (buf[8] << 7) | buf[9];

        // Flags byte (buf[5]) bit 4: footer present — adds another 10-byte block after the tag
        bool hasFooter = (buf[5] & 0x10) != 0;

        int skip = Id3v2HeaderSize + tagSize + (hasFooter ? Id3v2HeaderSize : 0);
        return Math.Min(skip, buf.Length);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static int FindNextSync(byte[] buf, int from)
    {
        for (int i = from; i <= buf.Length - 2; i++)
        {
            if (buf[i] == SyncByte && (buf[i + 1] & SyncMask) == SyncMask)
                return i;
        }
        return -1;
    }

    /// <summary>Returns the byte offset of the Xing/Info tag from the start of the first frame.</summary>
    private static int XingHeaderOffset(int version, int channelMode)
    {
        bool isMpeg1 = version == 3;
        bool isMono = channelMode == 3;
        return (isMpeg1, isMono) switch
        {
            (true, false) => 36, // MPEG1 stereo/joint/dual: 4-byte header + 32-byte side info
            (true, true) => 21, // MPEG1 mono:              4-byte header + 17-byte side info
            (false, false) => 21, // MPEG2/2.5 stereo:        4-byte header + 17-byte side info
            (false, true) => 13, // MPEG2/2.5 mono:          4-byte header +  9-byte side info
        };
    }

    /// <summary>Returns the side information length in bytes for CRC computation.</summary>
    private static int SideInfoLength(int version, int channelMode)
    {
        bool isMpeg1 = version == 3;
        bool isMono = channelMode == 3;
        return (isMpeg1, isMono) switch
        {
            (true, false) => 32,
            (true, true) => 17,
            (false, false) => 17,
            (false, true) => 9,
        };
    }

    private static uint ReadBigEndianUInt32(byte[] buf, int offset) =>
        ((uint)buf[offset] << 24)
        | ((uint)buf[offset + 1] << 16)
        | ((uint)buf[offset + 2] << 8)
        | buf[offset + 3];

    // -------------------------------------------------------------------------
    // CRC-16/ARC: poly=0x8005, init=0xFFFF, input reflected, output reflected.
    // Used for MP3 frame header protection (ISO 11172-3).
    // -------------------------------------------------------------------------

    private static ushort Crc16(byte[] buf, int offset, int length)
    {
        ushort crc = 0xFFFF;
        for (int i = 0; i < length; i++)
        {
            byte b = buf[offset + i];
            for (int bit = 0; bit < 8; bit++)
            {
                bool databit = (b & 1) != 0;
                bool crcBit = (crc & 1) != 0;
                crc >>= 1;
                if (databit ^ crcBit)
                    crc ^= 0xA001; // reflected 0x8005
                b >>= 1;
            }
        }
        return crc;
    }

    private static ushort Crc16Continue(ushort crc, byte[] buf, int offset, int length)
    {
        for (int i = 0; i < length; i++)
        {
            byte b = buf[offset + i];
            for (int bit = 0; bit < 8; bit++)
            {
                bool databit = (b & 1) != 0;
                bool crcBit = (crc & 1) != 0;
                crc >>= 1;
                if (databit ^ crcBit)
                    crc ^= 0xA001; // reflected 0x8005
                b >>= 1;
            }
        }
        return crc;
    }

    // -------------------------------------------------------------------------
    // CRC-16 used by the LAME tag: poly=0x8005, init=0x0000, NOT reflected (MSB-first).
    // Matches the CRC_update() function in libmp3lame/VbrTag.c.
    // -------------------------------------------------------------------------

    private static ushort CrcLameTag(byte[] buf, int offset, int length)
    {
        ushort crc = 0x0000;
        for (int i = 0; i < length; i++)
        {
            crc ^= (ushort)(buf[offset + i] << 8);
            for (int bit = 0; bit < 8; bit++)
            {
                if ((crc & 0x8000) != 0)
                    crc = (ushort)((crc << 1) ^ 0x8005);
                else
                    crc = (ushort)(crc << 1);
            }
        }
        return crc;
    }
}
