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
    public TextBox? TextBoxControl { get; private set; }
    public PictureBox? StatusPicture { get; private set; }
    public Label? StatusLabel { get; private set; }

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

    public static OptionRow FilePath(
        string title,
        string initialValue,
        string fileFilter,
        string? description = null
    )
    {
        var row = new OptionRow();

        var titleLbl = new Label
        {
            Text = title,
            AutoSize = true,
            Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 4),
        };
        row._layout.Controls.Add(titleLbl);
        row._layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var inputRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 4,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var textBox = new TextBox
        {
            Text = initialValue,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 6, 0),
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
        };
        row.TextBoxControl = textBox;

        var browseBtn = new Button
        {
            Text = "Browse…",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 8, 0),
            Padding = new Padding(6, 0, 6, 0),
        };
        browseBtn.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog
            {
                Title = $"Select {title}",
                Filter = fileFilter,
                FileName = textBox.Text,
                CheckFileExists = true,
            };
            if (!string.IsNullOrEmpty(textBox.Text))
            {
                try
                {
                    dialog.InitialDirectory = Path.GetDirectoryName(textBox.Text) ?? string.Empty;
                }
                catch { }
            }
            if (dialog.ShowDialog(row.FindForm()) == DialogResult.OK)
                textBox.Text = dialog.FileName;
        };

        var statusPic = new PictureBox
        {
            Size = new Size(16, 16),
            SizeMode = PictureBoxSizeMode.CenterImage,
            Margin = new Padding(0, 4, 4, 0),
            Anchor = AnchorStyles.Left,
        };
        row.StatusPicture = statusPic;

        var statusLbl = new Label
        {
            AutoSize = true,
            Margin = new Padding(0, 5, 0, 0),
            Anchor = AnchorStyles.Left,
            Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Bold),
        };
        row.StatusLabel = statusLbl;

        inputRow.Controls.Add(textBox, 0, 0);
        inputRow.Controls.Add(browseBtn, 1, 0);
        inputRow.Controls.Add(statusPic, 2, 0);
        inputRow.Controls.Add(statusLbl, 3, 0);

        row._layout.Controls.Add(inputRow);
        row._layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        if (!string.IsNullOrEmpty(description))
        {
            var desc = BuildDescriptionControl(description);
            row.DescriptionLabel = desc as Label;
            desc.Margin = new Padding(0, 4, 0, 0);
            row._layout.Controls.Add(desc);
            row._layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        return row;
    }

    private static Control BuildDescriptionControl(string description)
    {
        // Detect an http(s) URL in the description and render it as a clickable
        // LinkLabel region. Otherwise fall back to a plain gray Label.
        var match = System.Text.RegularExpressions.Regex.Match(description, @"https?://[^\s)]+");
        if (!match.Success)
        {
            return new Label
            {
                Text = description,
                AutoSize = true,
                MaximumSize = new Size(DescriptionMaxWidth, 0),
                ForeColor = SystemColors.GrayText,
            };
        }

        var link = new LinkLabel
        {
            Text = description,
            AutoSize = true,
            MaximumSize = new Size(DescriptionMaxWidth, 0),
            LinkColor = SystemColors.HotTrack,
            ActiveLinkColor = SystemColors.HotTrack,
            LinkBehavior = LinkBehavior.HoverUnderline,
            Padding = Padding.Empty,
        };
        link.LinkArea = new LinkArea(match.Index, match.Length);
        string url = match.Value;
        link.LinkClicked += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }
                );
            }
            catch { }
        };
        // LinkLabel shows its text black by default; match the surrounding gray
        // for the non-link portion.
        link.ForeColor = SystemColors.GrayText;
        return link;
    }
}
