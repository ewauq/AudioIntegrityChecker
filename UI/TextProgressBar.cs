using System.Runtime.Versioning;

namespace AudioIntegrityChecker.UI;

[SupportedOSPlatform("windows")]
internal sealed class TextProgressBar : ProgressBar
{
    private int _marqueeOffset;
    private bool _paused;

    public int MarqueeOffset
    {
        get => _marqueeOffset;
        set
        {
            _marqueeOffset = value;
            Invalidate();
        }
    }

    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            Invalidate();
        }
    }

    public TextProgressBar()
    {
        SetStyle(
            ControlStyles.UserPaint
                | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer,
            true
        );
        ForeColor = Color.Black;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var rect = ClientRectangle;

        if (ProgressBarRenderer.IsSupported)
            ProgressBarRenderer.DrawHorizontalBar(g, rect);
        else
            g.FillRectangle(SystemBrushes.Control, rect);

        var fill = rect;
        fill.Inflate(-1, -1);

        if (Style == ProgressBarStyle.Marquee)
        {
            int blockW = (int)(fill.Width * 0.4);
            int x = fill.X + _marqueeOffset % (fill.Width + blockW) - blockW;
            int clampedX = Math.Max(x, fill.X);
            int clampedW = Math.Min(x + blockW, fill.Right) - clampedX;
            if (clampedW > 0)
                DrawChunk(g, new Rectangle(clampedX, fill.Y, clampedW, fill.Height));
        }
        else if (Maximum > 0 && Value > 0)
        {
            int fillW = (int)Math.Round((double)Value / Maximum * fill.Width);
            if (fillW > 0)
                DrawChunk(g, new Rectangle(fill.X, fill.Y, fillW, fill.Height));
        }

        if (!string.IsNullOrEmpty(Text))
        {
            TextRenderer.DrawText(
                g,
                Text,
                Font,
                ClientRectangle,
                ForeColor,
                TextFormatFlags.HorizontalCenter
                    | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.SingleLine
            );
        }
    }

    private void DrawChunk(Graphics g, Rectangle chunk)
    {
        if (_paused)
        {
            using var b = new SolidBrush(Color.FromArgb(180, 180, 180));
            g.FillRectangle(b, chunk);
        }
        else if (ProgressBarRenderer.IsSupported)
            ProgressBarRenderer.DrawHorizontalChunks(g, chunk);
        else
            using (var b = new SolidBrush(SystemColors.Highlight))
                g.FillRectangle(b, chunk);
    }
}
