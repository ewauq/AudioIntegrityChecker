using System.Runtime.Versioning;

namespace AudioIntegrityChecker.UI;

/// <summary>
/// Modal settings dialog. Currently exposes only the worker count control
/// (automatic vs manual). Persisted via <see cref="UserPreferences"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class OptionsForm : Form
{
    private readonly UserPreferences _prefs;

    public event Action? SettingsApplied;

    private readonly bool _origWorkerCountAuto;
    private readonly int _origWorkerCount;

    private readonly CheckBox _autoThreadsCheck;
    private readonly TrackBar _threadsTrackBar;
    private readonly Label _threadsValueLabel;

    internal OptionsForm(UserPreferences prefs)
    {
        _prefs = prefs;
        _origWorkerCountAuto = prefs.WorkerCountAuto;
        _origWorkerCount = prefs.WorkerCount;

        Text = "Options";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(480, 300);

        var contentHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16) };

        _autoThreadsCheck = new CheckBox();
        _threadsTrackBar = new TrackBar();
        _threadsValueLabel = new Label();
        BuildPerformancePanel(contentHost);

        var buttonPanel = BuildButtonPanel();

        Controls.Add(contentHost);
        Controls.Add(buttonPanel);

        LoadFromPreferences();
    }

    private void BuildPerformancePanel(Panel host)
    {
        var title = new Label
        {
            Text = "Performance",
            Font = new Font(Font.FontFamily, 12f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(0, 0),
        };
        host.Controls.Add(title);

        var threadsHeader = new Label
        {
            Text = "Threads",
            Font = new Font(Font.FontFamily, 10f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(0, 42),
        };
        host.Controls.Add(threadsHeader);

        _autoThreadsCheck.Text = "Automatic (adapt to storage type)";
        _autoThreadsCheck.AutoSize = true;
        _autoThreadsCheck.Location = new Point(0, 70);
        _autoThreadsCheck.CheckedChanged += (_, _) =>
        {
            _threadsTrackBar.Enabled = !_autoThreadsCheck.Checked;
            UpdateThreadsLabel();
        };
        host.Controls.Add(_autoThreadsCheck);

        var autoInfoLabel = new Label
        {
            Text =
                "Automatic mode picks the optimal thread count based on the storage\n"
                + "type detected for each scan (HDD, SATA SSD, or NVMe).",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Location = new Point(18, 94),
        };
        host.Controls.Add(autoInfoLabel);

        int processorCount = Environment.ProcessorCount;
        _threadsTrackBar.Minimum = 1;
        _threadsTrackBar.Maximum = processorCount;
        _threadsTrackBar.TickFrequency = 1;
        _threadsTrackBar.SmallChange = 1;
        _threadsTrackBar.LargeChange = 2;
        _threadsTrackBar.Location = new Point(0, 146);
        _threadsTrackBar.Size = new Size(320, 45);
        _threadsTrackBar.ValueChanged += (_, _) => UpdateThreadsLabel();
        host.Controls.Add(_threadsTrackBar);

        _threadsValueLabel.AutoSize = true;
        _threadsValueLabel.Location = new Point(335, 156);
        host.Controls.Add(_threadsValueLabel);

        var cpuInfoLabel = new Label
        {
            Text = $"Your CPU has {processorCount} logical cores.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Location = new Point(0, 200),
        };
        host.Controls.Add(cpuInfoLabel);

        var applyNoteLabel = new Label
        {
            Text = "Changes apply to the next scan.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Font = new Font(Font.FontFamily, 8.5f, FontStyle.Italic),
            Location = new Point(0, 222),
        };
        host.Controls.Add(applyNoteLabel);
    }

    private FlowLayoutPanel BuildButtonPanel()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12, 10, 12, 10),
        };

        var okBtn = new Button { Text = "OK", Size = new Size(90, 28) };
        okBtn.Click += (_, _) =>
        {
            CommitDraft();
            _prefs.Save();
            SettingsApplied?.Invoke();
            DialogResult = DialogResult.OK;
            Close();
        };

        var cancelBtn = new Button { Text = "Cancel", Size = new Size(90, 28) };
        cancelBtn.Click += (_, _) =>
        {
            RestoreOriginal();
            DialogResult = DialogResult.Cancel;
            Close();
        };

        var applyBtn = new Button { Text = "Apply", Size = new Size(90, 28) };
        applyBtn.Click += (_, _) =>
        {
            CommitDraft();
            _prefs.Save();
            SettingsApplied?.Invoke();
        };

        panel.Controls.Add(applyBtn);
        panel.Controls.Add(cancelBtn);
        panel.Controls.Add(okBtn);

        AcceptButton = okBtn;
        CancelButton = cancelBtn;

        return panel;
    }

    private void LoadFromPreferences()
    {
        _autoThreadsCheck.Checked = _prefs.WorkerCountAuto;
        _threadsTrackBar.Value = Math.Clamp(
            _prefs.WorkerCount,
            _threadsTrackBar.Minimum,
            _threadsTrackBar.Maximum
        );
        _threadsTrackBar.Enabled = !_prefs.WorkerCountAuto;
        UpdateThreadsLabel();
    }

    private void CommitDraft()
    {
        _prefs.WorkerCountAuto = _autoThreadsCheck.Checked;
        _prefs.WorkerCount = _threadsTrackBar.Value;
    }

    private void RestoreOriginal()
    {
        _prefs.WorkerCountAuto = _origWorkerCountAuto;
        _prefs.WorkerCount = _origWorkerCount;
    }

    private void UpdateThreadsLabel()
    {
        int value = _autoThreadsCheck.Checked ? Environment.ProcessorCount : _threadsTrackBar.Value;
        _threadsValueLabel.Text = $"{value} thread{(value == 1 ? "" : "s")}";
    }
}
