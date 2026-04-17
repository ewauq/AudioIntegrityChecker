using AudioIntegrityChecker.Pipeline;

namespace AudioIntegrityChecker.Core;

/// <summary>
/// A format-specific integrity checker. The pipeline loads each file into a
/// <see cref="FileBuffer"/> (managed byte[] on HDD, memory-mapped view on SSD)
/// and hands it to the checker, which avoids re-reading the file inside
/// individual passes.
/// </summary>
internal interface IFormatChecker
{
    string FormatId { get; }

    /// <summary>
    /// When <see langword="true"/>, the pipeline may hand over a
    /// memory-mapped <see cref="FileBuffer"/>. Checkers must then access the
    /// contents via <see cref="FileBuffer.AsSpan"/> or
    /// <see cref="FileBuffer.Pointer"/>.
    /// </summary>
    bool SupportsMemoryMappedBuffer { get; }

    CheckOutcome Check(
        FileBuffer buffer,
        CancellationToken cancellationToken,
        IProgress<FileProgress> progress
    );
}

public record CheckOutcome(CheckResult Result, TimeSpan? Duration);
