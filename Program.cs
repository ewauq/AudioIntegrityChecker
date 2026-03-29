using System.Runtime.Versioning;
using AudioIntegrityChecker.UI;

[assembly: SupportedOSPlatform("windows")]

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();

        var missing = CheckDependencies();
        if (missing.Count > 0)
        {
            string list = string.Join("\n  • ", missing);
            MessageBox.Show(
                $"The following required libraries were not found next to the executable or in PATH:\n\n  • {list}\n\n"
                    + $"Place them in the same folder as AudioIntegrityChecker.exe and restart the application.",
                "Missing dependencies",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
            return;
        }

        Application.Run(new MainForm());
    }

    private static List<string> CheckDependencies()
    {
        var required = new[] { ("libFLAC.dll", "FLAC native decoder — https://xiph.org/flac/") };

        var missing = new List<string>();
        foreach (var (file, description) in required)
        {
            if (!IsAvailable(file))
                missing.Add($"{file}  ({description})");
        }
        return missing;
    }

    private static bool IsAvailable(string fileName)
    {
        // Use NativeLibrary.TryLoad — mirrors the exact DLL search order used by P/Invoke
        // (app dir → System32 → Windows dir → CWD → PATH directories).
        if (System.Runtime.InteropServices.NativeLibrary.TryLoad(fileName, out var handle))
        {
            System.Runtime.InteropServices.NativeLibrary.Free(handle);
            return true;
        }
        return false;
    }
}
