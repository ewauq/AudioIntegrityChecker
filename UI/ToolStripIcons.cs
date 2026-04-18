using System.Runtime.Versioning;

namespace AudioIntegrityChecker.UI;

[SupportedOSPlatform("windows")]
internal static class ToolStripIcons
{
    public const string FolderAdd = "folder_add.png";
    public const string PageWhiteAdd = "page_white_add.png";
    public const string ControlPlay = "control_play_blue.png";
    public const string ControlPause = "control_pause_blue.png";
    public const string Cancel = "cancel.png";
    public const string BinEmpty = "bin_empty.png";
    public const string Help = "help.png";
    public const string Cog = "cog.png";

    public static Image Load(string resourceName)
    {
        var asm = typeof(ToolStripIcons).Assembly;
        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream is null)
            throw new InvalidOperationException($"Embedded icon not found: {resourceName}");
        return Image.FromStream(stream);
    }
}
