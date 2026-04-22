using System.Runtime.Versioning;
using AudioIntegrityChecker.Core;
using AudioIntegrityChecker.UI;

[assembly: SupportedOSPlatform("windows")]

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        // Configure the native library resolver before any P/Invoke fires, so
        // user-configured paths for libFLAC.dll and mpg123.dll are honoured.
        // Availability of each library is checked lazily at Start scan time
        // against the queued file formats — the app always launches so the
        // user can configure paths from File > Options > Libraries.
        var prefs = UserPreferences.Load();
        NativeLibraryLoader.Configure(prefs.LibFlacPath, prefs.Mpg123Path);

        Application.Run(new MainForm());
    }
}
