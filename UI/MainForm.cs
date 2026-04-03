using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using AudioIntegrityChecker.Checkers.Flac;
using AudioIntegrityChecker.Checkers.Mp3;
using AudioIntegrityChecker.Core;
using AudioIntegrityChecker.Pipeline;
using TheArtOfDev.HtmlRenderer.WinForms;

namespace AudioIntegrityChecker.UI;

[SupportedOSPlatform("windows")]
public sealed class MainForm : Form
{
    private readonly CheckerRegistry _registry = new();
    private AnalysisPipeline? _pipeline;
    private CancellationTokenSource? _analysisCts;
    private CancellationTokenSource? _scanCts;
    private readonly List<string> _queuedFiles = [];
    private readonly Dictionary<string, ListViewItem> _itemByPath = new(
        StringComparer.OrdinalIgnoreCase
    );

    private int _totalFiles;
    private int _completedFiles;
    private long _totalBytes;
    private TimeSpan _totalDuration;

    private bool _isAnalysing;
    private int _sortColumn = -1;
    private bool _sortAscending = true;

    private readonly BufferedListView _listView;
    private readonly ProgressBar _globalBar;
    private readonly Panel _globalBarWrapper;
    private readonly Button _startButton;
    private readonly Button _cancelButton;
    private readonly Label _statusLabel;
    private readonly Panel _bottomPanel;
    private readonly StatusStrip _statusStrip;
    private readonly ToolStripStatusLabel _labelFiles;
    private readonly ToolStripStatusLabel _labelSize;
    private readonly ToolStripStatusLabel _labelDuration;
    private readonly ToolStripStatusLabel _labelRam;
    private readonly ToolStripStatusLabel _labelWorkers;
    private readonly ToolStripSeparator _sepSize;
    private readonly ToolStripSeparator _sepDuration;
    private readonly System.Windows.Forms.Timer _ramTimer;
    private readonly SplitContainer _splitContainer;
    private readonly HtmlPanel _htmlPanel;
    private readonly MenuStrip _menuStrip;
    private readonly ToolStripMenuItem _menuViewHelpPanel;

    private int _countOk;
    private int _countMetadata;
    private int _countIndex;
    private int _countStructure;
    private int _countCorruption;
    private int _countError;

    // DLL / binary status (static, set once on startup)
    private readonly ToolStripStatusLabel _labelLibFlac;
    private readonly ToolStripStatusLabel _labelMpg123;
    private readonly ToolStripSeparator _sepDlls;

    private const int ColDuration = 2;
    private const int ColFormat = 3;
    private const int ColResult = 4;
    private const int ColSeverity = 5;
    private const int ColMessage = 6;
    private const int ColError = 7;

    private const int ButtonRowHeight = 40;
    private const int GlobalBarHeight = 20;

    // Initial column widths in pixels (user-resizable at runtime)
    private const int ColDirWidth = 200;
    private const int ColFileWidth = 200;
    private const int ColDurationWidth = 65;
    private const int ColFormatWidth = 55;
    private const int ColResultWidth = 58;
    private const int ColSeverityWidth = 75;
    private const int ColMessageWidth = 420;
    private const int ColErrorWidth = 200;

    private const int HelpPanelWidth = 300;
    private const int RamUpdateIntervalMs = 1_000; // 1 s refresh for the RAM indicator in the status bar

    public MainForm()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version!;
        Text = $"Audio Integrity Checker — {v.Major}.{v.Minor}.{v.Build}";
        MinimumSize = new Size(900, 560);
        Size = new Size(1080, 640);
        StartPosition = FormStartPosition.CenterScreen;

        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;

        _listView = new BufferedListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            OwnerDraw = true,
        };
        _listView.Columns.Add("Directory", ColDirWidth);
        _listView.Columns.Add("File", ColFileWidth);
        _listView.Columns.Add(
            new ColumnHeader
            {
                Text = "Duration",
                Width = ColDurationWidth,
                TextAlign = HorizontalAlignment.Center,
            }
        );
        _listView.Columns.Add(
            new ColumnHeader
            {
                Text = "Format",
                Width = ColFormatWidth,
                TextAlign = HorizontalAlignment.Center,
            }
        );
        _listView.Columns.Add(
            new ColumnHeader
            {
                Text = "Result",
                Width = ColResultWidth,
                TextAlign = HorizontalAlignment.Center,
            }
        );
        _listView.Columns.Add(
            new ColumnHeader
            {
                Text = "Severity",
                Width = ColSeverityWidth,
                TextAlign = HorizontalAlignment.Center,
            }
        );
        _listView.Columns.Add("Message", ColMessageWidth);
        _listView.Columns.Add("Error", ColErrorWidth);
        _listView.ColumnClick += OnColumnClick;
        _listView.ItemActivate += OnItemActivate;
        _listView.KeyDown += OnListViewKeyDown;
        _listView.DrawColumnHeader += (_, e) => e.DrawDefault = true;
        _listView.DrawItem += (_, _) => { };
        _listView.DrawSubItem += OnDrawSubItem;

        var listPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 0, 6, 6) };
        listPanel.Controls.Add(_listView);

        _htmlPanel = new HtmlPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Text = HelpContent.GetHtml(null),
        };

        var helpBorderPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(1, 1, 0, 0),
            BackColor = SystemColors.ControlDark,
        };
        var helpInnerPanel = new Panel { Dock = DockStyle.Fill, BackColor = SystemColors.Window };
        helpInnerPanel.Controls.Add(_htmlPanel);
        helpBorderPanel.Controls.Add(helpInnerPanel);

        _bottomPanel = new Panel { Dock = DockStyle.Bottom, Height = ButtonRowHeight };

        _startButton = new Button
        {
            Text = "Start scan",
            Width = 90,
            Height = 26,
            Enabled = false,
            Margin = new Padding(1, 0, 4, 0),
        };
        _cancelButton = new Button
        {
            Text = "Clear",
            Width = 80,
            Height = 26,
            Enabled = false,
            Margin = new Padding(0),
        };
        _statusLabel = new Label
        {
            Text = "Drop audio files into the window and click Start scan.",
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Width = 600,
            Height = 26,
        };

        _statusLabel.Margin = new Padding(4, 0, 0, 0);

        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = ButtonRowHeight,
            Padding = new Padding(4, 4, 4, 0),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
        };
        buttonRow.Controls.Add(_startButton);
        buttonRow.Controls.Add(_cancelButton);
        buttonRow.Controls.Add(_statusLabel);

        // Left content area: list + buttons + progress bar
        var leftPanel = new Panel { Dock = DockStyle.Fill };

        _splitContainer = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            FixedPanel = FixedPanel.Panel2,
        };
        _splitContainer.Panel1.Controls.Add(listPanel);
        _splitContainer.Panel1.Controls.Add(_bottomPanel);
        _splitContainer.Panel2.Controls.Add(helpBorderPanel);

        _globalBarWrapper = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 0,
            Padding = new Padding(4, 0, 4, 4),
            Visible = false,
        };
        _globalBar = new ProgressBar
        {
            Dock = DockStyle.Fill,
            Minimum = 0,
            Maximum = 100,
            Value = 0,
        };
        _globalBarWrapper.Controls.Add(_globalBar);

        _bottomPanel.Controls.Add(_globalBarWrapper);
        _bottomPanel.Controls.Add(buttonRow);

        // ---- Menu strip ----
        var menuFile = new ToolStripMenuItem("&File");
        var menuAddFolder = new ToolStripMenuItem("Add folder...", null, OnMenuAddFolder);
        var menuAddFiles = new ToolStripMenuItem("Add files...", null, OnMenuAddFiles);
        var menuExit = new ToolStripMenuItem("Exit", null, (_, _) => Close());
        menuFile.DropDownItems.AddRange([
            menuAddFolder,
            menuAddFiles,
            new ToolStripSeparator(),
            menuExit,
        ]);

        var menuView = new ToolStripMenuItem("&View");
        _menuViewHelpPanel = new ToolStripMenuItem("Show help panel", null, OnMenuToggleHelpPanel)
        {
            Checked = true,
        };
        var menuAutoResize = new ToolStripMenuItem(
            "Auto resize columns",
            null,
            OnMenuAutoResizeColumns
        );
        menuView.DropDownItems.AddRange([
            _menuViewHelpPanel,
            new ToolStripSeparator(),
            menuAutoResize,
        ]);

        _menuStrip = new MenuStrip();
        _menuStrip.Items.AddRange([menuFile, menuView]);

        // ---- Column header context menu ----
        BuildColumnHeaderContextMenu();

        _labelFiles = new ToolStripStatusLabel();
        _sepSize = new ToolStripSeparator { Visible = false };
        _labelSize = new ToolStripStatusLabel { Visible = false };
        _sepDuration = new ToolStripSeparator { Visible = false };
        _labelDuration = new ToolStripStatusLabel { Visible = false };
        int workerCount = Math.Min(Environment.ProcessorCount, 8);
        _labelRam = new ToolStripStatusLabel("RAM: —");
        var sepWorkers = new ToolStripSeparator();
        _labelWorkers = new ToolStripStatusLabel($"Workers: {workerCount}");
        _sepDlls = new ToolStripSeparator();
        _labelLibFlac = new ToolStripStatusLabel();
        var sepMpg123 = new ToolStripSeparator();
        _labelMpg123 = new ToolStripStatusLabel();
        var spring = new ToolStripStatusLabel { Spring = true };

        _statusStrip = new StatusStrip();
        _statusStrip.Items.AddRange([
            _labelFiles,
            _sepSize,
            _labelSize,
            _sepDuration,
            _labelDuration,
            spring,
            _labelRam,
            sepWorkers,
            _labelWorkers,
            _sepDlls,
            _labelLibFlac,
            sepMpg123,
            _labelMpg123,
        ]);

        _ramTimer = new System.Windows.Forms.Timer { Interval = RamUpdateIntervalMs };
        _ramTimer.Tick += (_, _) =>
            _labelRam.Text = $"RAM: {FormatBytes(Process.GetCurrentProcess().WorkingSet64)}";
        _ramTimer.Start();

        Controls.Add(_splitContainer);
        Controls.Add(_menuStrip);
        Controls.Add(_statusStrip);
        MainMenuStrip = _menuStrip;

        AllowDrop = true;
        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        _listView.AllowDrop = true;
        _listView.DragEnter += OnDragEnter;
        _listView.DragDrop += OnDragDrop;
        _startButton.Click += OnStartClick;
        _cancelButton.Click += OnCancelClick;
        _listView.SelectedIndexChanged += OnSelectedIndexChanged;

        RegisterCheckers();

        _labelLibFlac.Text =
            $"libFLAC: {(NativeFlacChecker.IsLibraryAvailable() ? "available" : "not available")}";
        _labelMpg123.Text =
            $"mpg123: {(Mp3Mpg123Backend.IsLibraryAvailable() ? "available" : "not available")}";
        // Cancel any in-flight work before the form is destroyed so that mpg123
        // worker calls finish before Shutdown() tears down the native library.
        FormClosing += OnFormClosing;
        FormClosed += (_, _) => Mp3Mpg123Backend.Shutdown();

        Load += OnFormLoad;
    }

    private void OnFormLoad(object? sender, EventArgs e)
    {
        var prefs = UserPreferences.Load();

        // Restore window size and position
        if (prefs.WindowWidth > 0 && prefs.WindowHeight > 0)
            Size = new Size(prefs.WindowWidth, prefs.WindowHeight);

        if (prefs.WindowX != int.MinValue && prefs.WindowY != int.MinValue)
        {
            var restored = new Rectangle(prefs.WindowX, prefs.WindowY, Width, Height);
            bool onScreen = Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(restored));
            if (onScreen)
            {
                StartPosition = FormStartPosition.Manual;
                Location = new Point(prefs.WindowX, prefs.WindowY);
            }
        }

        if (prefs.WindowMaximized)
            WindowState = FormWindowState.Maximized;

        // Restore help panel state
        if (prefs.HelpPanelVisible)
        {
            _splitContainer.Panel2MinSize = HelpPanelWidth;
            _splitContainer.SplitterDistance = _splitContainer.Width - HelpPanelWidth;
        }
        else
        {
            _splitContainer.Panel2Collapsed = true;
            _menuViewHelpPanel.Checked = false;
        }

        // Restore hidden columns
        if (prefs.HiddenColumns.Count > 0)
        {
            foreach (int colIndex in prefs.HiddenColumns)
            {
                if (colIndex < _listView.Columns.Count)
                {
                    _hiddenColumnWidths[colIndex] = _listView.Columns[colIndex].Width;
                    _listView.Columns[colIndex].Width = 0;
                }
            }

            // Sync context menu checkmarks
            if (_listView.HeaderContextMenuStrip is ContextMenuStrip menu)
            {
                foreach (ToolStripMenuItem item in menu.Items)
                {
                    if (item.Tag is int idx && prefs.HiddenColumns.Contains(idx))
                        item.Checked = false;
                }
            }
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _scanCts?.Cancel();
        _analysisCts?.Cancel();

        var prefs = new UserPreferences
        {
            WindowMaximized = WindowState == FormWindowState.Maximized,
            HelpPanelVisible = !_splitContainer.Panel2Collapsed,
            HiddenColumns = new HashSet<int>(_hiddenColumnWidths.Keys),
        };

        // Save normal (non-maximized) bounds
        var restoreBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        prefs.WindowX = restoreBounds.X;
        prefs.WindowY = restoreBounds.Y;
        prefs.WindowWidth = restoreBounds.Width;
        prefs.WindowHeight = restoreBounds.Height;

        prefs.Save();
    }

    private void RegisterCheckers()
    {
        _registry.Register(
            "FLAC",
            nativeFactory: () => new NativeFlacChecker(),
            processFactory: () => new ProcessFlacChecker(),
            nativeAvailable: NativeFlacChecker.IsLibraryAvailable
        );

        _registry.Register(
            "MP3",
            nativeFactory: () => new Mp3Checker(),
            processFactory: () => new Mp3Checker(),
            nativeAvailable: () => true // Mp3Checker handles mpg123 absence internally
        );

        _registry.Build();
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect =
            e.Data?.GetDataPresent(DataFormats.FileDrop) == true
                ? DragDropEffects.Copy
                : DragDropEffects.None;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] droppedPaths)
            return;
        LoadPaths(droppedPaths);
    }

    private void LoadPaths(string[] paths)
    {
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();

        _queuedFiles.Clear();
        _itemByPath.Clear();
        _listView.Items.Clear();
        _totalBytes = 0;
        _totalDuration = TimeSpan.Zero;

        SetAnalysing(false);
        SetStatus("Scanning…");

        _ = ScanAsync(paths, _scanCts.Token);
    }

    private async Task ScanAsync(string[] droppedPaths, CancellationToken cancellationToken)
    {
        _globalBar.Style = ProgressBarStyle.Marquee;
        _globalBar.MarqueeAnimationSpeed = 30; // Windows marquee speed unit (lower = faster)
        ShowGlobalBar(true);

        List<FileEntry> entries;
        try
        {
            var supportedExtensions = _registry.SupportedExtensions.ToHashSet(
                StringComparer.OrdinalIgnoreCase
            );

            var scanProgress = new Progress<int>(count =>
                SetStatus($"Scanning… {count} file{(count == 1 ? "" : "s")} found")
            );

            entries = await Task.Run(
                () =>
                    FileCollector.Collect(
                        droppedPaths,
                        supportedExtensions,
                        cancellationToken,
                        scanProgress
                    ),
                cancellationToken
            );
        }
        catch (OperationCanceledException)
        {
            _globalBar.Style = ProgressBarStyle.Continuous;
            ShowGlobalBar(false);
            return;
        }

        _globalBar.Style = ProgressBarStyle.Continuous;
        _globalBar.Value = 0;
        ShowGlobalBar(false);

        if (cancellationToken.IsCancellationRequested)
            return;

        _listView.BeginUpdate();
        foreach (var entry in entries)
        {
            var format = _registry.Resolve(entry.FilePath)?.FormatId ?? entry.Format;
            var item = new ListViewItem(entry.DirectoryName)
            {
                ToolTipText = entry.FilePath,
                UseItemStyleForSubItems = false,
            };
            item.SubItems.Add(Path.GetFileName(entry.FilePath));
            item.SubItems.Add(FormatTrackDuration(entry.Duration));
            item.SubItems.Add(format);
            item.SubItems.Add(""); // Result
            item.SubItems.Add(""); // Severity
            item.SubItems.Add(""); // Message
            item.SubItems.Add(""); // Error

            _queuedFiles.Add(entry.FilePath);
            _itemByPath[entry.FilePath] = item;
            _totalBytes += entry.Bytes;
            if (entry.Duration.HasValue)
                _totalDuration += entry.Duration.Value;

            _listView.Items.Add(item);
        }
        _listView.EndUpdate();

        int fileCount = _queuedFiles.Count;
        SetStatus($"{fileCount} file{(fileCount == 1 ? "" : "s")} queued.");

        UpdateStatusBar();
        SetAnalysing(false);
    }

    private async void OnStartClick(object? sender, EventArgs e)
    {
        if (_queuedFiles.Count == 0)
            return;

        SetAnalysing(true);
        _countOk = 0;
        _countMetadata = 0;
        _countIndex = 0;
        _countStructure = 0;
        _countCorruption = 0;
        _countError = 0;
        _totalFiles = _queuedFiles.Count;
        _completedFiles = 0;

        foreach (ListViewItem item in _listView.Items)
        {
            item.SubItems[ColSeverity].ForeColor = _listView.ForeColor;
            item.SubItems[ColResult].Text = "Pending...";
            item.SubItems[ColSeverity].Text = "";
            item.SubItems[ColMessage].Text = "";
            item.SubItems[ColError].Text = "";
        }

        _globalBar.Value = 0;
        _globalBar.Maximum = _totalFiles;
        ShowGlobalBar(true);

        _pipeline = new AnalysisPipeline(_registry);
        _pipeline.FileCompleted += OnFileCompleted;
        _pipeline.FileProgressChanged += OnFileProgress;

        _analysisCts = new CancellationTokenSource();

        var globalProgress = new Progress<int>(completedCount =>
            BeginInvoke(() => _globalBar.Value = Math.Min(completedCount, _totalFiles))
        );

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await _pipeline.RunAsync(_queuedFiles, _analysisCts.Token, globalProgress);
            stopwatch.Stop();

            string timeText =
                stopwatch.Elapsed.TotalSeconds < 60 // format as "Xs" if under a minute, otherwise "Xm YYs"
                    ? $"{stopwatch.Elapsed.TotalSeconds:F1}s"
                    : $"{(int)stopwatch.Elapsed.TotalMinutes}m {stopwatch.Elapsed.Seconds:D2}s";
            int n = _completedFiles;
            SetStatus($"Processed {(n == 1 ? "1 file" : $"{n} files")} in {timeText}.");

            ShowCompletionDialog(stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            SetStatus("Cancelled.");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            SetStatus($"Pipeline error: {ex.Message}");
        }
        finally
        {
            _analysisCts.Dispose();
            _analysisCts = null;
            SetAnalysing(false);
            ShowGlobalBar(false);
            _ = Task.Run(TrimWorkingSet);
        }
    }

    private void OnCancelClick(object? sender, EventArgs e)
    {
        if (_isAnalysing)
        {
            _cancelButton.Enabled = false;
            SetStatus("Cancelling…");
            _analysisCts?.Cancel();
        }
        else
        {
            OnClear();
        }
    }

    private void OnClear()
    {
        _scanCts?.Cancel();

        _queuedFiles.Clear();
        _itemByPath.Clear();
        _listView.Items.Clear();
        _totalBytes = 0;
        _totalDuration = TimeSpan.Zero;
        _totalFiles = 0;
        _completedFiles = 0;
        _countOk = 0;
        _countMetadata = 0;
        _countIndex = 0;
        _countStructure = 0;
        _countCorruption = 0;
        _countError = 0;

        UpdateStatusBar();
        SetStatus("Drop audio files into the window and click Start scan.");
        SetAnalysing(false);
        TrimWorkingSet();
    }

    private void OnMenuToggleHelpPanel(object? sender, EventArgs e)
    {
        bool opening = _splitContainer.Panel2Collapsed;
        if (opening)
        {
            _splitContainer.Panel2Collapsed = false;
            _splitContainer.Panel2MinSize = HelpPanelWidth;
            _splitContainer.SplitterDistance = _splitContainer.Width - HelpPanelWidth;
            UpdateHelpPanel();
        }
        else
        {
            _splitContainer.Panel2MinSize = 0;
            _splitContainer.Panel2Collapsed = true;
        }

        _menuViewHelpPanel.Checked = opening;
    }

    private void OnMenuAddFolder(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog { Description = "Select a folder to scan" };
        if (dialog.ShowDialog() == DialogResult.OK)
            LoadPaths([dialog.SelectedPath]);
    }

    private void OnMenuAddFiles(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select audio files",
            Filter = "Audio files|*.flac;*.mp3|All files|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog() == DialogResult.OK)
            LoadPaths(dialog.FileNames);
    }

    private void OnMenuAutoResizeColumns(object? sender, EventArgs e)
    {
        _listView.AutoResizeColumns(ColumnHeaderAutoResizeStyle.ColumnContent);
    }

    // -------------------------------------------------------------------------
    // Column header context menu — show/hide optional columns
    // -------------------------------------------------------------------------

    // Columns that can be hidden; maps column index → saved width before hiding
    private readonly Dictionary<int, int> _hiddenColumnWidths = [];

    private static readonly (int Index, string Name, bool Optional)[] ColumnDefs =
    [
        (0, "Directory", true),
        (1, "File", false),
        (ColDuration, "Duration", true),
        (ColFormat, "Format", true),
        (ColResult, "Result", false),
        (ColSeverity, "Severity", true),
        (ColMessage, "Message", false),
        (ColError, "Error", true),
    ];

    private void BuildColumnHeaderContextMenu()
    {
        var contextMenu = new ContextMenuStrip();
        foreach (var (index, name, optional) in ColumnDefs)
        {
            if (!optional)
                continue;
            var menuItem = new ToolStripMenuItem(name)
            {
                Tag = index,
                Checked = true,
                CheckOnClick = true,
            };
            menuItem.CheckedChanged += OnColumnVisibilityChanged;
            contextMenu.Items.Add(menuItem);
        }

        _listView.ColumnWidthChanging += (_, args) =>
        {
            if (_hiddenColumnWidths.ContainsKey(args.ColumnIndex))
            {
                args.Cancel = true;
                args.NewWidth = 0;
            }
        };

        _listView.HeaderContextMenuStrip = contextMenu;
    }

    private void OnColumnVisibilityChanged(object? sender, EventArgs e)
    {
        if (sender is not ToolStripMenuItem menuItem || menuItem.Tag is not int colIndex)
            return;

        if (menuItem.Checked)
        {
            // Restore column
            if (_hiddenColumnWidths.TryGetValue(colIndex, out int savedWidth))
            {
                _listView.Columns[colIndex].Width = savedWidth;
                _hiddenColumnWidths.Remove(colIndex);
            }
        }
        else
        {
            // Hide column
            _hiddenColumnWidths[colIndex] = _listView.Columns[colIndex].Width;
            _listView.Columns[colIndex].Width = 0;
        }
    }

    private void OnSelectedIndexChanged(object? sender, EventArgs e)
    {
        if (!_splitContainer.Panel2Collapsed)
            UpdateHelpPanel();
    }

    private void UpdateHelpPanel()
    {
        string? diagnosticKey = null;
        if (_listView.SelectedItems.Count > 0)
        {
            var item = _listView.SelectedItems[0];
            var errorText = item.SubItems[ColError].Text;
            if (!string.IsNullOrEmpty(errorText))
                diagnosticKey = errorText;
        }

        _htmlPanel.Text = HelpContent.GetHtml(diagnosticKey);
    }

    private void SetAnalysing(bool active)
    {
        _isAnalysing = active;
        _cancelButton.Text = active ? "Cancel" : "Clear";
        _cancelButton.Enabled = active || _listView.Items.Count > 0;
        _startButton.Enabled = !active && _queuedFiles.Count > 0;
    }

    private void OnFileCompleted(FileCompletedEventArgs args)
    {
        // Increment all counters on the worker thread so they are visible to the await
        // continuation via the task memory barrier — no BeginInvoke delay needed.
        int completed = Interlocked.Increment(ref _completedFiles);
        switch (args.Result.Category)
        {
            case CheckCategory.Ok:
                Interlocked.Increment(ref _countOk);
                break;
            case CheckCategory.Metadata:
                Interlocked.Increment(ref _countMetadata);
                break;
            case CheckCategory.Index:
                Interlocked.Increment(ref _countIndex);
                break;
            case CheckCategory.Structure:
                Interlocked.Increment(ref _countStructure);
                break;
            case CheckCategory.Corruption:
                Interlocked.Increment(ref _countCorruption);
                break;
            case CheckCategory.Error:
                Interlocked.Increment(ref _countError);
                break;
        }

        BeginInvoke(() =>
        {
            if (!_itemByPath.TryGetValue(args.FilePath, out var item))
                return;

            var result = args.Result;
            bool isOk = result.Category == CheckCategory.Ok;
            var severity = ResultFormatting.GetSeverity(result.Category);
            var color = ResultFormatting.GetSeverityColor(severity);

            item.SubItems[ColResult].Text = isOk ? "OK" : "ISSUE";
            item.SubItems[ColSeverity].Text =
                severity == ResultSeverity.None ? "" : severity.ToString();
            item.SubItems[ColSeverity].ForeColor = color;
            item.SubItems[ColMessage].Text = ResultFormatting.BuildMessageColumnText(result);
            item.SubItems[ColError].Text = result.ErrorMessage ?? string.Empty;

            // Stop updating the status label once all files are accounted for —
            // the await continuation will overwrite it with the final summary.
            if (completed < _totalFiles)
                SetStatus($"Processing: {completed}/{_totalFiles}");
        });
    }

    private void OnFileProgress(FileProgressEventArgs args)
    {
        BeginInvoke(() =>
        {
            if (!_itemByPath.TryGetValue(args.FilePath, out var item))
                return;
            var current = item.SubItems[ColResult].Text;
            if (current is "OK" or "ISSUE")
                return;
            item.SubItems[ColResult].Text = $"{(int)(args.Progress.Value * 100)}%";
        });
    }

    // -------------------------------------------------------------------------
    // Double-click → reveal file in Explorer
    // -------------------------------------------------------------------------

    private void OnItemActivate(object? sender, EventArgs e)
    {
        if (_listView.SelectedItems.Count == 0)
            return;
        var filePath = _listView.SelectedItems[0].ToolTipText;
        if (string.IsNullOrEmpty(filePath))
            return;

        Process.Start(
            new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"")
            {
                UseShellExecute = true,
            }
        );
    }

    private void ShowCompletionDialog(TimeSpan elapsed)
    {
        string timeText =
            elapsed.TotalSeconds < 60
                ? $"{elapsed.TotalSeconds:F1}s"
                : $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:D2}s";

        var builder = new System.Text.StringBuilder();
        builder.AppendLine(
            $"Checked {_totalFiles} file{(_totalFiles == 1 ? "" : "s")} in {timeText}."
        );
        builder.AppendLine();

        bool hasSevere = _countCorruption > 0 || _countError > 0;
        bool hasMinor = _countStructure > 0 || _countIndex > 0 || _countMetadata > 0;

        if (!hasSevere && !hasMinor)
        {
            builder.Append("All files OK.");
        }
        else
        {
            if (_countCorruption > 0)
                builder.AppendLine(
                    $"{_countCorruption} CORRUPTION — audio data demonstrably damaged."
                );
            if (_countError > 0)
                builder.AppendLine(
                    $"{_countError} ERROR — analysis tool failed, file state unknown."
                );
            if (_countStructure > 0)
                builder.AppendLine(
                    $"{_countStructure} STRUCTURE — stream anomaly, audio likely intact."
                );
            if (_countIndex > 0)
                builder.AppendLine($"{_countIndex} INDEX — seek/frame count mismatch.");
            if (_countMetadata > 0)
                builder.Append(
                    $"{_countMetadata} METADATA — tag inconsistency, no playback impact."
                );
        }

        var icon =
            hasSevere ? MessageBoxIcon.Error
            : hasMinor ? MessageBoxIcon.Warning
            : MessageBoxIcon.Information;

        MessageBox.Show(
            builder.ToString().TrimEnd(),
            "Analysis Complete",
            MessageBoxButtons.OK,
            icon
        );
    }

    private void OnColumnClick(object? sender, ColumnClickEventArgs e)
    {
        if (e.Column == _sortColumn)
            _sortAscending = !_sortAscending;
        else
        {
            _sortColumn = e.Column;
            _sortAscending = true;
        }

        _listView.ListViewItemSorter = new ColumnSorter(_sortColumn, _sortAscending);
        _listView.Sort();
    }

    private void ShowGlobalBar(bool visible)
    {
        _globalBarWrapper.Visible = visible;
        _globalBarWrapper.Height = visible ? GlobalBarHeight + 4 : 0;
        _bottomPanel.Height =
            ButtonRowHeight + (_globalBarWrapper.Visible ? _globalBarWrapper.Height : 0);
    }

    private void UpdateStatusBar()
    {
        int fileCount = _queuedFiles.Count;
        _labelFiles.Text = $"{fileCount} file{(fileCount == 1 ? "" : "s")}";

        bool hasSize = _totalBytes > 0;
        bool hasDuration = _totalDuration > TimeSpan.Zero;

        _sepSize.Visible = hasSize;
        _labelSize.Visible = hasSize;
        if (hasSize)
            _labelSize.Text = FormatBytes(_totalBytes);

        _sepDuration.Visible = hasDuration;
        _labelDuration.Visible = hasDuration;
        if (hasDuration)
            _labelDuration.Text = FormatDuration(_totalDuration);
    }

    // -------------------------------------------------------------------------
    // Memory — trim OS working set after analysis
    // -------------------------------------------------------------------------

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(
        IntPtr processHandle,
        IntPtr minimumSize,
        IntPtr maximumSize
    );

    private static void TrimWorkingSet()
    {
        // Force a full Gen2 GC with LOH compaction to reclaim the large byte[] buffers
        // (File.ReadAllBytes per file) that accumulated on the Large Object Heap during analysis.
        // Without this, those buffers are eligible but won't be collected until the runtime
        // decides to run a Gen2 sweep on its own, which may not happen for a long time.
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        // Then tell Windows the now-empty pages can be paged out.
        SetProcessWorkingSetSize(
            Process.GetCurrentProcess().Handle,
            new IntPtr(-1),
            new IntPtr(-1)
        );
    }

    private static string FormatTrackDuration(TimeSpan? duration)
    {
        if (duration is null)
            return "";
        var d = duration.Value;
        return d.TotalHours >= 1
            ? $"{(int)d.TotalHours}:{d.Minutes:D2}:{d.Seconds:D2}"
            : $"{(int)d.TotalMinutes}:{d.Seconds:D2}";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes:D2}m {duration.Seconds:D2}s";
        if (duration.TotalMinutes >= 1)
            return $"{(int)duration.TotalMinutes}m {duration.Seconds:D2}s";
        return $"{duration.Seconds}s";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_073_741_824L)
            return $"{bytes / 1_073_741_824.0:F2} GB";
        if (bytes >= 1_048_576L)
            return $"{bytes / 1_048_576.0:F1} MB";
        if (bytes >= 1_024L)
            return $"{bytes / 1_024.0:F1} KB";
        return $"{bytes} B";
    }

    private void SetStatus(string message) => _statusLabel.Text = message;

    // -------------------------------------------------------------------------
    // Ctrl+C — copy selected rows as JSON to clipboard
    // -------------------------------------------------------------------------

    private sealed record ClipboardEntry(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("duration")] string? Duration,
        [property: JsonPropertyName("format")] string Format,
        [property: JsonPropertyName("result")] string Result,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("error")] string? Error
    );

    private static readonly JsonSerializerOptions JsonExportOptions = new()
    {
        WriteIndented = true,
    };

    private void OnListViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.C && _listView.SelectedItems.Count > 0)
        {
            e.Handled = true;
            CopySelectionAsJson();
        }
    }

    private void CopySelectionAsJson()
    {
        var entries = new List<ClipboardEntry>(_listView.SelectedItems.Count);

        foreach (ListViewItem item in _listView.SelectedItems)
        {
            var path = item.ToolTipText;
            var result = item.SubItems[ColResult].Text;
            var duration = item.SubItems[ColDuration].Text;
            entries.Add(
                new ClipboardEntry(
                    Path: path,
                    Name: Path.GetFileName(path),
                    Duration: string.IsNullOrEmpty(duration) ? null : duration,
                    Format: item.SubItems[ColFormat].Text,
                    Result: result,
                    Message: item.SubItems[ColMessage].Text,
                    Error: item.SubItems[ColError].Text is { Length: > 0 } e ? e : null
                )
            );
        }

        var json = JsonSerializer.Serialize(entries, JsonExportOptions);
        Clipboard.SetText(json);
    }

    private static readonly Color RowAltColor = Color.FromArgb(245, 245, 245);

    private void OnDrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item is null)
            return;

        // Row background: selection takes priority, then alternating color
        Color back = e.Item.Selected
            ? (_listView.Focused ? SystemColors.Highlight : SystemColors.ButtonFace)
            : (e.ItemIndex % 2 == 0 ? Color.White : RowAltColor);

        using (var brush = new SolidBrush(back))
            e.Graphics.FillRectangle(brush, e.Bounds);

        // Text color: respect per-subitem ForeColor (set for the Severity column)
        var subFore = e.SubItem?.ForeColor ?? Color.Empty;
        Color fore =
            e.Item.Selected && _listView.Focused
                ? SystemColors.HighlightText
                : (subFore.IsEmpty ? SystemColors.WindowText : subFore);

        // Text alignment from column definition
        var align = _listView.Columns[e.ColumnIndex].TextAlign;
        var flags =
            TextFormatFlags.VerticalCenter
            | TextFormatFlags.EndEllipsis
            | (
                align == HorizontalAlignment.Center ? TextFormatFlags.HorizontalCenter
                : align == HorizontalAlignment.Right ? TextFormatFlags.Right
                : TextFormatFlags.Left
            );

        var textBounds = new Rectangle(
            e.Bounds.X + 3, // 3 px left/right padding inside each cell
            e.Bounds.Y,
            e.Bounds.Width - 6,
            e.Bounds.Height
        );
        TextRenderer.DrawText(e.Graphics, e.SubItem?.Text, e.Item.Font, textBounds, fore, flags);

        // Vertical separator (right border only)
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
