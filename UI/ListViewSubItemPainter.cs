using System.Runtime.Versioning;

namespace AudioIntegrityChecker.UI;

[SupportedOSPlatform("windows")]
internal static class ListViewSubItemPainter
{
    private static readonly Color RowAltColor = Color.FromArgb(245, 245, 245);

    /// <summary>
    /// Owner-draw renderer shared by the result list views. Paints the row
    /// background (selection or zebra), draws the cell text with the
    /// per-subitem fore colour and column alignment, and adds a 1 px vertical
    /// divider on the right edge of every cell.
    /// </summary>
    internal static void Paint(ListView lv, DrawListViewSubItemEventArgs e)
    {
        if (e.Item is null)
            return;

        Color back = e.Item.Selected
            ? (lv.Focused ? SystemColors.Highlight : SystemColors.ButtonFace)
            : (e.ItemIndex % 2 == 0 ? Color.White : RowAltColor);

        using (var brush = new SolidBrush(back))
            e.Graphics.FillRectangle(brush, e.Bounds);

        var subFore = e.SubItem?.ForeColor ?? Color.Empty;
        Color fore =
            e.Item.Selected && lv.Focused
                ? SystemColors.HighlightText
                : (subFore.IsEmpty ? SystemColors.WindowText : subFore);

        var align = lv.Columns[e.ColumnIndex].TextAlign;
        var flags =
            TextFormatFlags.VerticalCenter
            | TextFormatFlags.EndEllipsis
            | (
                align == HorizontalAlignment.Center ? TextFormatFlags.HorizontalCenter
                : align == HorizontalAlignment.Right ? TextFormatFlags.Right
                : TextFormatFlags.Left
            );

        var textBounds = new Rectangle(
            e.Bounds.X + 3,
            e.Bounds.Y,
            e.Bounds.Width - 6,
            e.Bounds.Height
        );
        TextRenderer.DrawText(e.Graphics, e.SubItem?.Text, e.Item.Font, textBounds, fore, flags);

        using var pen = new Pen(SystemColors.ControlLight);
        e.Graphics.DrawLine(
            pen,
            e.Bounds.Right - 1,
            e.Bounds.Top,
            e.Bounds.Right - 1,
            e.Bounds.Bottom - 1
        );
    }
}
