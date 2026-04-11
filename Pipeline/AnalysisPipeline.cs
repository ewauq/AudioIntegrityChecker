using System.Threading.Channels;
using AudioIntegrityChecker.Core;

namespace AudioIntegrityChecker.Pipeline;

/// <summary>
/// Dispatches files to IFormatChecker workers. Two I/O strategies are used
/// depending on the physical disk backing the queue:
///
///   • <c>Direct</c> (SSD/NVMe/Unknown, or mixed disks): each worker loads
///     its own <see cref="FileBuffer"/> before handing it to the checker.
///     Parallelism is bounded by a <see cref="SemaphoreSlim"/>, which is
///     also the channel <see cref="AdjustWorkerCount"/> uses to resize the
///     worker pool live.
///
///   • <c>Sequential reader</c> (homogeneous HDD scan): a single reader
///     task walks the queue in order and loads each file ahead of the
///     decoding workers, pushing them into a bounded
///     <see cref="Channel{T}"/>. N workers pull <see cref="LoadedFile"/>
///     items and run the decode on the pre-loaded buffer. The drive head
///     never seeks between concurrent readers, which is the source of the
///     big HDD win.
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
    /// additional semaphore slots immediately. Shrinking acquires surplus slots on
    /// a background task so the caller thread is not blocked while in-flight
    /// workers finish their current file. Only affects the direct strategy; the
    /// sequential reader strategy uses a fixed worker count per scan (changes
    /// apply at the next scan).
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
        if (entries.Count == 0)
            return;

        if (IsHomogeneousHddScan(entries))
            await RunHddStrategyAsync(entries, cancellationToken, pauseController, globalProgress)
                .ConfigureAwait(false);
        else
            await RunDirectStrategyAsync(
                    entries,
                    cancellationToken,
                    pauseController,
                    globalProgress
                )
                .ConfigureAwait(false);
    }

    /// <summary>
    /// True when every queued entry resolves to the same physical disk and
    /// that disk is classified as a mechanical HDD. Mixed scans (e.g. one
    /// folder on NVMe + one folder on HDD) fall back to the direct strategy
    /// since a single reader thread cannot serve two disks optimally.
    /// </summary>
    private static bool IsHomogeneousHddScan(IReadOnlyList<FileEntry> entries)
    {
        int disk = entries[0].PhysicalDiskNumber;
        if (disk < 0)
            return false;
        for (int i = 1; i < entries.Count; i++)
        {
            if (entries[i].PhysicalDiskNumber != disk)
                return false;
        }
        return StorageDetector.GetKindForDisk(disk) == StorageKind.Hdd;
    }

    private async Task RunDirectStrategyAsync(
        IReadOnlyList<FileEntry> entries,
        CancellationToken cancellationToken,
        PauseController? pauseController,
        IProgress<int>? globalProgress
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

    private async Task RunHddStrategyAsync(
        IReadOnlyList<FileEntry> entries,
        CancellationToken cancellationToken,
        PauseController? pauseController,
        IProgress<int>? globalProgress
    )
    {
        int completedCount = 0;
        int workerCount = _workerCount;

        // Bounded channel capacity: enough to keep every worker fed while
        // the reader is prefetching the next file, but small enough to bound
        // the RAM held by in-flight buffers.
        int capacity = Math.Max(2, workerCount);
        var channel = Channel.CreateBounded<LoadedFile>(
            new BoundedChannelOptions(capacity)
            {
                SingleWriter = true,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.Wait,
            }
        );

        var readerTask = Task.Run(
            async () =>
            {
                try
                {
                    foreach (var entry in entries)
                    {
                        if (pauseController is not null)
                            await pauseController
                                .WaitIfPausedAsync(cancellationToken)
                                .ConfigureAwait(false);

                        cancellationToken.ThrowIfCancellationRequested();

                        FileBuffer? buffer = null;
                        Exception? loadError = null;

                        // Only checkers that opt into IBufferedChecker benefit
                        // from prefetching; fallback checkers (e.g. ProcessFlac)
                        // still use their own legacy path and skip the load.
                        if (entry.Checker is IBufferedChecker)
                        {
                            try
                            {
                                buffer = FileBuffer.Load(entry.FilePath);
                            }
                            catch (Exception ex)
                            {
                                loadError = ex;
                            }
                        }

                        await channel
                            .Writer.WriteAsync(
                                new LoadedFile(entry, buffer, loadError),
                                cancellationToken
                            )
                            .ConfigureAwait(false);
                    }
                }
                finally
                {
                    channel.Writer.Complete();
                }
            },
            cancellationToken
        );

        var workerTasks = new List<Task>(workerCount);
        for (int i = 0; i < workerCount; i++)
        {
            workerTasks.Add(
                Task.Run(
                    async () =>
                    {
                        await foreach (
                            var loaded in channel
                                .Reader.ReadAllAsync(cancellationToken)
                                .ConfigureAwait(false)
                        )
                        {
                            FileStarted?.Invoke(loaded.Entry.FilePath);
                            CheckOutcome outcome;
                            try
                            {
                                outcome = CheckPreloadedFile(loaded, cancellationToken);
                            }
                            finally
                            {
                                loaded.Buffer?.Dispose();
                            }

                            int count = Interlocked.Increment(ref completedCount);
                            globalProgress?.Report(count);
                            FileCompleted?.Invoke(
                                new FileCompletedEventArgs(
                                    loaded.Entry.FilePath,
                                    loaded.Entry.Format,
                                    outcome.Result,
                                    outcome.Duration
                                )
                            );
                        }
                    },
                    cancellationToken
                )
            );
        }

        try
        {
            await readerTask.ConfigureAwait(false);
        }
        finally
        {
            await Task.WhenAll(workerTasks).ConfigureAwait(false);
        }
    }

    private CheckOutcome CheckPreloadedFile(LoadedFile loaded, CancellationToken cancellationToken)
    {
        if (loaded.LoadError is not null)
            return TranslateLoadError(loaded.LoadError);

        var progress = new Progress<FileProgress>(fp =>
            FileProgressChanged?.Invoke(new FileProgressEventArgs(loaded.Entry.FilePath, fp))
        );

        if (loaded.Buffer is not null && loaded.Entry.Checker is IBufferedChecker bufferedChecker)
            return bufferedChecker.CheckWithBuffer(
                loaded.Entry.FilePath,
                loaded.Buffer,
                cancellationToken,
                progress
            );

        // Non-buffered checker (e.g. ProcessFlacChecker): the reader left
        // Buffer null; the worker runs the legacy Check(string) path which
        // spawns the external tool itself.
        return loaded.Entry.Checker.Check(loaded.Entry.FilePath, cancellationToken, progress);
    }

    private CheckOutcome CheckFile(FileEntry entry, CancellationToken cancellationToken)
    {
        var progress = new Progress<FileProgress>(fileProgress =>
            FileProgressChanged?.Invoke(new FileProgressEventArgs(entry.FilePath, fileProgress))
        );

        // Checkers that opt into IBufferedChecker get the file loaded once by
        // the pipeline. For the direct strategy this still runs inside the
        // worker, which is fine on SSD/NVMe because random reads are free.
        if (entry.Checker is IBufferedChecker bufferedChecker)
        {
            FileBuffer buffer;
            try
            {
                buffer = FileBuffer.Load(entry.FilePath);
            }
            catch (Exception ex)
            {
                return TranslateLoadError(ex);
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

    private static CheckOutcome TranslateLoadError(Exception ex) =>
        ex switch
        {
            FileNotFoundException => new CheckOutcome(
                CheckResult.Error("File not found.", CheckCategory.Error),
                null
            ),
            OutOfMemoryException => new CheckOutcome(
                CheckResult.Error("File too large to load into memory.", CheckCategory.Error),
                null
            ),
            _ => new CheckOutcome(
                CheckResult.Error($"Cannot read file: {ex.Message}", CheckCategory.Error),
                null
            ),
        };

    /// <summary>
    /// Item handed from the HDD reader task to the decoding workers. Either
    /// <see cref="Buffer"/> is set (successful load), <see cref="LoadError"/>
    /// is set (load failure, worker will translate it), or both are null
    /// (non-buffered checker, worker falls back to Check(string)).
    /// </summary>
    private sealed record LoadedFile(FileEntry Entry, FileBuffer? Buffer, Exception? LoadError);
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
