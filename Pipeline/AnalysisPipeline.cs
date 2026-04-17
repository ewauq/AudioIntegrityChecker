using System.Threading.Channels;
using AudioIntegrityChecker.Core;

namespace AudioIntegrityChecker.Pipeline;

/// <summary>
/// Dispatches files to <see cref="IFormatChecker"/> workers. Two I/O
/// strategies are used depending on the physical disk backing the queue:
///   • Direct (SSD/NVMe, mixed disks, or unknown): each worker loads its own
///     <see cref="FileBuffer"/> before handing it to the checker. Parallelism
///     is bounded by a <see cref="SemaphoreSlim"/>.
///   • Sequential reader (homogeneous HDD): a single reader task walks the
///     queue in order and loads each file ahead of the decoding workers via
///     a bounded <see cref="Channel{T}"/>. The drive head never seeks between
///     concurrent readers, which is the source of the HDD speedup.
/// </summary>
public sealed class AnalysisPipeline
{
    private readonly int _workerCount;

    public int WorkerCount => _workerCount;

    public event Action<string>? FileStarted;
    public event Action<FileCompletedEventArgs>? FileCompleted;
    public event Action<FileProgressEventArgs>? FileProgressChanged;

    public AnalysisPipeline(int workerCount)
    {
        _workerCount = Math.Clamp(workerCount, 1, Environment.ProcessorCount);
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

    // True when every entry maps to the same HDD. Mixed scans (e.g. one
    // folder on NVMe + one folder on HDD) fall through to the direct strategy.
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

    private async Task RunHddStrategyAsync(
        IReadOnlyList<FileEntry> entries,
        CancellationToken cancellationToken,
        PauseController? pauseController,
        IProgress<int>? globalProgress
    )
    {
        int completedCount = 0;
        int workerCount = _workerCount;

        // Bounded to keep RAM pressure low while still preloading ahead of the workers.
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
                        try
                        {
                            buffer = FileBuffer.Load(entry.FilePath);
                        }
                        catch (Exception ex)
                        {
                            loadError = ex;
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
                                outcome = RunChecker(loaded, cancellationToken);
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

    private CheckOutcome RunChecker(LoadedFile loaded, CancellationToken cancellationToken)
    {
        if (loaded.LoadError is not null || loaded.Buffer is null)
            return TranslateLoadError(loaded.LoadError);

        var progress = new Progress<FileProgress>(fp =>
            FileProgressChanged?.Invoke(new FileProgressEventArgs(loaded.Entry.FilePath, fp))
        );

        return loaded.Entry.Checker.Check(loaded.Buffer, cancellationToken, progress);
    }

    private CheckOutcome CheckFile(FileEntry entry, CancellationToken cancellationToken)
    {
        var progress = new Progress<FileProgress>(fileProgress =>
            FileProgressChanged?.Invoke(new FileProgressEventArgs(entry.FilePath, fileProgress))
        );

        FileBuffer buffer;
        try
        {
            buffer = LoadBuffer(entry);
        }
        catch (Exception ex)
        {
            return TranslateLoadError(ex);
        }

        try
        {
            return entry.Checker.Check(buffer, cancellationToken, progress);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    private static FileBuffer LoadBuffer(FileEntry entry)
    {
        if (entry.Checker.SupportsMemoryMappedBuffer && IsMappableDisk(entry.PhysicalDiskNumber))
        {
            try
            {
                return FileBuffer.MemoryMap(entry.FilePath);
            }
            catch
            {
                // Fall back to managed load if the OS refuses the mapping
                // (e.g. empty file, permission quirks).
            }
        }
        return FileBuffer.Load(entry.FilePath);
    }

    private static bool IsMappableDisk(int physicalDiskNumber)
    {
        var kind = StorageDetector.GetKindForDisk(physicalDiskNumber);
        return kind == StorageKind.SataSsd || kind == StorageKind.Nvme;
    }

    private static CheckOutcome TranslateLoadError(Exception? ex) =>
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
            null => new CheckOutcome(
                CheckResult.Error("Unknown load failure.", CheckCategory.Error),
                null
            ),
            _ => new CheckOutcome(
                CheckResult.Error($"Cannot read file: {ex.Message}", CheckCategory.Error),
                null
            ),
        };

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
