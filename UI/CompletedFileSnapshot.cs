using AudioIntegrityChecker.Core;

namespace AudioIntegrityChecker.UI;

internal sealed record CompletedFileSnapshot(
    string Path,
    string Format,
    TimeSpan? Duration,
    CheckResult Result
);
