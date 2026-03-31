using AudioIntegrityChecker.Checkers.Flac;

namespace AudioIntegrityChecker.Pipeline;

/// <summary>
/// Scans a set of file/directory paths and returns <see cref="FileEntry"/> records
/// for all supported audio files found, filtering out unsupported formats.
/// Runs on a thread-pool thread — no UI access.
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
        var entries = new List<FileEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int counter = 0;

        void AddFile(string filePath)
        {
            if (!seen.Add(filePath))
                return;

            if (++counter % 50 == 0)
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

            var (totalSamples, sampleRate) = FlacMetadataReader.TryReadStreamInfo(filePath);
            TimeSpan? duration =
                totalSamples > 0 && sampleRate > 0
                    ? TimeSpan.FromSeconds((double)totalSamples / sampleRate)
                    : null;

            var directoryName = Path.GetFileName(Path.GetDirectoryName(filePath)) ?? string.Empty;
            entries.Add(
                new FileEntry(
                    filePath,
                    directoryName,
                    extension.ToUpperInvariant(),
                    bytes,
                    duration
                )
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

/// <summary>Snapshot of a queued audio file with its pre-read metadata.</summary>
internal record FileEntry(
    string FilePath,
    string DirectoryName,
    string Format,
    long Bytes,
    TimeSpan? Duration
);
