using AudioIntegrityChecker.Core;

namespace AudioIntegrityChecker.Pipeline;

/// <summary>
/// Dispatches files to IFormatChecker workers using a SemaphoreSlim-bounded task pool.
/// Concurrency is capped at min(CPU count, 8), optimal for CPU-bound FLAC decoding.
/// </summary>
public sealed class AnalysisPipeline
{
    private readonly int _workerCount;

    public event Action<string>? FileStarted;
    public event Action<FileCompletedEventArgs>? FileCompleted;
    public event Action<FileProgressEventArgs>? FileProgressChanged;

    public AnalysisPipeline()
    {
        _workerCount = Math.Min(Environment.ProcessorCount, 8);
    }

    internal async Task RunAsync(
        IReadOnlyList<FileEntry> entries,
        CancellationToken cancellationToken,
        PauseController? pauseController = null,
        IProgress<int>? globalProgress = null
    )
    {
        int completedCount = 0;
        using var semaphore = new SemaphoreSlim(_workerCount, _workerCount);
        var tasks = new List<Task>(entries.Count);

        foreach (var entry in entries)
        {
            if (pauseController is not null)
                await pauseController.WaitIfPausedAsync(cancellationToken).ConfigureAwait(false);

            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

            var capturedEntry = entry;
            tasks.Add(
                Task.Run(
                    () =>
                    {
                        FileStarted?.Invoke(capturedEntry.FilePath);
                        try
                        {
                            var outcome = CheckFile(capturedEntry, cancellationToken);
                            int count = Interlocked.Increment(ref completedCount);
                            globalProgress?.Report(count);
                            FileCompleted?.Invoke(
                                new FileCompletedEventArgs(
                                    capturedEntry.FilePath,
                                    capturedEntry.Format,
                                    outcome.Result,
                                    outcome.Duration
                                )
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

    private CheckOutcome CheckFile(FileEntry entry, CancellationToken cancellationToken)
    {
        var progress = new Progress<FileProgress>(fileProgress =>
            FileProgressChanged?.Invoke(new FileProgressEventArgs(entry.FilePath, fileProgress))
        );

        return entry.Checker.Check(entry.FilePath, cancellationToken, progress);
    }
}

public sealed class FileCompletedEventArgs : EventArgs
{
    public string FilePath { get; }
    public string Format { get; }
    public CheckResult Result { get; }
    public TimeSpan? Duration { get; }

    public FileCompletedEventArgs(
        string filePath,
        string format,
        CheckResult result,
        TimeSpan? duration
    )
    {
        FilePath = filePath;
        Format = format;
        Result = result;
        Duration = duration;
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
