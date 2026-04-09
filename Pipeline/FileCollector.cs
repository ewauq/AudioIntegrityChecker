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
    internal static List<FileEntry> Collect(
        string[] paths,
        HashSet<string> supportedExtensions,
        CancellationToken cancellationToken,
        IProgress<int>? scanProgress = null
    )
    {
        const int ScanProgressInterval = 50; // check cancellation and report progress every N files scanned

        var entries = new List<FileEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int counter = 0;

        void AddFile(string filePath)
        {
            if (!seen.Add(filePath))
                return;

            if (++counter % ScanProgressInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanProgress?.Report(entries.Count);
            }

            var extension = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
            if (!supportedExtensions.Contains(extension))
                return;

            long bytes = 0;
            try
            {
                bytes = new FileInfo(filePath).Length;
            }
            catch { }

            var directoryName = Path.GetFileName(Path.GetDirectoryName(filePath)) ?? string.Empty;
            entries.Add(
                new FileEntry(filePath, directoryName, extension.ToUpperInvariant(), bytes)
            );
        }

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(path))
            {
                foreach (
                    var filePath in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                )
                    AddFile(filePath);
            }
            else if (File.Exists(path))
            {
                AddFile(path);
            }
        }

        return entries;
    }
}

/// <summary>Snapshot of a queued audio file. Duration is populated later by the checker.</summary>
internal record FileEntry(string FilePath, string DirectoryName, string Format, long Bytes);
