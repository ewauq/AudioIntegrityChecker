using AudioIntegrityChecker.Core;

namespace AudioIntegrityChecker.Pipeline;

/// <summary>
/// Dispatches files to IFormatChecker workers using a SemaphoreSlim-bounded task pool.
/// </summary>
public sealed class AnalysisPipeline
{
    // Absolute ceiling for the semaphore. The live AdjustWorkerCount path can
    // never exceed this, so we pick the host CPU count as a safe upper bound.
    private static readonly int MaxWorkerCount = Math.Max(1, Environment.ProcessorCount);

    private int _workerCount;
    private SemaphoreSlim? _semaphore;
    private readonly object _adjustLock = new();

    public int WorkerCount => _workerCount;

    public event Action<string>? FileStarted;
    public event Action<FileCompletedEventArgs>? FileCompleted;
    public event Action<FileProgressEventArgs>? FileProgressChanged;

    public AnalysisPipeline()
        : this(Math.Min(Environment.ProcessorCount, 8)) { }

    public AnalysisPipeline(int workerCount)
    {
        _workerCount = Math.Clamp(workerCount, 1, MaxWorkerCount);
    }

    /// <summary>
    /// Adjusts the active worker count while a scan is running. Expanding releases
    /// additional slots immediately. Shrinking acquires surplus slots on a
    /// background task so the caller thread is not blocked while in-flight
    /// workers finish their current file.
    /// </summary>
    public void AdjustWorkerCount(int newCount)
    {
        newCount = Math.Clamp(newCount, 1, MaxWorkerCount);

        lock (_adjustLock)
        {
            var semaphore = _semaphore;
            if (semaphore is null)
            {
                _workerCount = newCount;
                return;
            }

            int diff = newCount - _workerCount;
            if (diff == 0)
                return;

            _workerCount = newCount;

            if (diff > 0)
            {
                semaphore.Release(diff);
            }
            else
            {
                int toAcquire = -diff;
                _ = Task.Run(() =>
                {
                    for (int i = 0; i < toAcquire; i++)
                    {
                        try
                        {
                            semaphore.Wait();
                        }
                        catch (ObjectDisposedException)
                        {
                            return;
                        }
                    }
                });
            }
        }
    }

    internal async Task RunAsync(
        IReadOnlyList<FileEntry> entries,
        CancellationToken cancellationToken,
        PauseController? pauseController = null,
        IProgress<int>? globalProgress = null
    )
    {
        int completedCount = 0;
        var semaphore = new SemaphoreSlim(_workerCount, MaxWorkerCount);
        lock (_adjustLock)
            _semaphore = semaphore;

        try
        {
            var tasks = new List<Task>(entries.Count);

            foreach (var entry in entries)
            {
                if (pauseController is not null)
                    await pauseController
                        .WaitIfPausedAsync(cancellationToken)
                        .ConfigureAwait(false);

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
        finally
        {
            lock (_adjustLock)
                _semaphore = null;
            semaphore.Dispose();
        }
    }

    private CheckOutcome CheckFile(FileEntry entry, CancellationToken cancellationToken)
    {
        var progress = new Progress<FileProgress>(fileProgress =>
            FileProgressChanged?.Invoke(new FileProgressEventArgs(entry.FilePath, fileProgress))
        );

        // Checkers that opt into IBufferedChecker get the file loaded once by
        // the pipeline. This will matter in later phases when we swap the
        // backing store for a memory-mapped file or a sequential reader; for
        // now it already avoids duplicating the File.ReadAllBytes call inside
        // each checker.
        if (entry.Checker is IBufferedChecker bufferedChecker)
        {
            FileBuffer buffer;
            try
            {
                buffer = FileBuffer.Load(entry.FilePath);
            }
            catch (FileNotFoundException)
            {
                return new CheckOutcome(
                    CheckResult.Error("File not found.", CheckCategory.Error),
                    null
                );
            }
            catch (OutOfMemoryException)
            {
                return new CheckOutcome(
                    CheckResult.Error("File too large to load into memory.", CheckCategory.Error),
                    null
                );
            }
            catch (Exception ex)
            {
                return new CheckOutcome(
                    CheckResult.Error($"Cannot read file: {ex.Message}", CheckCategory.Error),
                    null
                );
            }

            try
            {
                return bufferedChecker.CheckWithBuffer(
                    entry.FilePath,
                    buffer,
                    cancellationToken,
                    progress
                );
            }
            finally
            {
                buffer.Dispose();
            }
        }

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
