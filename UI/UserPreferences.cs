using System.Runtime.Versioning;
using Microsoft.Win32;

namespace AudioIntegrityChecker.UI;

/// <summary>
/// Persists UI preferences (window geometry, help panel, hidden columns,
/// worker count) in HKCU\Software\AudioIntegrityChecker.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class UserPreferences
{
    private const string RegistryPath = @"Software\AudioIntegrityChecker";

    internal int WindowX { get; set; } = int.MinValue;
    internal int WindowY { get; set; } = int.MinValue;
    internal int WindowWidth { get; set; }
    internal int WindowHeight { get; set; }
    internal bool WindowMaximized { get; set; }
    internal bool HelpPanelVisible { get; set; } = true;
    internal HashSet<int> HiddenColumns { get; set; } = [];

    internal bool WorkerCountAuto { get; set; } = true;
    internal int WorkerCount { get; set; } = Environment.ProcessorCount;

    internal string LibFlacPath { get; set; } = string.Empty;
    internal string Mpg123Path { get; set; } = string.Empty;

    internal string LastExportFormat { get; set; } = "Text";
    internal string LastExportScope { get; set; } = "Issues";

    internal static UserPreferences Load()
    {
        var prefs = new UserPreferences();

        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        if (key is null)
            return prefs;

        // A corrupted or externally-tampered registry value would otherwise
        // bring down the whole startup with InvalidCastException. Read each
        // entry through helpers that fall back to the property default.
        prefs.WindowX = ReadInt(key, "WindowX", int.MinValue);
        prefs.WindowY = ReadInt(key, "WindowY", int.MinValue);
        prefs.WindowWidth = ReadInt(key, "WindowWidth", 0);
        prefs.WindowHeight = ReadInt(key, "WindowHeight", 0);
        prefs.WindowMaximized = ReadInt(key, "WindowMaximized", 0) == 1;
        prefs.HelpPanelVisible = ReadInt(key, "HelpPanelVisible", 1) == 1;

        string hiddenCols = ReadString(key, "HiddenColumns", string.Empty);
        if (hiddenCols.Length > 0)
        {
            foreach (var part in hiddenCols.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(part, out int colIndex))
                    prefs.HiddenColumns.Add(colIndex);
            }
        }

        prefs.WorkerCountAuto = ReadInt(key, "WorkerCountAuto", 1) == 1;
        prefs.WorkerCount = ReadInt(key, "WorkerCount", Environment.ProcessorCount);

        prefs.LibFlacPath = ReadString(key, "LibFlacPath", string.Empty);
        prefs.Mpg123Path = ReadString(key, "Mpg123Path", string.Empty);

        prefs.LastExportFormat = ReadString(key, "LastExportFormat", "Text");
        prefs.LastExportScope = ReadString(key, "LastExportScope", "Issues");

        return prefs;
    }

    private static int ReadInt(RegistryKey key, string name, int fallback)
    {
        try
        {
            return key.GetValue(name) is int i ? i : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static string ReadString(RegistryKey key, string name, string fallback)
    {
        try
        {
            return key.GetValue(name) is string s ? s : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    internal void Save()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
        key.SetValue("WindowX", WindowX, RegistryValueKind.DWord);
        key.SetValue("WindowY", WindowY, RegistryValueKind.DWord);
        key.SetValue("WindowWidth", WindowWidth, RegistryValueKind.DWord);
        key.SetValue("WindowHeight", WindowHeight, RegistryValueKind.DWord);
        key.SetValue("WindowMaximized", WindowMaximized ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("HelpPanelVisible", HelpPanelVisible ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("HiddenColumns", string.Join(",", HiddenColumns), RegistryValueKind.String);
        key.SetValue("WorkerCountAuto", WorkerCountAuto ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue("WorkerCount", WorkerCount, RegistryValueKind.DWord);
        key.SetValue("LibFlacPath", LibFlacPath ?? string.Empty, RegistryValueKind.String);
        key.SetValue("Mpg123Path", Mpg123Path ?? string.Empty, RegistryValueKind.String);
        key.SetValue("LastExportFormat", LastExportFormat ?? "Text", RegistryValueKind.String);
        key.SetValue("LastExportScope", LastExportScope ?? "Issues", RegistryValueKind.String);
    }
}
