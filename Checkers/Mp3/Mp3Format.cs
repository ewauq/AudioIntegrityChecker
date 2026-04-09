namespace AudioIntegrityChecker.Checkers.Mp3;

/// <summary>
/// MPEG Layer III format constants shared between structural parsing and metadata reading.
/// </summary>
internal static class Mp3Format
{
    // -------------------------------------------------------------------------
    // Frame sync word (ISO 11172-3)
    // First byte must be 0xFF; upper 3 bits of second byte must all be 1 (0b111xxxxx).
    // Together they form the 11-bit sync pattern.
    // -------------------------------------------------------------------------
    internal const byte SyncByte = 0xFF;
    internal const byte SyncMask = 0xE0; // 0b11100000

    // -------------------------------------------------------------------------
    // Frame header layout
    // -------------------------------------------------------------------------
    internal const int FrameHeaderSize = 4; // bytes 0–3: sync + version/layer + bitrate/sr + channels

    // -------------------------------------------------------------------------
    // MPEG version codes (bits 4–3 of header byte 1)
    // -------------------------------------------------------------------------
    internal const int Mpeg1Version = 3; // version field value encoding MPEG-1
    internal const int MonoChannelMode = 3; // channel_mode value for single-channel mono

    // -------------------------------------------------------------------------
    // Samples per MPEG Layer III frame
    // -------------------------------------------------------------------------
    internal const int SamplesPerFrameMpeg1 = 1152; // MPEG-1  Layer III
    internal const int SamplesPerFrameMpeg2 = 576; //  MPEG-2 / MPEG-2.5  Layer III

    // Frame size formula: (coefficient × bitrate_bps / sampleRate) + paddingBit
    // coefficient = samplesPerFrame / 8
    internal const int FrameSizeCoeffMpeg1 = 144; // 1152 / 8
    internal const int FrameSizeCoeffMpeg2 = 72; //   576 / 8

    // -------------------------------------------------------------------------
    // ID3v2 tag layout (ID3v2.4 spec §3.1)
    // -------------------------------------------------------------------------
    internal const int Id3v2HeaderSize = 10; // 3 "ID3" + 2 version + 1 flags + 4 syncsafe size
    internal const byte Id3v2FooterFlag = 0x10; // bit 4 of flags byte: footer block present (+10 bytes)

    // -------------------------------------------------------------------------
    // Xing/Info header flags (big-endian uint32 at signature + 4)
    // -------------------------------------------------------------------------
    internal const uint XingFlagFrameCount = 0x01; // bit 0: frame count field present at signature + 8

    // -------------------------------------------------------------------------
    // Bitrate tables (kbps) for MPEG Layer III
    // Index 0 (free bitrate) and 15 (forbidden) are invalid, stored as 0.
    // -------------------------------------------------------------------------
    internal static readonly int[] Mpeg1L3Bitrate =
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

    internal static readonly int[] Mpeg2L3Bitrate =
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
    // version encoding: 0=MPEG 2.5, 1=reserved, 2=MPEG 2, 3=MPEG 1
    // srIdx 3 is reserved (invalid), stored as 0
    // -------------------------------------------------------------------------
    internal static readonly int[][] SampleRates =
    [
        [11025, 12000, 8000, 0], // 0 = MPEG 2.5
        [0, 0, 0, 0], //            1 = reserved
        [22050, 24000, 16000, 0], // 2 = MPEG 2
        [44100, 48000, 32000, 0], // 3 = MPEG 1
    ];

    // -------------------------------------------------------------------------
    // Xing/Info tag position within the first MPEG frame
    // Offset from frame start = 4-byte frame header + variable-length side_info block
    // -------------------------------------------------------------------------
    internal static int XingHeaderOffset(int version, int channelMode)
    {
        bool isMpeg1 = version == Mpeg1Version;
        bool isMono = channelMode == MonoChannelMode;
        return (isMpeg1, isMono) switch
        {
            (true, false) => 36, // MPEG-1 stereo/joint/dual: 4-byte header + 32-byte side info
            (true, true) => 21, //  MPEG-1 mono:              4-byte header + 17-byte side info
            (false, false) => 21, // MPEG-2/2.5 stereo:        4-byte header + 17-byte side info
            (false, true) => 13, //  MPEG-2/2.5 mono:          4-byte header +  9-byte side info
        };
    }
}
