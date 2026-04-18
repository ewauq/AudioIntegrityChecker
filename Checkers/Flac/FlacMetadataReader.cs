namespace AudioIntegrityChecker.Checkers.Flac;

/// <summary>
/// Parses FLAC STREAMINFO from the first metadata block. Tolerates an
/// ID3v2 tag prepended to the file (non-standard for FLAC but common in
/// the wild) by skipping past it before looking for the fLaC marker.
/// </summary>
public static class FlacMetadataReader
{
    private const int StreamInfoPayloadSize = 34; // fixed by the FLAC spec
    private const int SampleRateOffset = 10;
    private const int TotalSamplesOffset = 13;

    public static (ulong TotalSamples, uint SampleRate) TryReadStreamInfo(ReadOnlySpan<byte> buffer)
    {
        int start = SkipId3v2(buffer);

        // Need fLaC (4) + block header (4) + STREAMINFO payload (34) = 42 bytes
        if (buffer.Length - start < 4 + 4 + StreamInfoPayloadSize)
            return default;

        if (
            buffer[start] != 0x66
            || buffer[start + 1] != 0x4C
            || buffer[start + 2] != 0x61
            || buffer[start + 3] != 0x43
        )
            return default;

        int blockHeader = start + 4;
        // First metadata block must be STREAMINFO (type 0, low 7 bits of the header byte)
        if ((buffer[blockHeader] & 0x7F) != 0)
            return default;

        int streamInfo = blockHeader + 4;

        // sample_rate: 20 bits packed across bytes at offset +10, +11, +12
        uint sampleRate =
            ((uint)buffer[streamInfo + SampleRateOffset] << 12)
            | ((uint)buffer[streamInfo + SampleRateOffset + 1] << 4)
            | ((uint)buffer[streamInfo + SampleRateOffset + 2] >> 4);

        // total_samples: 36 bits packed, low nibble of byte +13 is bits [35:32]
        ulong totalSamples =
            ((ulong)(buffer[streamInfo + TotalSamplesOffset] & 0x0F) << 32)
            | ((ulong)buffer[streamInfo + TotalSamplesOffset + 1] << 24)
            | ((ulong)buffer[streamInfo + TotalSamplesOffset + 2] << 16)
            | ((ulong)buffer[streamInfo + TotalSamplesOffset + 3] << 8)
            | (ulong)buffer[streamInfo + TotalSamplesOffset + 4];

        if (sampleRate == 0 || totalSamples == 0)
            return default;

        return (totalSamples, sampleRate);
    }

    // Returns the byte offset at which FLAC data begins. Zero if no ID3v2
    // tag is prepended, otherwise 10 + synch-safe size (+10 if a footer is
    // declared, v2.4 only).
    private static int SkipId3v2(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 10 || buffer[0] != 0x49 || buffer[1] != 0x44 || buffer[2] != 0x33)
            return 0;

        // Size is a synch-safe integer: 7 bits per byte, MSB must be zero
        if ((buffer[6] | buffer[7] | buffer[8] | buffer[9]) > 0x7F)
            return 0;

        int size = (buffer[6] << 21) | (buffer[7] << 14) | (buffer[8] << 7) | buffer[9];
        int total = 10 + size;
        if ((buffer[5] & 0x10) != 0) // footer present (v2.4)
            total += 10;

        return total <= buffer.Length ? total : 0;
    }
}
