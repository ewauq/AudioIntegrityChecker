using System.Diagnostics;
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

    public static NativeLibraryMetadata? GetLibFlacMetadata(string configuredPath) =>
        GetMetadata(configuredPath, LibFlacName, ReadLibFlacVersion);

    public static NativeLibraryMetadata? GetMpg123Metadata(string configuredPath) =>
        GetMetadata(configuredPath, Mpg123Name, ReadMpg123Version);

    /// <summary>
    /// Resolves the DLL to an on-disk path (user-configured first, app folder
    /// second) and extracts its file version plus a best-effort build date
    /// from the PE header. Returns null when no readable DLL can be located.
    /// Falls back to a library-specific runtime probe when the PE resources
    /// carry no version info (the case for most Xiph/mpg123 MinGW builds).
    /// </summary>
    private static NativeLibraryMetadata? GetMetadata(
        string configuredPath,
        string defaultName,
        Func<string, string?> runtimeVersionProbe
    )
    {
        string? path = ResolvePhysicalPath(configuredPath, defaultName);
        if (path is null)
            return null;

        string? version = null;
        try
        {
            var fvi = FileVersionInfo.GetVersionInfo(path);
            string raw = fvi.FileVersion ?? fvi.ProductVersion ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(raw) && raw != "0.0.0.0")
                version = raw.Trim();
        }
        catch { }

        if (string.IsNullOrEmpty(version))
        {
            try
            {
                version = runtimeVersionProbe(path);
            }
            catch { }
        }

        DateTime? buildDate = ReadPeTimestampUtc(path);
        if (buildDate is null)
        {
            try
            {
                buildDate = File.GetLastWriteTimeUtc(path);
            }
            catch { }
        }

        return new NativeLibraryMetadata(version, buildDate);
    }

    /// <summary>
    /// Reads the <c>FLAC__VERSION_STRING</c> exported symbol (a
    /// <c>const char *</c>) to recover a version string like "1.5.0" when
    /// the PE file has no VERSIONINFO resource.
    /// </summary>
    private static string? ReadLibFlacVersion(string path)
    {
        if (!NativeLibrary.TryLoad(path, out IntPtr handle))
            return null;
        try
        {
            if (!NativeLibrary.TryGetExport(handle, "FLAC__VERSION_STRING", out IntPtr sym))
                return null;
            // The exported symbol points at a `const char *` variable, so one
            // pointer-dereference gets us the address of the actual string.
            IntPtr strPtr = Marshal.ReadIntPtr(sym);
            if (strPtr == IntPtr.Zero)
                return null;
            return Marshal.PtrToStringAnsi(strPtr);
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    /// <summary>
    /// Calls <c>mpg123_distversion(major, minor, patch)</c> to recover a
    /// runtime version string from mpg123 when VERSIONINFO is missing.
    /// </summary>
    private static string? ReadMpg123Version(string path)
    {
        if (!NativeLibrary.TryLoad(path, out IntPtr handle))
            return null;
        try
        {
            if (!NativeLibrary.TryGetExport(handle, "mpg123_distversion", out IntPtr sym))
                return null;
            var distversion = Marshal.GetDelegateForFunctionPointer<Mpg123DistVersionDelegate>(sym);
            uint major = 0,
                minor = 0,
                patch = 0;
            distversion(ref major, ref minor, ref patch);
            if (major == 0 && minor == 0 && patch == 0)
                return null;
            return $"{major}.{minor}.{patch}";
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Mpg123DistVersionDelegate(ref uint major, ref uint minor, ref uint patch);

    private static string? ResolvePhysicalPath(string configuredPath, string defaultName)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            return configuredPath;
        string fallback = Path.Combine(AppContext.BaseDirectory, defaultName);
        if (File.Exists(fallback))
            return fallback;
        // Ask Windows to locate the DLL along its default search path and
        // read back the absolute path via GetModuleFileName.
        if (NativeLibrary.TryLoad(defaultName, out IntPtr handle))
        {
            try
            {
                var sb = new System.Text.StringBuilder(1024);
                int len = GetModuleFileName(handle, sb, sb.Capacity);
                if (len > 0 && len < sb.Capacity)
                    return sb.ToString();
            }
            finally
            {
                NativeLibrary.Free(handle);
            }
        }
        return null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetModuleFileName(
        IntPtr hModule,
        System.Text.StringBuilder lpFilename,
        int nSize
    );

    /// <summary>
    /// Reads the PE COFF TimeDateStamp. Returns null when the file is not a
    /// valid PE image or when the timestamp is zero / a reproducible-build
    /// marker outside the plausible calendar range.
    /// </summary>
    private static DateTime? ReadPeTimestampUtc(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            if (fs.Length < 64 || br.ReadUInt16() != 0x5A4D) // "MZ"
                return null;
            fs.Seek(0x3C, SeekOrigin.Begin);
            uint peOffset = br.ReadUInt32();
            if (peOffset + 8 > fs.Length)
                return null;
            fs.Seek(peOffset, SeekOrigin.Begin);
            if (br.ReadUInt32() != 0x00004550) // "PE\0\0"
                return null;
            fs.Seek(4, SeekOrigin.Current); // Machine (2) + NumberOfSections (2)
            uint timestamp = br.ReadUInt32();
            if (timestamp == 0)
                return null;
            var date = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime;
            if (date.Year < 1990 || date > DateTime.UtcNow.AddYears(1))
                return null; // likely a reproducible-build marker, not a real date
            return date;
        }
        catch
        {
            return null;
        }
    }

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

internal readonly record struct NativeLibraryMetadata(string? Version, DateTime? BuildDateUtc);
