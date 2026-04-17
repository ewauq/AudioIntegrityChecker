using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace AudioIntegrityChecker.Pipeline;

/// <summary>
/// Opaque handle over the in-memory contents of an audio file. Two backing
/// modes are supported so the pipeline can pick the one that fits the disk:
/// <see cref="Load"/> for a pinned managed <c>byte[]</c> (used on HDD),
/// <see cref="MemoryMap"/> for a read-only view over the file
/// (used on SSD/NVMe to skip the managed copy).
/// Both expose the same <see cref="Pointer"/> / <see cref="AsSpan"/> API.
/// The handle is single-use: load, hand to checker, dispose.
/// </summary>
internal sealed class FileBuffer : IDisposable
{
    private byte[]? _managedData;
    private GCHandle _pinHandle;

    private MemoryMappedFile? _mmapFile;
    private MemoryMappedViewAccessor? _mmapView;
    private bool _mmapPointerAcquired;

    private IntPtr _pointer;
    private int _length;

    public int Length => _length;

    public IntPtr Pointer => _pointer;

    public unsafe ReadOnlySpan<byte> AsSpan() => new((void*)_pointer, _length);

    private FileBuffer(byte[] managedData)
    {
        _managedData = managedData;
        _length = managedData.Length;
        _pinHandle = GCHandle.Alloc(managedData, GCHandleType.Pinned);
        _pointer = _pinHandle.AddrOfPinnedObject();
    }

    private unsafe FileBuffer(MemoryMappedFile file, MemoryMappedViewAccessor view, int length)
    {
        _mmapFile = file;
        _mmapView = view;
        _length = length;

        byte* ptr = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        _mmapPointerAcquired = true;
        _pointer = new IntPtr(ptr);
    }

    public static FileBuffer Load(string filePath) => new(File.ReadAllBytes(filePath));

    public static FileBuffer MemoryMap(string filePath)
    {
        // view.Capacity is rounded up to the OS page size, so bytes past the
        // real file end are accessible as zeros. Using it as the buffer length
        // lets checkers read that padding and misdetect trailing garbage, so
        // we capture the actual file size up front instead.
        long fileSize = new FileInfo(filePath).Length;

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
            return new FileBuffer(file, view, checked((int)fileSize));
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
