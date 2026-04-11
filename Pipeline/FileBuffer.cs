using AudioIntegrityChecker.Core;

namespace AudioIntegrityChecker.Pipeline;

/// <summary>
/// Opaque handle over the in-memory contents of an audio file. The pipeline
/// loads each file once and hands the resulting buffer to the format checker,
/// which avoids the current per-checker <c>File.ReadAllBytes</c> call. The
/// abstraction is deliberately minimal so subsequent phases can swap in a
/// memory-mapped backing store (P_B.4) or a pre-fetched reader queue (P_B.3)
/// without touching the checkers themselves.
///
/// Instances are single-use: a buffer is loaded immediately before the checker
/// runs and disposed immediately after. The underlying storage is released on
/// <see cref="Dispose"/>.
/// </summary>
internal sealed class FileBuffer : IDisposable
{
    private byte[]? _data;

    public int Length { get; }

    private FileBuffer(byte[] data)
    {
        _data = data;
        Length = data.Length;
    }

    /// <summary>
    /// Returns the underlying managed array. The buffer remains valid until
    /// <see cref="Dispose"/> is called. Throws <see cref="ObjectDisposedException"/>
    /// if accessed after disposal.
    /// </summary>
    public byte[] AsArray() => _data ?? throw new ObjectDisposedException(nameof(FileBuffer));

    public ReadOnlySpan<byte> AsSpan() =>
        _data ?? throw new ObjectDisposedException(nameof(FileBuffer));

    /// <summary>
    /// Loads the entire file into a managed buffer. Throws the usual I/O
    /// exceptions on failure; callers are expected to translate them into a
    /// <c>CheckOutcome</c>.
    /// </summary>
    public static FileBuffer Load(string filePath) => new(File.ReadAllBytes(filePath));

    public void Dispose()
    {
        _data = null;
    }
}

/// <summary>
/// Optional capability implemented by checkers that can operate on a
/// pre-loaded in-memory buffer instead of reading the file themselves. The
/// analysis pipeline detects this at runtime and provides the buffer so the
/// file is read exactly once per scan, regardless of how many passes the
/// checker performs internally.
/// </summary>
internal interface IBufferedChecker : IFormatChecker
{
    CheckOutcome CheckWithBuffer(
        string filePath,
        FileBuffer buffer,
        CancellationToken cancellationToken,
        IProgress<FileProgress> progress
    );
}
