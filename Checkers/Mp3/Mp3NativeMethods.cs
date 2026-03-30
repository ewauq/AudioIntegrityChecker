using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AudioIntegrityChecker.Checkers.Mp3;

[SupportedOSPlatform("windows")]
internal static class Mp3NativeMethods
{
    internal const int MPG123_OK = 0;
    internal const int MPG123_DONE = -12;
    internal const int MPG123_ERR = -1;
    internal const int MPG123_NEW_FORMAT = 1;
    internal const int MPG123_NEED_MORE = 10;

    [DllImport("mpg123.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpg123_init();

    [DllImport("mpg123.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr mpg123_new(string? decoder, ref int error);

    [DllImport("mpg123.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpg123_open_feed(IntPtr mh);

    [DllImport("mpg123.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpg123_feed(IntPtr mh, byte[] data, nuint size);

    [DllImport("mpg123.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int mpg123_read(IntPtr mh, byte[] outBuffer, nuint size, out nuint done);

    [DllImport("mpg123.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpg123_delete(IntPtr mh);

    [DllImport("mpg123.dll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void mpg123_exit();
}
