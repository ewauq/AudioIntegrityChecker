using AudioIntegrityChecker.Core;

namespace AudioIntegrityChecker.Pipeline;

/// <summary>
/// Dispatches files to IFormatChecker workers using a SemaphoreSlim-bounded task pool.
/// Concurrency is capped at min(CPU count, 8) — optimal for CPU-bound FLAC decoding.
/// </summary>
public sealed class AnalysisPipeline
{
    private readonly CheckerRegistry _registry;
    private readonly int _workerCount;

    public event Action<FileCompletedEventArgs>? FileCompleted;
    public event Action<FileProgressEventArgs>? FileProgressChanged;

    public AnalysisPipeline(CheckerRegistry registry)
    {
        _registry = registry;
        _workerCount = Math.Min(Environment.ProcessorCount, 8);
    }

    public async Task RunAsync(
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken,
        IProgress<int>? globalProgress = null
    )
    {
        int completedCount = 0;
        using var semaphore = new SemaphoreSlim(_workerCount, _workerCount);
        var tasks = new List<Task>(filePaths.Count);

        foreach (var filePath in filePaths)
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

            var capturedPath = filePath;
            tasks.Add(
                Task.Run(
                    () =>
                    {
                        try
                        {
                            var (result, format) = CheckFile(capturedPath, cancellationToken);
                            int count = Interlocked.Increment(ref completedCount);
                            globalProgress?.Report(count);
                            FileCompleted?.Invoke(
                                new FileCompletedEventArgs(capturedPath, format, result)
                            );
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    },
                    cancellationToken
                )
            );
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private (CheckResult Result, string Format) CheckFile(
        string filePath,
        CancellationToken cancellationToken
    )
    {
        var checker = _registry.Resolve(filePath);
        if (checker is null)
        {
            // Should not happen — unsupported files are filtered out before queuing.
            var extension = Path.GetExtension(filePath).TrimStart('.');
            return (
                CheckResult.Error($"Unrecognized format: .{extension}", CheckCategory.Error),
                extension.ToUpperInvariant()
            );
        }

        var progress = new Progress<FileProgress>(fileProgress =>
            FileProgressChanged?.Invoke(new FileProgressEventArgs(filePath, fileProgress))
        );

        var result = checker.Check(filePath, cancellationToken, progress);
        return (result, checker.FormatId);
    }
}

public sealed class FileCompletedEventArgs : EventArgs
{
    public string FilePath { get; }
    public string Format { get; }
    public CheckResult Result { get; }

    public FileCompletedEventArgs(string filePath, string format, CheckResult result)
    {
        FilePath = filePath;
        Format = format;
        Result = result;
    }
}

public sealed class FileProgressEventArgs : EventArgs
{
    public string FilePath { get; }
    public FileProgress Progress { get; }

    public FileProgressEventArgs(string filePath, FileProgress progress)
    {
        FilePath = filePath;
        Progress = progress;
    }
}
