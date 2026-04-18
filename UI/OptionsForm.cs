using System.Runtime.Versioning;

namespace AudioIntegrityChecker.UI;

[SupportedOSPlatform("windows")]
internal sealed class OptionsForm : Form
{
    private readonly UserPreferences _prefs;

    public event Action? SettingsApplied;

    private readonly bool _origWorkerCountAuto;
    private readonly int _origWorkerCount;

    private readonly OptionRow _autoThreadsRow;
    private readonly OptionRow _workerThreadsRow;

    internal OptionsForm(UserPreferences prefs)
    {
        _prefs = prefs;
        _origWorkerCountAuto = prefs.WorkerCountAuto;
        _origWorkerCount = prefs.WorkerCount;

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

        var tabControl = BuildTabControl();
        var buttonPanel = BuildButtonPanel();

        Controls.Add(tabControl);
        Controls.Add(buttonPanel);

        WireUpEventHandlers();
        LoadFromPreferences();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        MinimumSize = new Size(
            480 + (Width - ClientSize.Width),
            360 + (Height - ClientSize.Height)
        );
    }

    private TabControl BuildTabControl()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill };

        var performanceTab = new TabPage("Performance")
        {
            Padding = new Padding(20, 16, 20, 16),
            BackColor = SystemColors.Control,
        };

        var tabLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            ColumnCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        tabLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        tabLayout.Controls.Add(_autoThreadsRow);
        tabLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        tabLayout.Controls.Add(_workerThreadsRow);
        tabLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        tabLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        performanceTab.Controls.Add(tabLayout);
        tabs.TabPages.Add(performanceTab);

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
    }

    private void LoadFromPreferences()
    {
        var autoChk = _autoThreadsRow.CheckBoxControl!;
        var track = _workerThreadsRow.SliderControl!;
        autoChk.Checked = _prefs.WorkerCountAuto;
        track.Value = Math.Clamp(_prefs.WorkerCount, track.Minimum, track.Maximum);
        track.Enabled = !_prefs.WorkerCountAuto;
        UpdateThreadsLabel();
    }

    private void CommitDraft()
    {
        _prefs.WorkerCountAuto = _autoThreadsRow.CheckBoxControl!.Checked;
        _prefs.WorkerCount = _workerThreadsRow.SliderControl!.Value;
    }

    private void RestoreOriginal()
    {
        _prefs.WorkerCountAuto = _origWorkerCountAuto;
        _prefs.WorkerCount = _origWorkerCount;
    }

    private void UpdateThreadsLabel()
    {
        var autoChk = _autoThreadsRow.CheckBoxControl!;
        var track = _workerThreadsRow.SliderControl!;
        int value = autoChk.Checked ? Environment.ProcessorCount : track.Value;
        _workerThreadsRow.ValueLabel!.Text = $"{value} thread{(value == 1 ? "" : "s")}";
    }
}
