namespace AudioIntegrityChecker.Checkers.Flac;

/// <summary>
/// Reads FLAC STREAMINFO without a full decode.
/// Parses the first 42 bytes of the file (fLaC marker + STREAMINFO block header + data).
/// </summary>
public static class FlacMetadataReader
{
    private const int MinStreamInfoBytes = 42;

    private const int StreamInfoBlockOffset = 8; // 4 (fLaC marker) + 4 (block header)
    private const int SampleRateOffset = 10;
    private const int TotalSamplesOffset = 13;

    public static (ulong TotalSamples, uint SampleRate) TryReadStreamInfo(string filePath)
    {
        try
        {
            Span<byte> buffer = stackalloc byte[MinStreamInfoBytes];
            using var fileStream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64
            );

            if (fileStream.Read(buffer) < MinStreamInfoBytes)
                return default;

            return ParseStreamInfo(buffer);
        }
        catch
        {
            return default;
        }
    }

    public static (ulong TotalSamples, uint SampleRate) TryReadStreamInfo(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < MinStreamInfoBytes)
            return default;

        return ParseStreamInfo(buffer);
    }

    private static (ulong TotalSamples, uint SampleRate) ParseStreamInfo(ReadOnlySpan<byte> buffer)
    {
        if (buffer[0] != 0x66 || buffer[1] != 0x4C || buffer[2] != 0x61 || buffer[3] != 0x43)
            return default;

        // First metadata block must be STREAMINFO (type 0)
        if ((buffer[4] & 0x7F) != 0)
            return default;

        int streamInfoOffset = StreamInfoBlockOffset;

        // sample_rate: 20 bits packed across bytes at offset +10, +11, +12
        uint sampleRate =
            ((uint)buffer[streamInfoOffset + SampleRateOffset] << 12)
            | ((uint)buffer[streamInfoOffset + SampleRateOffset + 1] << 4)
            | ((uint)buffer[streamInfoOffset + SampleRateOffset + 2] >> 4);

        // total_samples: 36 bits packed, low nibble of byte +13 is bits [35:32]
        ulong totalSamples =
            ((ulong)(buffer[streamInfoOffset + TotalSamplesOffset] & 0x0F) << 32)
            | ((ulong)buffer[streamInfoOffset + TotalSamplesOffset + 1] << 24)
            | ((ulong)buffer[streamInfoOffset + TotalSamplesOffset + 2] << 16)
            | ((ulong)buffer[streamInfoOffset + TotalSamplesOffset + 3] << 8)
            | (ulong)buffer[streamInfoOffset + TotalSamplesOffset + 4];

        if (sampleRate == 0 || totalSamples == 0)
            return default;

        return (totalSamples, sampleRate);
    }
}
