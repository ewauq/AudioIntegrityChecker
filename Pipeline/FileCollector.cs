using System.Collections.Concurrent;
using AudioIntegrityChecker.Core;

namespace AudioIntegrityChecker.Pipeline;

/// <summary>
/// Walks the file system under a set of roots and returns
/// <see cref="FileEntry"/> records for every supported audio file found.
/// Extension filtering happens kernel-side via <c>FindFirstFileEx</c>.
/// Roots are grouped by physical disk and walked in parallel across groups;
/// inside a disk group, the walk is sequential. HDD groups are sorted by
/// file path after collection so the analysis phase follows the MFT
/// allocation order, which is close to the physical layout.
/// </summary>
internal static class FileCollector
{
    // BufferSize = 64 KB lets FindFirstFile batch many entries per syscall
    // on large directories (default is 4 KB).
    private static readonly EnumerationOptions s_directOnlyOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.System | FileAttributes.Hidden,
        BufferSize = 65536,
        MatchType = MatchType.Win32,
    };

    internal static List<FileEntry> Collect(
        string[] paths,
        IReadOnlyDictionary<string, IFormatChecker> checkersByExtension,
        CancellationToken cancellationToken,
        IProgress<int>? scanProgress = null
    )
    {
        const int ScanProgressInterval = 50;

        // Flatten the extension → checker mapping into parallel arrays so
        // the hot walk loop reads them by index instead of hashing strings.
        int extCount = checkersByExtension.Count;
        var patterns = new string[extCount];
        var labels = new string[extCount];
        var checkers = new IFormatChecker[extCount];
        int idx = 0;
        foreach (var (ext, checker) in checkersByExtension)
        {
            patterns[idx] = "*." + ext;
            labels[idx] = ext.ToUpperInvariant();
            checkers[idx] = checker;
            idx++;
        }

        // Split the incoming paths into disk groups and standalone files. A
        // disk number of -1 means the detector could not resolve the volume;
        // those roots are still grouped together so the walk runs in one shot.
        var rootsByDisk = new Dictionary<int, List<string>>();
        var singleFiles = new List<string>();
        foreach (var rootPath in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(rootPath))
            {
                int disk = StorageDetector.GetPhysicalDiskNumber(rootPath);
                if (!rootsByDisk.TryGetValue(disk, out var list))
                {
                    list = new List<string>();
                    rootsByDisk[disk] = list;
                }
                list.Add(rootPath);
            }
            else if (File.Exists(rootPath))
            {
                singleFiles.Add(rootPath);
            }
        }

        var seen = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var entriesByDisk = new ConcurrentDictionary<int, List<FileEntry>>();
        int globalCounter = 0;

        void Walk(
            DirectoryInfo dir,
            string directoryName,
            int physicalDiskNumber,
            List<FileEntry> bucket
        )
        {
            for (int i = 0; i < extCount; i++)
            {
                IEnumerable<FileInfo> matches;
                try
                {
                    matches = dir.EnumerateFiles(patterns[i], s_directOnlyOptions);
                }
                catch
                {
                    continue;
                }

                var label = labels[i];
                var checker = checkers[i];

                foreach (var fi in matches)
                {
                    if (!seen.TryAdd(fi.FullName, 0))
                        continue;

                    int local = Interlocked.Increment(ref globalCounter);
                    if (local % ScanProgressInterval == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        scanProgress?.Report(local);
                    }

                    bucket.Add(
                        new FileEntry(
                            fi.FullName,
                            directoryName,
                            label,
                            fi.Length,
                            physicalDiskNumber,
                            checker
                        )
                    );
                }
            }

            IEnumerable<DirectoryInfo> subdirs;
            try
            {
                subdirs = dir.EnumerateDirectories("*", s_directOnlyOptions);
            }
            catch
            {
                return;
            }

            foreach (var sub in subdirs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Walk(sub, sub.Name, physicalDiskNumber, bucket);
            }
        }

        // Walk disk groups in parallel, one worker per distinct physical disk.
        // Intra-disk walking stays sequential: a HDD is single-threaded to
        // avoid head thrashing, and NVMe intra-disk parallelism is deferred.
        if (rootsByDisk.Count > 0)
        {
            var parallelOptions = new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Max(1, rootsByDisk.Count),
            };

            Parallel.ForEach(
                rootsByDisk,
                parallelOptions,
                kvp =>
                {
                    var bucket = new List<FileEntry>();
                    foreach (var rootPath in kvp.Value)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var rootDir = new DirectoryInfo(rootPath);
                        Walk(rootDir, rootDir.Name, kvp.Key, bucket);
                    }

                    // P_B.10: on HDD, sort by path so analysis follows the
                    // MFT allocation order (roughly the physical layout) and
                    // the drive head seeks monotonically. Skipped on SSD and
                    // NVMe where seek time is negligible.
                    if (StorageDetector.GetKindForDisk(kvp.Key) == StorageKind.Hdd)
                        bucket.Sort(
                            (a, b) =>
                                string.Compare(
                                    a.FilePath,
                                    b.FilePath,
                                    StringComparison.OrdinalIgnoreCase
                                )
                        );

                    entriesByDisk[kvp.Key] = bucket;
                }
            );
        }

        // Standalone files: sequential, trivial cost.
        var singleEntries = new List<FileEntry>();
        foreach (var filePath in singleFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int extIndex = FindExtensionIndex(filePath, patterns);
            if (extIndex < 0)
                continue;

            if (!seen.TryAdd(filePath, 0))
                continue;

            int local = Interlocked.Increment(ref globalCounter);
            if (local % ScanProgressInterval == 0)
                scanProgress?.Report(local);

            FileInfo fi;
            try
            {
                fi = new FileInfo(filePath);
            }
            catch
            {
                continue;
            }

            var directoryName = fi.DirectoryName is { } dirPath
                ? Path.GetFileName(dirPath) ?? string.Empty
                : string.Empty;
            int physicalDisk = StorageDetector.GetPhysicalDiskNumber(filePath);

            singleEntries.Add(
                new FileEntry(
                    fi.FullName,
                    directoryName,
                    labels[extIndex],
                    fi.Length,
                    physicalDisk,
                    checkers[extIndex]
                )
            );
        }

        // Aggregate disk buckets in deterministic order (ascending disk number),
        // followed by any standalone files.
        int totalCount = singleEntries.Count;
        foreach (var bucket in entriesByDisk.Values)
            totalCount += bucket.Count;

        var result = new List<FileEntry>(totalCount);
        foreach (var disk in entriesByDisk.Keys.OrderBy(k => k))
            result.AddRange(entriesByDisk[disk]);
        result.AddRange(singleEntries);

        return result;
    }

    private static int FindExtensionIndex(string filePath, string[] patterns)
    {
        var ext = Path.GetExtension(filePath.AsSpan());
        if (ext.IsEmpty)
            return -1;
        // ext starts with a dot, e.g. ".flac"; match against "*.flac" suffix.
        for (int i = 0; i < patterns.Length; i++)
        {
            var p = patterns[i].AsSpan(1); // skip leading '*'
            if (ext.Equals(p, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }
}

/// <summary>Snapshot of a queued audio file. Duration is populated later by the checker.</summary>
internal sealed record FileEntry(
    string FilePath,
    string DirectoryName,
    string Format,
    long Bytes,
    int PhysicalDiskNumber,
    IFormatChecker Checker
);
