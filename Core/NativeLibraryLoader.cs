using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AudioIntegrityChecker.Core;

/// <summary>
/// Resolves DllImport calls to user-configured paths for libFLAC and mpg123.
/// Must be configured before any P/Invoke that targets those libraries, since
/// the runtime caches resolution per (assembly, library name).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NativeLibraryLoader
{
    private const string LibFlacName = "libFLAC.dll";
    private const string Mpg123Name = "mpg123.dll";

    // Symbols that every real build of each library must export. Used to tell
    // a genuine libFLAC / mpg123 apart from an unrelated DLL that a user might
    // accidentally (or maliciously) point at: a loadable-but-wrong DLL would
    // pass a file-name check and then crash at the first P/Invoke.
    private const string LibFlacProbeSymbol = "FLAC__stream_decoder_new";
    private const string Mpg123ProbeSymbol = "mpg123_init";

    private static readonly object _lock = new();
    private static bool _resolverRegistered;
    private static IntPtr _libFlacHandle;
    private static string? _libFlacLoadedFrom;
    private static IntPtr _mpg123Handle;
    private static string? _mpg123LoadedFrom;

    public static void Configure(string libFlacPath, string mpg123Path)
    {
        lock (_lock)
        {
            if (!_resolverRegistered)
            {
                NativeLibrary.SetDllImportResolver(typeof(NativeLibraryLoader).Assembly, Resolver);
                _resolverRegistered = true;
            }

            SyncHandle(libFlacPath, LibFlacProbeSymbol, ref _libFlacHandle, ref _libFlacLoadedFrom);
            SyncHandle(mpg123Path, Mpg123ProbeSymbol, ref _mpg123Handle, ref _mpg123LoadedFrom);
        }
    }

    /// <summary>
    /// True when the library is either loadable from a user-configured path or
    /// resolvable through the default Windows DLL search path.
    /// </summary>
    public static bool IsLibFlacAvailable(string configuredPath) =>
        IsAvailable(configuredPath, LibFlacName, LibFlacProbeSymbol);

    public static bool IsMpg123Available(string configuredPath) =>
        IsAvailable(configuredPath, Mpg123Name, Mpg123ProbeSymbol);

    public static NativeLibraryStatus ValidateLibFlac(string configuredPath) =>
        Validate(configuredPath, LibFlacName, LibFlacProbeSymbol);

    public static NativeLibraryStatus ValidateMpg123(string configuredPath) =>
        Validate(configuredPath, Mpg123Name, Mpg123ProbeSymbol);

    /// <summary>
    /// Classifies the availability of a native library. Found means it loads
    /// (either from the configured path or from the Windows default search);
    /// Error means a path was configured but the file is missing or not
    /// loadable; Missing means no path is configured and the default search
    /// does not locate the library.
    /// </summary>
    private static NativeLibraryStatus Validate(
        string configuredPath,
        string defaultName,
        string probeSymbol
    )
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            if (TryLoadMatching(defaultName, probeSymbol, out var handle))
            {
                NativeLibrary.Free(handle);
                return NativeLibraryStatus.Found;
            }
            return NativeLibraryStatus.Missing;
        }

        if (!File.Exists(configuredPath))
            return NativeLibraryStatus.Error;

        if (TryLoadMatching(configuredPath, probeSymbol, out var h))
        {
            NativeLibrary.Free(h);
            return NativeLibraryStatus.Found;
        }
        return NativeLibraryStatus.Error;
    }

    /// <summary>
    /// Load the library and only keep the handle if it exports the expected
    /// probe symbol. Rejects unrelated DLLs that happen to load but would
    /// crash at the first P/Invoke.
    /// </summary>
    private static bool TryLoadMatching(string nameOrPath, string probeSymbol, out IntPtr handle)
    {
        if (!NativeLibrary.TryLoad(nameOrPath, out handle))
        {
            handle = IntPtr.Zero;
            return false;
        }
        if (!NativeLibrary.TryGetExport(handle, probeSymbol, out _))
        {
            NativeLibrary.Free(handle);
            handle = IntPtr.Zero;
            return false;
        }
        return true;
    }

    private static void SyncHandle(
        string path,
        string probeSymbol,
        ref IntPtr handle,
        ref string? loadedFrom
    )
    {
        // Once a native library is resolved for the process, never free or swap it.
        // The CLR caches DllImport resolutions per (assembly, library name), and any
        // P/Invoke that already bound to this handle holds cached function pointers
        // into it; unloading would leave dangling pointers. To switch paths, the user
        // must restart the application.
        if (handle != IntPtr.Zero)
            return;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        if (TryLoadMatching(path, probeSymbol, out var h))
        {
            handle = h;
            loadedFrom = path;
        }
        else
        {
            handle = IntPtr.Zero;
            loadedFrom = null;
        }
    }

    private static IntPtr Resolver(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath
    )
    {
        // DllImport attributes use "libFLAC.dll" and "mpg123.dll" as names.
        // Match by loose contains to tolerate minor naming variants (lib prefix, .dll suffix).
        string n = libraryName.ToLowerInvariant();
        if (n.Contains("flac") && _libFlacHandle != IntPtr.Zero)
            return _libFlacHandle;
        if (n.Contains("mpg123") && _mpg123Handle != IntPtr.Zero)
            return _mpg123Handle;
        return IntPtr.Zero; // fall through to default Windows search
    }

    private static bool IsAvailable(
        string configuredPath,
        string defaultName,
        string probeSymbol
    ) => Validate(configuredPath, defaultName, probeSymbol) == NativeLibraryStatus.Found;
}

internal enum NativeLibraryStatus
{
    /// <summary>Library is loadable (configured path or default search).</summary>
    Found,

    /// <summary>No path configured and the library is not in the default search path.</summary>
    Missing,

    /// <summary>A path was configured but the file is missing or not loadable.</summary>
    Error,
}
