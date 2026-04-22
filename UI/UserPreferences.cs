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

    internal static UserPreferences Load()
    {
        var prefs = new UserPreferences();

        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        if (key is null)
            return prefs;

        prefs.WindowX = (int)(key.GetValue("WindowX") ?? int.MinValue);
        prefs.WindowY = (int)(key.GetValue("WindowY") ?? int.MinValue);
        prefs.WindowWidth = (int)(key.GetValue("WindowWidth") ?? 0);
        prefs.WindowHeight = (int)(key.GetValue("WindowHeight") ?? 0);
        prefs.WindowMaximized = ((int)(key.GetValue("WindowMaximized") ?? 0)) == 1;
        prefs.HelpPanelVisible = ((int)(key.GetValue("HelpPanelVisible") ?? 1)) == 1;

        if (key.GetValue("HiddenColumns") is string hiddenCols && hiddenCols.Length > 0)
        {
            foreach (var part in hiddenCols.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(part, out int colIndex))
                    prefs.HiddenColumns.Add(colIndex);
            }
        }

        prefs.WorkerCountAuto = ((int)(key.GetValue("WorkerCountAuto") ?? 1)) == 1;
        prefs.WorkerCount = (int)(key.GetValue("WorkerCount") ?? Environment.ProcessorCount);

        prefs.LibFlacPath = (string)(key.GetValue("LibFlacPath") ?? string.Empty);
        prefs.Mpg123Path = (string)(key.GetValue("Mpg123Path") ?? string.Empty);

        return prefs;
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
    }
}
