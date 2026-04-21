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
        var prefs = UserPreferences.Load();
        NativeLibraryLoader.Configure(prefs.LibFlacPath, prefs.Mpg123Path);

        var missing = CheckDependencies(prefs);
        if (missing.Count > 0)
        {
            string list = string.Join("\n  • ", missing);
            MessageBox.Show(
                $"The following required libraries were not found:\n\n  • {list}\n\n"
                    + "Download them from the project's GitHub releases, then either\n"
                    + "place them next to the executable or point to them from\n"
                    + "File > Options > Native libraries.",
                "Missing dependencies",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
            return;
        }

        Application.Run(new MainForm());
    }

    private static List<string> CheckDependencies(UserPreferences prefs)
    {
        var missing = new List<string>();
        if (!NativeLibraryLoader.IsLibFlacAvailable(prefs.LibFlacPath))
            missing.Add("libFLAC.dll  (FLAC native decoder — https://xiph.org/flac/)");
        if (!NativeLibraryLoader.IsMpg123Available(prefs.Mpg123Path))
            missing.Add("mpg123.dll  (MP3 native decoder — https://mpg123.org/)");
        return missing;
    }
}
