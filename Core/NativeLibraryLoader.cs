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

            SyncHandle(libFlacPath, ref _libFlacHandle, ref _libFlacLoadedFrom);
            SyncHandle(mpg123Path, ref _mpg123Handle, ref _mpg123LoadedFrom);
        }
    }

    /// <summary>
    /// True when the library is either loadable from a user-configured path or
    /// resolvable through the default Windows DLL search path.
    /// </summary>
    public static bool IsLibFlacAvailable(string configuredPath) =>
        IsAvailable(configuredPath, LibFlacName);

    public static bool IsMpg123Available(string configuredPath) =>
        IsAvailable(configuredPath, Mpg123Name);

    public static NativeLibraryStatus ValidateLibFlac(string configuredPath) =>
        Validate(configuredPath, LibFlacName);

    public static NativeLibraryStatus ValidateMpg123(string configuredPath) =>
        Validate(configuredPath, Mpg123Name);

    public static NativeLibraryMetadata? GetLibFlacMetadata(string configuredPath) =>
        GetMetadata(configuredPath, LibFlacName);

    public static NativeLibraryMetadata? GetMpg123Metadata(string configuredPath) =>
        GetMetadata(configuredPath, Mpg123Name);

    /// <summary>
    /// Resolves the DLL to an on-disk path (user-configured first, app folder
    /// second) and extracts its file version plus a best-effort build date
    /// from the PE header. Returns null when no readable DLL can be located.
    /// </summary>
    private static NativeLibraryMetadata? GetMetadata(string configuredPath, string defaultName)
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
    /// Returns Valid when the library is loadable either from the user-configured
    /// path or, if empty, from the default Windows DLL search path. Invalid
    /// otherwise (path configured but unloadable, or search-path fallback fails).
    /// </summary>
    private static NativeLibraryStatus Validate(string configuredPath, string defaultName)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            if (NativeLibrary.TryLoad(defaultName, out var handle))
            {
                NativeLibrary.Free(handle);
                return NativeLibraryStatus.Valid;
            }
            return NativeLibraryStatus.Invalid;
        }

        if (!File.Exists(configuredPath))
            return NativeLibraryStatus.Invalid;

        try
        {
            var handle = NativeLibrary.Load(configuredPath);
            NativeLibrary.Free(handle);
            return NativeLibraryStatus.Valid;
        }
        catch
        {
            return NativeLibraryStatus.Invalid;
        }
    }

    private static void SyncHandle(string path, ref IntPtr handle, ref string? loadedFrom)
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

        try
        {
            handle = NativeLibrary.Load(path);
            loadedFrom = path;
        }
        catch
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

    private static bool IsAvailable(string configuredPath, string defaultName) =>
        Validate(configuredPath, defaultName) == NativeLibraryStatus.Valid;
}

internal enum NativeLibraryStatus
{
    Valid,
    Invalid,
}

internal readonly record struct NativeLibraryMetadata(string? Version, DateTime? BuildDateUtc);
