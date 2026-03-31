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
        CheckCategory category,
        TimeSpan? timecode = null,
        long? frameIndex = null
    ) => new(true, message, timecode, frameIndex) { Category = category };

    public static CheckResult Error(
        string message,
        CheckCategory category,
        TimeSpan? timecode = null,
        long? frameIndex = null
    ) => new(false, message, timecode, frameIndex) { Category = category };

    public CheckCategory Category { get; init; } = CheckCategory.Ok;
}
