using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AudioIntegrityChecker.Checkers.Mp3;

[SupportedOSPlatform("windows")]
internal static class Mp3Mpg123Backend
{
    // 1152 samples/frame × 2 channels × 4 bytes/sample (float32) = 9216 bytes
    private const int MaxDecodedFrameBytes = 9216;

    private static bool? _libraryAvailable;
    private static bool _initialized;
    private static readonly object _initLock = new();

    internal static bool IsLibraryAvailable()
    {
        if (_libraryAvailable.HasValue)
            return _libraryAvailable.Value;

        if (NativeLibrary.TryLoad("mpg123.dll", out var handle))
        {
            NativeLibrary.Free(handle);
            _libraryAvailable = true;
        }
        else
        {
            _libraryAvailable = false;
        }

        return _libraryAvailable.Value;
    }

    /// <summary>
    /// Calls mpg123_init() exactly once across all threads.
    /// Safe to call from parallel workers. Subsequent calls return the cached result.
    /// </summary>
    internal static bool TryInitialize()
    {
        if (_initialized)
            return true;

        lock (_initLock)
        {
            if (_initialized)
                return true;

            try
            {
                int rc = Mp3NativeMethods.mpg123_init();
                _initialized = rc == Mp3NativeMethods.MPG123_OK;
                return _initialized;
            }
            catch (DllNotFoundException)
            {
                _libraryAvailable = false;
                return false;
            }
        }
    }

    /// <summary>
    /// Decodes a pre-loaded file buffer via mpg123 and returns a list of
    /// diagnostics. The input is passed as a raw pointer + length so the call
    /// works uniformly for pinned managed arrays and memory-mapped views.
    /// Returns <see langword="null"/> if the decoder handle could not be created
    /// or opened, indicating an infrastructure failure rather than a file problem.
    /// </summary>
    internal static List<(Mp3Diagnostic Diagnostic, long FrameIndex)>? Decode(
        IntPtr data,
        int length
    )
    {
        var diagnostics = new List<(Mp3Diagnostic, long)>();

        int error = 0;
        IntPtr mh = Mp3NativeMethods.mpg123_new(null, ref error);
        if (mh == IntPtr.Zero)
            return null;

        try
        {
            int rc = Mp3NativeMethods.mpg123_open_feed(mh);
            if (rc != Mp3NativeMethods.MPG123_OK)
                return null;

            Mp3NativeMethods.mpg123_feed(mh, data, (nuint)length);

            var outBuf = ArrayPool<byte>.Shared.Rent(MaxDecodedFrameBytes);
            try
            {
                while (true)
                {
                    rc = Mp3NativeMethods.mpg123_read(
                        mh,
                        outBuf,
                        (nuint)MaxDecodedFrameBytes,
                        out _
                    );

                    if (rc == Mp3NativeMethods.MPG123_DONE)
                        break;
                    if (
                        rc == Mp3NativeMethods.MPG123_OK
                        || rc == Mp3NativeMethods.MPG123_NEW_FORMAT
                    )
                        continue;
                    if (rc == Mp3NativeMethods.MPG123_ERR)
                    {
                        diagnostics.Add((Mp3Diagnostic.DECODE_ERROR, 0));
                        break;
                    }
                    // MPG123_NEED_MORE: all data already fed, treat as end of stream
                    break;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(outBuf);
            }
        }
        finally
        {
            Mp3NativeMethods.mpg123_delete(mh);
        }

        return diagnostics;
    }

    internal static void Shutdown()
    {
        if (_initialized)
            Mp3NativeMethods.mpg123_exit();
    }
}
