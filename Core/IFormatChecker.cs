namespace AudioIntegrityChecker.Core;

public interface IFormatChecker
{
    string FormatId { get; }
    CheckResult Check(string filePath, CancellationToken ct, IProgress<FileProgress> progress);
}
