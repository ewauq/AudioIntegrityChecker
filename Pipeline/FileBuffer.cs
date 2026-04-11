using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using AudioIntegrityChecker.Core;

namespace AudioIntegrityChecker.Pipeline;

/// <summary>
/// Opaque handle over the in-memory contents of an audio file. The pipeline
/// loads each file once and hands the resulting buffer to the format checker,
/// which avoids the per-checker <c>File.ReadAllBytes</c> call and lets the
/// pipeline pick the backing store that best fits the underlying disk:
///
///   • <see cref="Load"/>  — managed <c>byte[]</c> pinned via <c>GCHandle</c>.
///     Used for HDDs and when the checker does not opt into mmap.
///   • <see cref="MemoryMap"/> — <see cref="MemoryMappedFile"/> + view
///     accessor. Used on SSDs and NVMe where the kernel paginates the view
///     lazily without any managed copy, so a 300 MB file costs nothing on
///     the managed heap.
///
/// Both modes expose the same API: a raw <see cref="Pointer"/> for native
/// interop and a <see cref="AsSpan"/> view for managed callers. The handle
/// is single-use: load, hand to checker, dispose immediately.
/// </summary>
internal sealed class FileBuffer : IDisposable
{
    // Exactly one of these two backing stores is populated per instance.
    private byte[]? _managedData;
    private GCHandle _pinHandle;

    private MemoryMappedFile? _mmapFile;
    private MemoryMappedViewAccessor? _mmapView;
    private bool _mmapPointerAcquired;

    private IntPtr _pointer;
    private int _length;

    public int Length => _length;

    /// <summary>
    /// Raw pointer to the first byte of the file contents. Stable for the
    /// lifetime of this <see cref="FileBuffer"/> (pinned for the managed
    /// backing, view base for the mmap backing). Do not use after
    /// <see cref="Dispose"/>.
    /// </summary>
    public IntPtr Pointer => _pointer;

    /// <summary>
    /// Read-only view of the contents. Works for both backing stores.
    /// </summary>
    public unsafe ReadOnlySpan<byte> AsSpan() => new((void*)_pointer, _length);

    /// <summary>
    /// Returns the contents as a managed <c>byte[]</c>. For the managed
    /// backing this is the exact underlying array (zero copy). For the mmap
    /// backing this allocates a new array and copies the view into it —
    /// callers that care about avoiding the copy should use <see cref="AsSpan"/>
    /// or <see cref="Pointer"/> directly.
    /// </summary>
    public byte[] AsArray()
    {
        if (_managedData is not null)
            return _managedData;

        var copy = new byte[_length];
        AsSpan().CopyTo(copy);
        return copy;
    }

    private FileBuffer(byte[] managedData)
    {
        _managedData = managedData;
        _length = managedData.Length;
        _pinHandle = GCHandle.Alloc(managedData, GCHandleType.Pinned);
        _pointer = _pinHandle.AddrOfPinnedObject();
    }

    private unsafe FileBuffer(MemoryMappedFile file, MemoryMappedViewAccessor view)
    {
        _mmapFile = file;
        _mmapView = view;
        _length = checked((int)view.Capacity);

        byte* ptr = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        _mmapPointerAcquired = true;
        _pointer = new IntPtr(ptr);
    }

    /// <summary>
    /// Loads the entire file into a pinned managed buffer. Throws the usual
    /// I/O exceptions on failure; callers are expected to translate them
    /// into a <see cref="CheckOutcome"/>.
    /// </summary>
    public static FileBuffer Load(string filePath) => new(File.ReadAllBytes(filePath));

    /// <summary>
    /// Opens the file as a read-only memory-mapped view. The whole file is
    /// mapped (size 0 = full length) but the kernel only materialises pages
    /// the checker actually touches.
    /// </summary>
    public static FileBuffer MemoryMap(string filePath)
    {
        MemoryMappedFile? file = null;
        MemoryMappedViewAccessor? view = null;
        try
        {
            file = MemoryMappedFile.CreateFromFile(
                filePath,
                FileMode.Open,
                mapName: null,
                capacity: 0,
                MemoryMappedFileAccess.Read
            );
            view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            return new FileBuffer(file, view);
        }
        catch
        {
            view?.Dispose();
            file?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_managedData is not null)
        {
            if (_pinHandle.IsAllocated)
                _pinHandle.Free();
            _managedData = null;
        }

        if (_mmapView is not null)
        {
            if (_mmapPointerAcquired)
            {
                _mmapView.SafeMemoryMappedViewHandle.ReleasePointer();
                _mmapPointerAcquired = false;
            }
            _mmapView.Dispose();
            _mmapView = null;
        }

        if (_mmapFile is not null)
        {
            _mmapFile.Dispose();
            _mmapFile = null;
        }

        _pointer = IntPtr.Zero;
        _length = 0;
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
    /// <summary>
    /// When <see langword="true"/>, the pipeline may hand over a
    /// <see cref="FileBuffer"/> backed by a memory-mapped view instead of a
    /// managed <c>byte[]</c>. The checker must access the contents via
    /// <see cref="FileBuffer.Pointer"/> or <see cref="FileBuffer.AsSpan"/> in
    /// that case — calling <see cref="FileBuffer.AsArray"/> on a mmap-backed
    /// buffer forces a full copy and defeats the purpose.
    /// </summary>
    bool SupportsMemoryMappedBuffer => false;

    CheckOutcome CheckWithBuffer(
        string filePath,
        FileBuffer buffer,
        CancellationToken cancellationToken,
        IProgress<FileProgress> progress
    );
}
