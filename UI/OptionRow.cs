using System.Runtime.Versioning;

namespace AudioIntegrityChecker.UI;

[SupportedOSPlatform("windows")]
internal sealed class OptionRow : UserControl
{
    private const int DescriptionMaxWidth = 520;

    private readonly TableLayoutPanel _layout;

    public CheckBox? CheckBoxControl { get; private set; }
    public TrackBar? SliderControl { get; private set; }
    public Label? ValueLabel { get; private set; }
    public Label? DescriptionLabel { get; private set; }

    private OptionRow()
    {
        Dock = DockStyle.Top;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Margin = new Padding(0, 0, 0, 16);
        Padding = Padding.Empty;

        _layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        Controls.Add(_layout);
    }

    public static OptionRow CheckBox(string title, string? description = null)
    {
        var row = new OptionRow();

        var chk = new System.Windows.Forms.CheckBox
        {
            Text = title,
            AutoSize = true,
            Margin = Padding.Empty,
        };
        row.CheckBoxControl = chk;
        row._layout.Controls.Add(chk);
        row._layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        if (!string.IsNullOrEmpty(description))
        {
            var desc = new Label
            {
                Text = description,
                AutoSize = true,
                MaximumSize = new Size(DescriptionMaxWidth, 0),
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(20, 2, 0, 0),
            };
            row.DescriptionLabel = desc;
            row._layout.Controls.Add(desc);
            row._layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        return row;
    }

    public static OptionRow Slider(
        string title,
        int min,
        int max,
        int value,
        string? description = null
    )
    {
        var row = new OptionRow();

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Margin = new Padding(0, 0, 0, 4),
            Padding = Padding.Empty,
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var titleLbl = new Label
        {
            Text = title,
            AutoSize = true,
            Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold),
            Margin = Padding.Empty,
            Anchor = AnchorStyles.Left | AnchorStyles.Bottom,
        };
        var valueLbl = new Label
        {
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = Padding.Empty,
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
        };
        row.ValueLabel = valueLbl;
        header.Controls.Add(titleLbl, 0, 0);
        header.Controls.Add(valueLbl, 1, 0);

        row._layout.Controls.Add(header);
        row._layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var track = new TrackBar
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Clamp(value, min, max),
            TickFrequency = 1,
            SmallChange = 1,
            LargeChange = 2,
            TickStyle = TickStyle.BottomRight,
            Dock = DockStyle.Top,
            Margin = Padding.Empty,
        };
        row.SliderControl = track;
        row._layout.Controls.Add(track);
        row._layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        if (!string.IsNullOrEmpty(description))
        {
            var desc = new Label
            {
                Text = description,
                AutoSize = true,
                MaximumSize = new Size(DescriptionMaxWidth, 0),
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 2, 0, 0),
            };
            row.DescriptionLabel = desc;
            row._layout.Controls.Add(desc);
            row._layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        return row;
    }
}
