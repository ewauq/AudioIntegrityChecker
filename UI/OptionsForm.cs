using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AudioIntegrityChecker.UI;

/// <summary>
/// Modal options dialog with a category list on the left and the matching
/// settings panel on the right. Settings are persisted via
/// <see cref="UserPreferences"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class OptionsForm : Form
{
    private readonly UserPreferences _prefs;

    /// <summary>
    /// Fired after Save() during Apply or OK, so the caller can propagate
    /// live changes (worker count, DLL paths) to a running pipeline or the
    /// status bar.
    /// </summary>
    public event Action? SettingsApplied;

    // Snapshot of original values so Cancel can discard pending edits.
    private readonly bool _origWorkerCountAuto;
    private readonly int _origWorkerCount;
    private readonly string _origLibFlacCustomPath;
    private readonly string _origMpg123CustomPath;

    private readonly ListBox _categoryList;
    private readonly Panel _contentHost;
    private readonly Panel _performancePanel;
    private readonly Panel _toolsPanel;

    private readonly CheckBox _autoThreadsCheck;
    private readonly TrackBar _threadsTrackBar;
    private readonly Label _threadsValueLabel;

    private readonly TextBox _libFlacPathBox;
    private readonly PictureBox _libFlacStatusIcon;
    private readonly TextBox _mpg123PathBox;
    private readonly PictureBox _mpg123StatusIcon;

    private readonly Image _acceptIcon;
    private readonly Image _crossIcon;

    internal OptionsForm(UserPreferences prefs)
    {
        _prefs = prefs;
        _origWorkerCountAuto = prefs.WorkerCountAuto;
        _origWorkerCount = prefs.WorkerCount;
        _origLibFlacCustomPath = prefs.LibFlacCustomPath;
        _origMpg123CustomPath = prefs.Mpg123CustomPath;

        _acceptIcon = LoadEmbeddedIcon("accept_button.png");
        _crossIcon = LoadEmbeddedIcon("cross.png");

        Text = "Options";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(640, 460);

        _categoryList = new ListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            IntegralHeight = false,
            Font = new Font(Font.FontFamily, 9.5f),
        };
        _categoryList.Items.Add("Performance");
        _categoryList.Items.Add("Tools");
        _categoryList.SelectedIndex = 0;
        _categoryList.SelectedIndexChanged += OnCategoryChanged;

        var categoryListContainer = new Panel
        {
            Dock = DockStyle.Left,
            Width = 160,
            Padding = new Padding(4),
        };
        categoryListContainer.Controls.Add(_categoryList);

        _contentHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16) };

        _autoThreadsCheck = new CheckBox();
        _threadsTrackBar = new TrackBar();
        _threadsValueLabel = new Label();
        _performancePanel = BuildPerformancePanel();

        _libFlacPathBox = new TextBox();
        _libFlacStatusIcon = new PictureBox();
        _mpg123PathBox = new TextBox();
        _mpg123StatusIcon = new PictureBox();
        _toolsPanel = BuildToolsPanel();

        _contentHost.Controls.Add(_performancePanel);
        _contentHost.Controls.Add(_toolsPanel);

        var buttonPanel = BuildButtonPanel();

        Controls.Add(_contentHost);
        Controls.Add(categoryListContainer);
        Controls.Add(buttonPanel);

        LoadFromPreferences();
        RefreshDllStatus();
        ShowCategory(0);
    }

    private Panel BuildPerformancePanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Visible = false };

        var title = new Label
        {
            Text = "Performance",
            Font = new Font(Font.FontFamily, 12f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(0, 0),
        };
        panel.Controls.Add(title);

        var threadsHeader = new Label
        {
            Text = "Threads",
            Font = new Font(Font.FontFamily, 10f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(0, 42),
        };
        panel.Controls.Add(threadsHeader);

        _autoThreadsCheck.Text = "Automatic (adapt to storage type)";
        _autoThreadsCheck.AutoSize = true;
        _autoThreadsCheck.Location = new Point(0, 70);
        _autoThreadsCheck.CheckedChanged += (_, _) =>
        {
            _threadsTrackBar.Enabled = !_autoThreadsCheck.Checked;
            UpdateThreadsLabel();
        };
        panel.Controls.Add(_autoThreadsCheck);

        var autoInfoLabel = new Label
        {
            Text =
                "Automatic mode picks the optimal thread count based on the storage\n"
                + "type detected for each scan (HDD, SATA SSD, or NVMe).",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Location = new Point(18, 94),
        };
        panel.Controls.Add(autoInfoLabel);

        int processorCount = Environment.ProcessorCount;
        _threadsTrackBar.Minimum = 1;
        _threadsTrackBar.Maximum = processorCount;
        _threadsTrackBar.TickFrequency = 1;
        _threadsTrackBar.SmallChange = 1;
        _threadsTrackBar.LargeChange = 2;
        _threadsTrackBar.Location = new Point(0, 146);
        _threadsTrackBar.Size = new Size(320, 45);
        _threadsTrackBar.ValueChanged += (_, _) => UpdateThreadsLabel();
        panel.Controls.Add(_threadsTrackBar);

        _threadsValueLabel.AutoSize = true;
        _threadsValueLabel.Location = new Point(335, 156);
        panel.Controls.Add(_threadsValueLabel);

        var cpuInfoLabel = new Label
        {
            Text = $"Your CPU has {processorCount} logical cores.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Location = new Point(0, 200),
        };
        panel.Controls.Add(cpuInfoLabel);

        var applyNoteLabel = new Label
        {
            Text = "Changes apply to the next scan.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Font = new Font(Font.FontFamily, 8.5f, FontStyle.Italic),
            Location = new Point(0, 222),
        };
        panel.Controls.Add(applyNoteLabel);

        return panel;
    }

    private Panel BuildToolsPanel()
    {
        var panel = new Panel { Dock = DockStyle.Fill, Visible = false };

        var title = new Label
        {
            Text = "Tools",
            Font = new Font(Font.FontFamily, 12f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(0, 0),
        };
        panel.Controls.Add(title);

        BuildDllBlock(
            panel,
            y: 42,
            title: "libFLAC",
            defaultFileName: "libFLAC.dll",
            pathBox: _libFlacPathBox,
            statusIcon: _libFlacStatusIcon,
            getCustomPath: () => _prefs.LibFlacCustomPath,
            setCustomPath: p => _prefs.LibFlacCustomPath = p
        );

        BuildDllBlock(
            panel,
            y: 180,
            title: "mpg123",
            defaultFileName: "mpg123.dll",
            pathBox: _mpg123PathBox,
            statusIcon: _mpg123StatusIcon,
            getCustomPath: () => _prefs.Mpg123CustomPath,
            setCustomPath: p => _prefs.Mpg123CustomPath = p
        );

        var restartNote = new Label
        {
            Text = "Library path changes take effect after restarting the application.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Font = new Font(Font.FontFamily, 8.5f, FontStyle.Italic),
            Location = new Point(0, 320),
        };
        panel.Controls.Add(restartNote);

        return panel;
    }

    private void BuildDllBlock(
        Panel parent,
        int y,
        string title,
        string defaultFileName,
        TextBox pathBox,
        PictureBox statusIcon,
        Func<string> getCustomPath,
        Action<string> setCustomPath
    )
    {
        var header = new Label
        {
            Text = title,
            Font = new Font(Font.FontFamily, 10f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(0, y),
        };
        parent.Controls.Add(header);

        pathBox.ReadOnly = true;
        pathBox.Location = new Point(0, y + 28);
        pathBox.Size = new Size(420, 23);
        pathBox.BackColor = SystemColors.Window;
        parent.Controls.Add(pathBox);

        statusIcon.Size = new Size(16, 16);
        statusIcon.Location = new Point(428, y + 31);
        statusIcon.SizeMode = PictureBoxSizeMode.AutoSize;
        parent.Controls.Add(statusIcon);

        var browseBtn = new Button
        {
            Text = "Browse…",
            Location = new Point(0, y + 58),
            Size = new Size(90, 26),
        };
        browseBtn.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog
            {
                Filter = $"{defaultFileName}|{defaultFileName}|DLL files (*.dll)|*.dll",
                Title = $"Select {defaultFileName}",
                CheckFileExists = true,
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                setCustomPath(dialog.FileName);
                pathBox.Text = dialog.FileName;
                UpdateDllStatusIcon(statusIcon, dialog.FileName, defaultFileName);
            }
        };
        parent.Controls.Add(browseBtn);

        var searchBtn = new Button
        {
            Text = "Search in PATH",
            Location = new Point(98, y + 58),
            Size = new Size(130, 26),
        };
        searchBtn.Click += (_, _) =>
        {
            var hit = FindInPath(defaultFileName);
            if (hit is null)
            {
                MessageBox.Show(
                    this,
                    $"{defaultFileName} not found in PATH.",
                    "Search in PATH",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
                return;
            }
            setCustomPath(hit);
            pathBox.Text = hit;
            UpdateDllStatusIcon(statusIcon, hit, defaultFileName);
        };
        parent.Controls.Add(searchBtn);

        var resetBtn = new Button
        {
            Text = "Reset to default",
            Location = new Point(236, y + 58),
            Size = new Size(130, 26),
        };
        resetBtn.Click += (_, _) =>
        {
            setCustomPath(string.Empty);
            pathBox.Text = "(using default resolution)";
            UpdateDllStatusIcon(statusIcon, string.Empty, defaultFileName);
        };
        parent.Controls.Add(resetBtn);
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

        _libFlacPathBox.Text = string.IsNullOrEmpty(_prefs.LibFlacCustomPath)
            ? "(using default resolution)"
            : _prefs.LibFlacCustomPath;
        _mpg123PathBox.Text = string.IsNullOrEmpty(_prefs.Mpg123CustomPath)
            ? "(using default resolution)"
            : _prefs.Mpg123CustomPath;
    }

    private void CommitDraft()
    {
        _prefs.WorkerCountAuto = _autoThreadsCheck.Checked;
        _prefs.WorkerCount = _threadsTrackBar.Value;
        // DLL paths are mutated directly by Browse/Search/Reset handlers.
    }

    private void RestoreOriginal()
    {
        _prefs.WorkerCountAuto = _origWorkerCountAuto;
        _prefs.WorkerCount = _origWorkerCount;
        _prefs.LibFlacCustomPath = _origLibFlacCustomPath;
        _prefs.Mpg123CustomPath = _origMpg123CustomPath;
    }

    private void UpdateThreadsLabel()
    {
        int value = _autoThreadsCheck.Checked ? Environment.ProcessorCount : _threadsTrackBar.Value;
        _threadsValueLabel.Text = $"{value} thread{(value == 1 ? "" : "s")}";
    }

    private void RefreshDllStatus()
    {
        UpdateDllStatusIcon(_libFlacStatusIcon, _prefs.LibFlacCustomPath, "libFLAC.dll");
        UpdateDllStatusIcon(_mpg123StatusIcon, _prefs.Mpg123CustomPath, "mpg123.dll");
    }

    private void UpdateDllStatusIcon(PictureBox icon, string customPath, string defaultFileName)
    {
        bool ok = IsDllLoadable(customPath, defaultFileName);
        icon.Image = ok ? _acceptIcon : _crossIcon;
    }

    private static bool IsDllLoadable(string customPath, string defaultFileName)
    {
        try
        {
            IntPtr handle;
            if (!string.IsNullOrEmpty(customPath))
            {
                if (!File.Exists(customPath))
                    return false;
                handle = NativeLibrary.Load(customPath);
            }
            else if (!NativeLibrary.TryLoad(defaultFileName, out handle))
            {
                return false;
            }

            NativeLibrary.Free(handle);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindInPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
            return null;

        foreach (
            var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        )
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // Invalid PATH entry, skip.
            }
        }
        return null;
    }

    private void ShowCategory(int index)
    {
        _performancePanel.Visible = index == 0;
        _toolsPanel.Visible = index == 1;
    }

    private void OnCategoryChanged(object? sender, EventArgs e)
    {
        ShowCategory(_categoryList.SelectedIndex);
    }

    private static Image LoadEmbeddedIcon(string name)
    {
        var assembly = typeof(OptionsForm).Assembly;
        var resourceName = $"AudioIntegrityChecker.UI.Icons.{name}";
        using var stream =
            assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
        return Image.FromStream(stream);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _acceptIcon.Dispose();
            _crossIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
