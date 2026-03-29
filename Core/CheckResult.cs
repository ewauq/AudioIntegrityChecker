namespace AudioIntegrityChecker.Core;

public record CheckResult(
    bool IsValid,
    string? ErrorMessage,
    TimeSpan? ErrorTimecode,
    long? ErrorFrameIndex
)
{
    public static CheckResult Ok() => new(true, null, null, null);

    public static CheckResult Warning(
        string message,
        TimeSpan? timecode = null,
        long? frameIndex = null
    ) => new(true, message, timecode, frameIndex) { IsWarning = true };

    public static CheckResult Error(
        string message,
        TimeSpan? timecode = null,
        long? frameIndex = null
    ) => new(false, message, timecode, frameIndex);

    public static CheckResult Skipped(string reason) =>
        new(true, reason, null, null) { IsSkipped = true };

    public bool IsWarning { get; private init; }
    public bool IsSkipped { get; private init; }
}
