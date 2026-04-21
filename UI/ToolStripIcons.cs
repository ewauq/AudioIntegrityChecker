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
    public const string AcceptButton = "accept_button.png";
    public const string Cross = "cross.png";

    public static Image Load(string resourceName)
    {
        var asm = typeof(ToolStripIcons).Assembly;
        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream is null)
            throw new InvalidOperationException($"Embedded icon not found: {resourceName}");
        // Image.FromStream keeps a reference on the stream for the image's lifetime,
        // so it must not be disposed while the image is in use. Clone the decoded
        // image into a standalone bitmap and let the stream close immediately.
        using var decoded = Image.FromStream(stream);
        return new Bitmap(decoded);
    }
}
