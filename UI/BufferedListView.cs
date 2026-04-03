using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AudioIntegrityChecker.UI;

/// <summary>
/// ListView with double-buffering enabled to eliminate column-resize flicker.
/// Adds <see cref="HeaderContextMenuStrip"/> for right-click on column headers,
/// which the standard ListView does not support (the header is a separate native control).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class BufferedListView : ListView
{
    private const int WM_CONTEXTMENU = 0x007B;
    private const int LVM_GETHEADER = 0x101F;

    public ContextMenuStrip? HeaderContextMenuStrip { get; set; }

    public BufferedListView() => DoubleBuffered = true;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_CONTEXTMENU && HeaderContextMenuStrip is not null)
        {
            IntPtr headerHandle = SendMessage(Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
            if (m.WParam == headerHandle)
            {
                var pos = PointToClient(Cursor.Position);
                HeaderContextMenuStrip.Show(this, pos);
                return;
            }
        }

        base.WndProc(ref m);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
