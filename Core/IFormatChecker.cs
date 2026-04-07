namespace AudioIntegrityChecker.Core;

public interface IFormatChecker
{
    string FormatId { get; }
    CheckOutcome Check(string filePath, CancellationToken ct, IProgress<FileProgress> progress);
}

/// <summary>
/// Result of a format check plus any metadata extracted from the already-loaded file buffer.
/// Duration is computed opportunistically by the checker (which has the file in memory)
/// to avoid a second disk round-trip during the scan phase.
/// </summary>
public record CheckOutcome(CheckResult Result, TimeSpan? Duration);
