using AudioIntegrityChecker.Core;

namespace AudioIntegrityChecker.Pipeline;

/// <summary>
/// Scans a set of file/directory paths and returns <see cref="FileEntry"/> records
/// for all supported audio files found, filtering out unsupported formats.
/// Runs on a thread-pool thread, no UI access.
///
/// Duration is deliberately NOT read here: opening each file a second time just to
/// peek at metadata is extremely expensive on spinning disks. The duration is
/// instead extracted by the checker during analysis, when the file is already
/// loaded into memory.
/// </summary>
internal static class FileCollector
{
    // Enumerate once per extension so NTFS filters unsupported files kernel-side,
    // instead of streaming every entry back to us for a managed-side rejection.
    // BufferSize = 64 KB lets FindFirstFile batch many entries per syscall on
    // large directories (default is 4 KB).
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
        ScanContext context,
        CancellationToken cancellationToken,
        IProgress<int>? scanProgress = null
    )
    {
        const int ScanProgressInterval = 50;

        var entries = new List<FileEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int counter = 0;

        // Flatten the extension → checker mapping into parallel arrays so the
        // hot walk loop reads them by index instead of hashing strings.
        int extCount = context.SupportedExtensions.Count;
        var patterns = new string[extCount];
        var labels = new string[extCount];
        var checkers = new IFormatChecker[extCount];
        int idx = 0;
        foreach (var ext in context.SupportedExtensions)
        {
            patterns[idx] = "*." + ext;
            labels[idx] = ext.ToUpperInvariant();
            checkers[idx] = context.CheckersByExtension[ext];
            idx++;
        }

        void ReportProgress()
        {
            if (++counter % ScanProgressInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanProgress?.Report(entries.Count);
            }
        }

        void Walk(DirectoryInfo dir, string directoryName, int physicalDiskNumber)
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
                    if (!seen.Add(fi.FullName))
                        continue;

                    ReportProgress();
                    entries.Add(
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
                Walk(sub, sub.Name, physicalDiskNumber);
            }
        }

        foreach (var rootPath in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Directory.Exists(rootPath))
            {
                var rootDir = new DirectoryInfo(rootPath);
                int physicalDisk = StorageDetector.GetPhysicalDiskNumber(rootPath);
                Walk(rootDir, rootDir.Name, physicalDisk);
            }
            else if (File.Exists(rootPath))
            {
                int extIndex = FindExtensionIndex(rootPath, patterns);
                if (extIndex < 0)
                    continue;

                if (!seen.Add(rootPath))
                    continue;

                ReportProgress();

                FileInfo fi;
                try
                {
                    fi = new FileInfo(rootPath);
                }
                catch
                {
                    continue;
                }

                var directoryName = fi.DirectoryName is { } dirPath
                    ? Path.GetFileName(dirPath) ?? string.Empty
                    : string.Empty;
                int physicalDisk = StorageDetector.GetPhysicalDiskNumber(rootPath);

                entries.Add(
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
        }

        return entries;
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
