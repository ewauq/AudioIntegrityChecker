using System.Runtime.Versioning;

namespace AudioIntegrityChecker.UI;

/// <summary>
/// ListView with double-buffering enabled to eliminate column-resize flicker.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class BufferedListView : ListView
{
    public BufferedListView() => DoubleBuffered = true;
}
