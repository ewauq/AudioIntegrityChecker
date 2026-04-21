using System.Runtime.Versioning;
using AudioIntegrityChecker.Core;

namespace AudioIntegrityChecker.UI;

[SupportedOSPlatform("windows")]
internal sealed class OptionsForm : Form
{
    private readonly UserPreferences _prefs;

    public event Action? SettingsApplied;

    private readonly bool _origWorkerCountAuto;
    private readonly int _origWorkerCount;
    private readonly string _origLibFlacPath;
    private readonly string _origMpg123Path;

    private readonly OptionRow _autoThreadsRow;
    private readonly OptionRow _workerThreadsRow;
    private readonly OptionRow _libFlacRow;
    private readonly OptionRow _mpg123Row;

    private Image? _statusValidIcon;
    private Image? _statusInvalidIcon;
    private Image? _infoIcon;

    internal OptionsForm(UserPreferences prefs)
    {
        _prefs = prefs;
        _origWorkerCountAuto = prefs.WorkerCountAuto;
        _origWorkerCount = prefs.WorkerCount;
        _origLibFlacPath = prefs.LibFlacPath;
        _origMpg123Path = prefs.Mpg123Path;

        Text = "Options";
        FormBorderStyle = FormBorderStyle.Sizable;
        SizeGripStyle = SizeGripStyle.Auto;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(560, 440);

        int processorCount = Environment.ProcessorCount;

        _autoThreadsRow = OptionRow.CheckBox(
            "Automatic thread count",
            "Picks the optimal thread count for each scan based on the detected storage type (HDD, SATA SSD, or NVMe)."
        );

        _workerThreadsRow = OptionRow.Slider(
            title: "Worker threads",
            min: 1,
            max: processorCount,
            value: Math.Clamp(prefs.WorkerCount, 1, processorCount),
            description: $"Your CPU has {processorCount} logical cores."
        );

        const string dllFilter = "DLL files (*.dll)|*.dll|All files (*.*)|*.*";
        _libFlacRow = OptionRow.FilePath(
            title: "libFLAC.dll",
            initialValue: prefs.LibFlacPath,
            fileFilter: dllFilter,
            description: "FLAC native decoder - https://xiph.org/flac/"
        );

        _mpg123Row = OptionRow.FilePath(
            title: "mpg123.dll",
            initialValue: prefs.Mpg123Path,
            fileFilter: dllFilter,
            description: "MP3 native decoder - https://mpg123.org/"
        );

        var tabControl = BuildTabControl();
        var buttonPanel = BuildButtonPanel();

        Controls.Add(tabControl);
        Controls.Add(buttonPanel);

        LoadStatusIcons();
        WireUpEventHandlers();
        LoadFromPreferences();
        RefreshNativeLibraryStatus();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        MinimumSize = new Size(
            480 + (Width - ClientSize.Width),
            360 + (Height - ClientSize.Height)
        );
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _statusValidIcon?.Dispose();
            _statusInvalidIcon?.Dispose();
            _infoIcon?.Dispose();
        }
        base.Dispose(disposing);
    }

    private TabControl BuildTabControl()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill };

        var performanceTab = new TabPage("Performance")
        {
            Padding = new Padding(20, 16, 20, 16),
            BackColor = SystemColors.Control,
        };
        var performanceLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            ColumnCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        performanceLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        performanceLayout.Controls.Add(_autoThreadsRow);
        performanceLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        performanceLayout.Controls.Add(_workerThreadsRow);
        performanceLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        performanceLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        performanceTab.Controls.Add(performanceLayout);
        tabs.TabPages.Add(performanceTab);

        var librariesTab = new TabPage("Libraries")
        {
            Padding = new Padding(20, 16, 20, 16),
            BackColor = SystemColors.Control,
        };
        var librariesLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            ColumnCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        librariesLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        librariesLayout.Controls.Add(_libFlacRow);
        librariesLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        librariesLayout.Controls.Add(_mpg123Row);
        librariesLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        librariesLayout.Controls.Add(BuildFallbackNote());
        librariesLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        librariesLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        librariesTab.Controls.Add(librariesLayout);
        tabs.TabPages.Add(librariesTab);

        return tabs;
    }

    private Panel BuildButtonPanel()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 56,
            Padding = new Padding(16, 12, 16, 12),
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var note = new Label
        {
            Text = "Changes apply to the next scan.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            TextAlign = ContentAlignment.MiddleLeft,
            Anchor = AnchorStyles.Left,
            Margin = Padding.Empty,
        };

        var okBtn = new Button
        {
            Text = "OK",
            Size = new Size(88, 28),
            Margin = new Padding(8, 0, 0, 0),
            Anchor = AnchorStyles.Right,
        };
        okBtn.Click += (_, _) =>
        {
            CommitDraft();
            _prefs.Save();
            SettingsApplied?.Invoke();
            DialogResult = DialogResult.OK;
            Close();
        };

        var cancelBtn = new Button
        {
            Text = "Cancel",
            Size = new Size(88, 28),
            Margin = new Padding(8, 0, 0, 0),
            Anchor = AnchorStyles.Right,
        };
        cancelBtn.Click += (_, _) =>
        {
            RestoreOriginal();
            DialogResult = DialogResult.Cancel;
            Close();
        };

        var applyBtn = new Button
        {
            Text = "Apply",
            Size = new Size(88, 28),
            Margin = new Padding(8, 0, 0, 0),
            Anchor = AnchorStyles.Right,
        };
        applyBtn.Click += (_, _) =>
        {
            CommitDraft();
            _prefs.Save();
            SettingsApplied?.Invoke();
        };

        layout.Controls.Add(note, 0, 0);
        layout.Controls.Add(okBtn, 1, 0);
        layout.Controls.Add(cancelBtn, 2, 0);
        layout.Controls.Add(applyBtn, 3, 0);

        panel.Controls.Add(layout);

        AcceptButton = okBtn;
        CancelButton = cancelBtn;

        return panel;
    }

    private void LoadStatusIcons()
    {
        _statusValidIcon = ToolStripIcons.Load(ToolStripIcons.AcceptButton);
        _statusInvalidIcon = ToolStripIcons.Load(ToolStripIcons.Cross);
    }

    private void WireUpEventHandlers()
    {
        var autoChk = _autoThreadsRow.CheckBoxControl!;
        var track = _workerThreadsRow.SliderControl!;
        autoChk.CheckedChanged += (_, _) =>
        {
            track.Enabled = !autoChk.Checked;
            UpdateThreadsLabel();
        };
        track.ValueChanged += (_, _) => UpdateThreadsLabel();

        _libFlacRow.TextBoxControl!.TextChanged += (_, _) => RefreshNativeLibraryStatus();
        _mpg123Row.TextBoxControl!.TextChanged += (_, _) => RefreshNativeLibraryStatus();
    }

    private void LoadFromPreferences()
    {
        var autoChk = _autoThreadsRow.CheckBoxControl!;
        var track = _workerThreadsRow.SliderControl!;
        autoChk.Checked = _prefs.WorkerCountAuto;
        track.Value = Math.Clamp(_prefs.WorkerCount, track.Minimum, track.Maximum);
        track.Enabled = !_prefs.WorkerCountAuto;
        UpdateThreadsLabel();

        _libFlacRow.TextBoxControl!.Text = _prefs.LibFlacPath;
        _mpg123Row.TextBoxControl!.Text = _prefs.Mpg123Path;
    }

    private void CommitDraft()
    {
        _prefs.WorkerCountAuto = _autoThreadsRow.CheckBoxControl!.Checked;
        _prefs.WorkerCount = _workerThreadsRow.SliderControl!.Value;
        // Native library paths are persisted here but only take effect after the
        // next application start: the resolver is configured once in Program.Main
        // and DllImport resolutions are cached per process. Reconfiguring mid-run
        // would be unsafe if any scan had already bound to the current handles.
        _prefs.LibFlacPath = _libFlacRow.TextBoxControl!.Text.Trim();
        _prefs.Mpg123Path = _mpg123Row.TextBoxControl!.Text.Trim();
    }

    private void RestoreOriginal()
    {
        _prefs.WorkerCountAuto = _origWorkerCountAuto;
        _prefs.WorkerCount = _origWorkerCount;
        _prefs.LibFlacPath = _origLibFlacPath;
        _prefs.Mpg123Path = _origMpg123Path;
    }

    private void UpdateThreadsLabel()
    {
        var autoChk = _autoThreadsRow.CheckBoxControl!;
        var track = _workerThreadsRow.SliderControl!;
        int value = autoChk.Checked ? Environment.ProcessorCount : track.Value;
        _workerThreadsRow.ValueLabel!.Text = $"{value} thread{(value == 1 ? "" : "s")}";
    }

    private void RefreshNativeLibraryStatus()
    {
        ApplyStatus(_libFlacRow, NativeLibraryLoader.ValidateLibFlac);
        ApplyStatus(_mpg123Row, NativeLibraryLoader.ValidateMpg123);
    }

    private void ApplyStatus(OptionRow row, Func<string, NativeLibraryStatus> validator)
    {
        var textBox = row.TextBoxControl!;
        var pic = row.StatusPicture!;
        var label = row.StatusLabel!;
        string path = textBox.Text.Trim();
        var status = validator(path);
        bool empty = string.IsNullOrWhiteSpace(path);

        switch (status)
        {
            case NativeLibraryStatus.Found:
                pic.Image = _statusValidIcon;
                label.ForeColor = Color.ForestGreen;
                label.Text = empty ? "Found in PATH" : "Found";
                break;

            case NativeLibraryStatus.Missing:
                pic.Image = null;
                label.ForeColor = SystemColors.ControlText;
                label.Text = "Not found in PATH";
                break;

            case NativeLibraryStatus.Error:
            default:
                pic.Image = _statusInvalidIcon;
                label.ForeColor = Color.Firebrick;
                label.Text = "File not found or not a loadable DLL";
                break;
        }
    }

    private Control BuildFallbackNote()
    {
        _infoIcon ??= ToolStripIcons.Load(ToolStripIcons.Information);

        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Margin = new Padding(0, 8, 0, 0),
            Padding = Padding.Empty,
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var pic = new PictureBox
        {
            Image = _infoIcon,
            Size = new Size(16, 16),
            SizeMode = PictureBoxSizeMode.CenterImage,
            Margin = new Padding(0, 2, 6, 0),
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
        };
        var text = new Label
        {
            Text =
                "Leave a path empty to use the DLL placed next to the executable "
                + "or in the system PATH.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            MaximumSize = new Size(480, 0),
            Margin = Padding.Empty,
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
        };
        row.Controls.Add(pic, 0, 0);
        row.Controls.Add(text, 1, 0);
        return row;
    }
}
