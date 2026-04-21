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
    private readonly List<FileEntry> _queuedFiles = [];
    private readonly Dictionary<string, ListViewItem> _itemByPath = new(
        StringComparer.OrdinalIgnoreCase
    );

    private int _totalFiles;
    private int _completedFiles;
    private long _totalBytes;
    private readonly Dictionary<string, long> _fileSizes = new(StringComparer.OrdinalIgnoreCase);
    private long _processedBytes;
    private readonly Stopwatch _analysisStopwatch = new();

    private enum AnalysisState
    {
        Idle,
        Analysing,
        Pausing,
        Paused,
    }

    private AnalysisState _analysisState = AnalysisState.Idle;
    private PauseController? _pauseController;
    private int _startedFiles;

    private int _sortColumn = -1;
    private bool _sortAscending = true;

    private readonly BufferedListView _listView;
    private readonly TextProgressBar _globalBar;
    private readonly Panel _globalBarWrapper;
    private readonly ToolStripButton _startButton;
    private readonly ToolStripButton _cancelButton;
    private readonly ToolStripButton _clearButton;
    private readonly ToolStripButton _helpPanelButton;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly StatusStrip _statusStrip;
    private readonly ToolStripStatusLabel _labelFiles;
    private readonly ToolStripStatusLabel _labelSize;
    private readonly ToolStripStatusLabel _labelRam;
    private readonly ToolStripStatusLabel _labelWorkers;
    private readonly ToolStripStatusLabel _labelStorage;
    private readonly ToolStripSeparator _sepSize;
    private readonly ToolStripSeparator _sepStorage;
    private readonly ToolStripSeparator _sepStatus;
    private readonly System.Windows.Forms.Timer _ramTimer;
    private readonly SplitContainer _splitContainer;
    private readonly HtmlPanel _htmlPanel;
    private readonly MenuStrip _menuStrip;
    private readonly ToolStrip _toolStrip;
    private readonly ToolStripMenuItem _menuViewHelpPanel;
    private readonly ToolStripMenuItem _menuScanStart;
    private readonly ToolStripMenuItem _menuScanCancel;
    private readonly ToolStripMenuItem _menuClearList;
    private readonly Image _iconPlay;
    private readonly Image _iconPause;
    private readonly List<Image> _ownedIcons = [];

    private int _countOk;
    private int _countMetadata;
    private int _countIndex;
    private int _countStructure;
    private int _countCorruption;
    private int _countError;

    private const int ColDuration = 2;
    private const int ColFormat = 3;
    private const int ColResult = 4;
    private const int ColSeverity = 5;
    private const int ColMessage = 6;
    private const int ColError = 7;

    private readonly System.Windows.Forms.Timer _progressBarTimer;
    private Icon? _smallIcon;
    private Icon? _bigIcon;

    private const int GlobalBarHeight = 26;

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
        Text = $"Audio Integrity Checker v{v.Major}.{v.Minor}.{v.Build}";
        MinimumSize = new Size(900, 580);
        Size = new Size(1080, 640);
        StartPosition = FormStartPosition.CenterScreen;

        LoadEmbeddedIcon();

        _listView = new BufferedListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            OwnerDraw = true,
            BorderStyle = BorderStyle.None,
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

        var helpBackColor = Color.FromArgb(245, 245, 245);
        _htmlPanel = new HtmlPanel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = helpBackColor,
            Text = HelpContent.GetWelcomeHtml(),
        };

        _iconPlay = LoadOwnedIcon(ToolStripIcons.ControlPlay);
        _iconPause = LoadOwnedIcon(ToolStripIcons.ControlPause);

        _startButton = new ToolStripButton
        {
            Text = "Start",
            Image = _iconPlay,
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            TextImageRelation = TextImageRelation.ImageBeforeText,
            ImageScaling = ToolStripItemImageScaling.None,
            Enabled = false,
            ToolTipText = "Start the scan (F5)",
        };

        _cancelButton = new ToolStripButton
        {
            Text = "Cancel",
            Image = LoadOwnedIcon(ToolStripIcons.Cancel),
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            TextImageRelation = TextImageRelation.ImageBeforeText,
            ImageScaling = ToolStripItemImageScaling.None,
            Enabled = false,
            ToolTipText = "Cancel the running scan (Esc)",
        };

        _clearButton = new ToolStripButton
        {
            Text = "Clear",
            Image = LoadOwnedIcon(ToolStripIcons.BinEmpty),
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            TextImageRelation = TextImageRelation.ImageBeforeText,
            ImageScaling = ToolStripItemImageScaling.None,
            Enabled = false,
            ToolTipText = "Clear the list (Ctrl+L)",
        };

        _helpPanelButton = new ToolStripButton
        {
            Text = "Hide help panel",
            Image = LoadOwnedIcon(ToolStripIcons.Help),
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            TextImageRelation = TextImageRelation.ImageBeforeText,
            ImageScaling = ToolStripItemImageScaling.None,
            Alignment = ToolStripItemAlignment.Right,
            ToolTipText = "Toggle the help panel (F9)",
        };

        var optionsToolBtn = new ToolStripButton
        {
            Text = "Options",
            Image = LoadOwnedIcon(ToolStripIcons.Cog),
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            TextImageRelation = TextImageRelation.ImageBeforeText,
            ImageScaling = ToolStripItemImageScaling.None,
            Alignment = ToolStripItemAlignment.Right,
            ToolTipText = "Open options (Ctrl+,)",
        };

        var btnAddFolder = new ToolStripButton
        {
            Text = "Add folder",
            Image = LoadOwnedIcon(ToolStripIcons.FolderAdd),
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            TextImageRelation = TextImageRelation.ImageBeforeText,
            ImageScaling = ToolStripItemImageScaling.None,
            ToolTipText = "Add a folder to the queue (Ctrl+Shift+O)",
        };

        var btnAddFiles = new ToolStripButton
        {
            Text = "Add files",
            Image = LoadOwnedIcon(ToolStripIcons.PageWhiteAdd),
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            TextImageRelation = TextImageRelation.ImageBeforeText,
            ImageScaling = ToolStripItemImageScaling.None,
            ToolTipText = "Add files to the queue (Ctrl+O)",
        };

        _toolStrip = new ToolStrip
        {
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            ImageScalingSize = new Size(16, 16),
            RenderMode = ToolStripRenderMode.System,
            Padding = new Padding(4, 2, 4, 2),
        };
        _toolStrip.Items.AddRange([
            btnAddFolder,
            btnAddFiles,
            new ToolStripSeparator(),
            _startButton,
            _cancelButton,
            new ToolStripSeparator(),
            _clearButton,
            _helpPanelButton,
            optionsToolBtn,
        ]);
        foreach (ToolStripItem item in _toolStrip.Items)
        {
            if (item is ToolStripButton btn)
                btn.Padding = new Padding(2);
        }

        _splitContainer = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            FixedPanel = FixedPanel.Panel2,
            SplitterWidth = 1,
            BackColor = SystemColors.ControlDark,
        };
        _splitContainer.Panel1.Controls.Add(_listView);
        _splitContainer.Panel2.Controls.Add(_htmlPanel);

        _globalBarWrapper = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 0,
            Padding = new Padding(4, 4, 4, 4),
            Visible = false,
        };
        _globalBar = new TextProgressBar
        {
            Dock = DockStyle.Fill,
            Minimum = 0,
            Maximum = 100,
            Value = 0,
        };
        _globalBarWrapper.Controls.Add(_globalBar);

        // ---- Menu strip ----
        var menuFile = new ToolStripMenuItem("&File");
        var menuAddFiles = new ToolStripMenuItem("Add files…", null, OnMenuAddFiles)
        {
            ShortcutKeys = Keys.Control | Keys.O,
        };
        var menuAddFolder = new ToolStripMenuItem("Add folder…", null, OnMenuAddFolder)
        {
            ShortcutKeys = Keys.Control | Keys.Shift | Keys.O,
        };
        _menuClearList = new ToolStripMenuItem("Clear list", null, (_, _) => OnClear())
        {
            ShortcutKeys = Keys.Control | Keys.L,
            Enabled = false,
        };
        var menuOptions = new ToolStripMenuItem("Options…", null, OnMenuOptions)
        {
            ShortcutKeys = Keys.Control | Keys.Oemcomma,
            ShortcutKeyDisplayString = "Ctrl+,",
        };
        var menuExit = new ToolStripMenuItem("Exit", null, (_, _) => Close());
        menuFile.DropDownItems.AddRange([
            menuAddFiles,
            menuAddFolder,
            new ToolStripSeparator(),
            _menuClearList,
            new ToolStripSeparator(),
            menuOptions,
            new ToolStripSeparator(),
            menuExit,
        ]);

        var menuScan = new ToolStripMenuItem("&Scan");
        _menuScanStart = new ToolStripMenuItem("Start", null, OnStartClick)
        {
            ShortcutKeys = Keys.F5,
            Enabled = false,
        };
        _menuScanCancel = new ToolStripMenuItem("Cancel", null, OnCancelClick) { Enabled = false };
        menuScan.DropDownItems.AddRange([_menuScanStart, _menuScanCancel]);

        var menuView = new ToolStripMenuItem("&View");
        _menuViewHelpPanel = new ToolStripMenuItem("Hide help panel", null, OnMenuToggleHelpPanel)
        {
            ShortcutKeys = Keys.F9,
        };
        var menuAutoResize = new ToolStripMenuItem(
            "Auto-fit columns",
            null,
            OnMenuAutoResizeColumns
        );
        var menuResetColumns = new ToolStripMenuItem("Reset columns", null, OnMenuResetColumns);
        menuView.DropDownItems.AddRange([
            _menuViewHelpPanel,
            new ToolStripSeparator(),
            menuAutoResize,
            menuResetColumns,
        ]);

        var menuHelp = new ToolStripMenuItem("&Help");
        var menuKeyboardShortcuts = new ToolStripMenuItem(
            "Keyboard shortcuts",
            null,
            OnMenuKeyboardShortcuts
        )
        {
            ShortcutKeys = Keys.Shift | Keys.F1,
        };
        var menuAbout = new ToolStripMenuItem("About", null, OnMenuAbout);
        var menuGitHub = new ToolStripMenuItem("View on GitHub…", null, OnMenuViewGitHub);
        menuHelp.DropDownItems.AddRange([
            menuKeyboardShortcuts,
            new ToolStripSeparator(),
            menuAbout,
            menuGitHub,
        ]);

        _menuStrip = new MenuStrip();
        _menuStrip.Items.AddRange([menuFile, menuScan, menuView, menuHelp]);

        // ---- Column header context menu ----
        BuildColumnHeaderContextMenu();

        _labelFiles = new ToolStripStatusLabel();
        _sepSize = new ToolStripSeparator { Visible = false };
        _labelSize = new ToolStripStatusLabel { Visible = false };
        _sepStorage = new ToolStripSeparator { Visible = false };
        _labelStorage = new ToolStripStatusLabel { Visible = false };
        _sepStatus = new ToolStripSeparator { Visible = false };
        int workerCount = Math.Min(Environment.ProcessorCount, 8);
        _labelRam = new ToolStripStatusLabel(
            $"RAM: {FormatBytes(Process.GetCurrentProcess().WorkingSet64)}"
        );
        var sepRam = new ToolStripSeparator();
        _labelWorkers = new ToolStripStatusLabel($"Workers: {workerCount}");
        _statusLabel = new ToolStripStatusLabel
        {
            Spring = true,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _statusStrip = new StatusStrip();
        _statusStrip.Items.AddRange([
            _labelFiles,
            _sepSize,
            _labelSize,
            _sepStorage,
            _labelStorage,
            _sepStatus,
            _statusLabel,
            _labelWorkers,
            sepRam,
            _labelRam,
        ]);

        _progressBarTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _progressBarTimer.Tick += OnProgressBarTick;

        _ramTimer = new System.Windows.Forms.Timer { Interval = RamUpdateIntervalMs };
        _ramTimer.Tick += (_, _) =>
            _labelRam.Text = $"RAM: {FormatBytes(Process.GetCurrentProcess().WorkingSet64)}";
        _ramTimer.Start();

        var topSeparator = new Panel
        {
            Dock = DockStyle.Top,
            Height = 1,
            BackColor = SystemColors.ControlDark,
        };

        Controls.Add(_splitContainer);
        Controls.Add(_globalBarWrapper);
        Controls.Add(_statusStrip);
        Controls.Add(topSeparator);
        Controls.Add(_toolStrip);
        Controls.Add(_menuStrip);
        MainMenuStrip = _menuStrip;

        AllowDrop = true;
        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        _listView.AllowDrop = true;
        _listView.DragEnter += OnDragEnter;
        _listView.DragDrop += OnDragDrop;
        _startButton.Click += OnStartClick;
        _cancelButton.Click += OnCancelClick;
        _clearButton.Click += (_, _) => OnClear();
        _helpPanelButton.Click += OnMenuToggleHelpPanel;
        optionsToolBtn.Click += OnMenuOptions;
        btnAddFolder.Click += OnMenuAddFolder;
        btnAddFiles.Click += OnMenuAddFiles;
        _listView.SelectedIndexChanged += OnSelectedIndexChanged;

        RegisterCheckers();

        // Cancel any in-flight work before the form is destroyed so that mpg123
        // worker calls finish before Shutdown() tears down the native library.
        FormClosing += OnFormClosing;
        FormClosed += (_, _) => Mp3Mpg123Backend.Shutdown();

        Load += OnFormLoad;
    }

    private void OnFormLoad(object? sender, EventArgs e)
    {
        var prefs = UserPreferences.Load();

        _labelWorkers.Text = $"Workers: {GetEffectiveWorkerCount(prefs, StorageKind.Unknown)}";

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
        }
        UpdateHelpPanelLabel(prefs.HelpPanelVisible);

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
        _registry.Add("flac", new NativeFlacChecker());
        _registry.Add("mp3", new Mp3Checker());
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
        _fileSizes.Clear();
        _processedBytes = 0;

        SetAnalysisState(AnalysisState.Idle);
        SetStatus("Scanning…");

        _ = ScanAsync(paths, _scanCts.Token);
    }

    private async Task ScanAsync(string[] droppedPaths, CancellationToken cancellationToken)
    {
        _globalBar.Style = ProgressBarStyle.Marquee;
        ShowGlobalBar(true);

        List<FileEntry> entries;
        try
        {
            var scanProgress = new Progress<int>(count =>
                SetStatus($"Scanning… {count} file{(count == 1 ? "" : "s")} found")
            );

            entries = await Task.Run(
                () =>
                    FileCollector.Collect(
                        droppedPaths,
                        _registry.CheckersByExtension,
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
            var item = new ListViewItem(entry.DirectoryName)
            {
                ToolTipText = entry.FilePath,
                UseItemStyleForSubItems = false,
            };
            item.SubItems.Add(Path.GetFileName(entry.FilePath));
            // Duration is populated later by the checker (see OnFileCompleted). Scanning
            // no longer opens each file a second time to peek at metadata.
            item.SubItems.Add("");
            item.SubItems.Add(entry.Checker.FormatId);
            item.SubItems.Add(""); // Result
            item.SubItems.Add(""); // Severity
            item.SubItems.Add(""); // Message
            item.SubItems.Add(""); // Error

            _queuedFiles.Add(entry);
            _itemByPath[entry.FilePath] = item;
            _totalBytes += entry.Bytes;

            _listView.Items.Add(item);
        }
        _listView.EndUpdate();

        foreach (var entry in entries)
            _fileSizes[entry.FilePath] = entry.Bytes;

        int fileCount = _queuedFiles.Count;
        SetStatus($"{fileCount} file{(fileCount == 1 ? "" : "s")} queued.");

        UpdateStorageIndicator(entries);
        UpdateStatusBar();
        UpdateHelpPanel();
        SetAnalysisState(AnalysisState.Idle);
    }

    private void UpdateStorageIndicator(IReadOnlyList<FileEntry> entries)
    {
        if (entries.Count == 0)
        {
            _sepStorage.Visible = false;
            _labelStorage.Visible = false;
            _sepStatus.Visible = false;
            return;
        }

        var info = StorageDetector.GetInfoForDisk(entries[0].PhysicalDiskNumber);
        _labelStorage.Text = FormatStorageDisplay(info);
        _sepStorage.Visible = true;
        _labelStorage.Visible = true;
        _sepStatus.Visible = true;

        // Automatic mode depends on the detected storage type, so refresh the
        // Workers label now that we know what disk the queue sits on.
        int workerCount = GetEffectiveWorkerCount(UserPreferences.Load(), info.Kind);
        _labelWorkers.Text = $"Workers: {workerCount}";
    }

    private static string FormatStorageDisplay(StorageInfo info)
    {
        var parts = new List<string>(4);
        if (!string.IsNullOrEmpty(info.FriendlyName))
            parts.Add(info.FriendlyName);
        if (info.Kind != StorageKind.Unknown)
            parts.Add(FormatMediaKind(info.Kind));
        if (!string.IsNullOrEmpty(info.BusDisplay) && info.BusDisplay != "Unknown")
            parts.Add($"({info.BusDisplay})");
        if (info.SizeBytes > 0)
            parts.Add(FormatStorageSize(info.SizeBytes));

        return parts.Count > 0 ? string.Join(" ", parts) : "Storage unknown";
    }

    private static string FormatMediaKind(StorageKind kind) =>
        kind switch
        {
            StorageKind.Hdd => "HDD",
            StorageKind.SataSsd => "SSD",
            StorageKind.Nvme => "SSD",
            _ => "",
        };

    private static string FormatStorageSize(long bytes)
    {
        const double TB = 1_000_000_000_000.0;
        const double GB = 1_000_000_000.0;
        if (bytes >= TB)
            return $"{bytes / TB:0.#} TB";
        if (bytes >= GB)
            return $"{bytes / GB:0.#} GB";
        return $"{bytes / 1_000_000.0:0.#} MB";
    }

    private async void OnStartClick(object? sender, EventArgs e)
    {
        switch (_analysisState)
        {
            case AnalysisState.Idle:
                await StartAnalysisAsync();
                break;
            case AnalysisState.Analysing:
                PauseAnalysis();
                break;
            case AnalysisState.Paused:
                ResumeAnalysis();
                break;
        }
    }

    private async Task StartAnalysisAsync()
    {
        if (_queuedFiles.Count == 0)
            return;

        SetAnalysisState(AnalysisState.Analysing);
        _countOk = 0;
        _countMetadata = 0;
        _countIndex = 0;
        _countStructure = 0;
        _countCorruption = 0;
        _countError = 0;
        _totalFiles = _queuedFiles.Count;
        _completedFiles = 0;
        _startedFiles = 0;
        _processedBytes = 0;
        UpdateStatusBar();

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

        int workerCount = GetEffectiveWorkerCount(UserPreferences.Load(), CurrentStorageKind());
        _labelWorkers.Text = $"Workers: {workerCount}";

        _pipeline = new AnalysisPipeline(workerCount);
        _pipeline.FileStarted += OnFileStarted;
        _pipeline.FileCompleted += OnFileCompleted;
        _pipeline.FileProgressChanged += OnFileProgress;

        _analysisCts = new CancellationTokenSource();
        _pauseController = new PauseController();

        var globalProgress = new Progress<int>(completedCount =>
            BeginInvoke(() => _globalBar.Value = Math.Min(completedCount, _totalFiles))
        );

        _analysisStopwatch.Restart();
        try
        {
            await _pipeline.RunAsync(
                _queuedFiles,
                _analysisCts.Token,
                _pauseController,
                globalProgress
            );
            _analysisStopwatch.Stop();

            string timeText = FormatElapsed(_analysisStopwatch.Elapsed);
            int n = _completedFiles;
            SetStatus($"Processed {(n == 1 ? "1 file" : $"{n} files")} in {timeText}.");

            ShowCompletionDialog(_analysisStopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            _analysisStopwatch.Stop();
            SetStatus("Cancelled.");
        }
        catch (Exception ex)
        {
            _analysisStopwatch.Stop();
            SetStatus($"Pipeline error: {ex.Message}");
        }
        finally
        {
            _pauseController.Reset();
            _pauseController = null;
            _analysisCts.Dispose();
            _analysisCts = null;
            SetAnalysisState(AnalysisState.Idle);
            ShowGlobalBar(false);
            _ = Task.Run(TrimWorkingSet);
        }
    }

    private void PauseAnalysis()
    {
        _pauseController?.Pause();
        _analysisStopwatch.Stop();
        SetAnalysisState(AnalysisState.Pausing);
        RefreshPausingState();
    }

    private void OnFileStarted(string filePath)
    {
        Interlocked.Increment(ref _startedFiles);
        if (_analysisState == AnalysisState.Pausing)
            BeginInvoke(RefreshPausingState);
    }

    private void RefreshPausingState()
    {
        if (_analysisState != AnalysisState.Pausing)
            return;
        int inFlight = _startedFiles - _completedFiles;
        if (inFlight <= 0)
        {
            SetAnalysisState(AnalysisState.Paused);
            SetStatus("Paused.");
        }
        else
        {
            string waiting = $"Waiting ({inFlight})...";
            _startButton.Text = waiting;
            _menuScanStart.Text = waiting;
        }
    }

    private void ResumeAnalysis()
    {
        _analysisStopwatch.Start();
        SetAnalysisState(AnalysisState.Analysing);
        SetStatus("");
        _pauseController?.Resume();
    }

    private void OnCancelClick(object? sender, EventArgs e)
    {
        if (_analysisState == AnalysisState.Idle)
            return;
        _cancelButton.Enabled = false;
        _menuScanCancel.Enabled = false;
        SetStatus("Cancelling…");
        // Unblock the pause gate so the cancellation token propagates
        // through the pipeline loop instead of hanging.
        _pauseController?.Reset();
        _analysisCts?.Cancel();
    }

    private void OnClear()
    {
        _pauseController?.Reset();
        _pauseController = null;
        _scanCts?.Cancel();

        _queuedFiles.Clear();
        _itemByPath.Clear();
        _listView.Items.Clear();
        _totalBytes = 0;
        _fileSizes.Clear();
        _processedBytes = 0;
        _totalFiles = 0;
        _completedFiles = 0;
        _countOk = 0;
        _countMetadata = 0;
        _countIndex = 0;
        _countStructure = 0;
        _countCorruption = 0;
        _countError = 0;

        _sepStorage.Visible = false;
        _labelStorage.Visible = false;
        _sepStatus.Visible = false;

        UpdateStatusBar();
        UpdateHelpPanel();
        SetStatus("");
        SetAnalysisState(AnalysisState.Idle);
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

        UpdateHelpPanelLabel(opening);
    }

    private void UpdateHelpPanelLabel(bool visible)
    {
        string label = visible ? "Hide help panel" : "Show help panel";
        _helpPanelButton.Text = label;
        _menuViewHelpPanel.Text = label;
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

    private void OnMenuOptions(object? sender, EventArgs e)
    {
        var prefs = UserPreferences.Load();
        using var dialog = new OptionsForm(prefs);
        dialog.SettingsApplied += OnOptionsApplied;
        dialog.ShowDialog(this);
    }

    private void OnOptionsApplied()
    {
        // The label reflects what the next scan will use. A running scan
        // keeps its current worker count until it finishes.
        int workerCount = GetEffectiveWorkerCount(UserPreferences.Load(), CurrentStorageKind());
        _labelWorkers.Text = $"Workers: {workerCount}";
    }

    private void OnMenuResetColumns(object? sender, EventArgs e)
    {
        int[] defaults =
        [
            ColDirWidth,
            ColFileWidth,
            ColDurationWidth,
            ColFormatWidth,
            ColResultWidth,
            ColSeverityWidth,
            ColMessageWidth,
            ColErrorWidth,
        ];
        _hiddenColumnWidths.Clear();

        if (_listView.HeaderContextMenuStrip is ContextMenuStrip menu)
        {
            foreach (ToolStripMenuItem item in menu.Items)
                item.Checked = true;
        }

        for (int i = 0; i < _listView.Columns.Count && i < defaults.Length; i++)
            _listView.Columns[i].Width = defaults[i];
    }

    private void OnMenuKeyboardShortcuts(object? sender, EventArgs e)
    {
        const string text =
            "File\n"
            + "    Ctrl+O              Add files\n"
            + "    Ctrl+Shift+O   Add folder\n"
            + "    Ctrl+L              Clear list\n"
            + "    Ctrl+,              Options\n"
            + "\n"
            + "Scan\n"
            + "    F5                    Start / Pause / Resume\n"
            + "    Esc                  Cancel\n"
            + "\n"
            + "View\n"
            + "    F9                    Toggle help panel\n"
            + "\n"
            + "Help\n"
            + "    Shift+F1          Keyboard shortcuts\n"
            + "\n"
            + "List\n"
            + "    Ctrl+C             Copy selected rows as JSON\n"
            + "    Enter / dbl   Reveal in Explorer";
        MessageBox.Show(
            this,
            text,
            "Keyboard shortcuts",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information
        );
    }

    private void OnMenuAbout(object? sender, EventArgs e)
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version!;
        string text =
            $"Audio Integrity Checker v{v.Major}.{v.Minor}.{v.Build}\n"
            + "\n"
            + "A read-only scanner that detects corruption, structural anomalies "
            + "and metadata inconsistencies in audio files (FLAC, MP3).\n"
            + "\n"
            + "https://github.com/ewauq/AudioIntegrityChecker";
        MessageBox.Show(this, text, "About", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OnMenuViewGitHub(object? sender, EventArgs e)
    {
        Process.Start(
            new ProcessStartInfo("https://github.com/ewauq/AudioIntegrityChecker")
            {
                UseShellExecute = true,
            }
        );
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && _analysisState != AnalysisState.Idle)
        {
            OnCancelClick(this, EventArgs.Empty);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>
    /// Best-effort storage kind for the files currently queued. Returns
    /// <see cref="StorageKind.Unknown"/> when the queue is empty, which maps to
    /// the conservative default (min(ProcessorCount, 8)).
    /// </summary>
    private StorageKind CurrentStorageKind()
    {
        if (_queuedFiles.Count == 0)
            return StorageKind.Unknown;
        return StorageDetector.GetKindForDisk(_queuedFiles[0].PhysicalDiskNumber);
    }

    // Per-disk matrix from the plan (Section 4). In Automatic mode the worker
    // count tracks the storage type; mechanical disks use every core for CPU
    // decoding (I/O is serialised anyway) while SATA SSDs are capped to avoid
    // saturating the SATA command queue. Manual mode overrides everything.
    private static int GetEffectiveWorkerCount(UserPreferences prefs, StorageKind kind)
    {
        if (!prefs.WorkerCountAuto)
            return Math.Clamp(prefs.WorkerCount, 1, Environment.ProcessorCount);

        int processorCount = Environment.ProcessorCount;
        return kind switch
        {
            StorageKind.Hdd => processorCount,
            StorageKind.Nvme => processorCount,
            StorageKind.SataSsd => Math.Min(processorCount, 8),
            _ => Math.Min(processorCount, 8),
        };
    }

    // -------------------------------------------------------------------------
    // Column header context menu: show/hide optional columns
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

    private void OnSelectedIndexChanged(object? sender, EventArgs e) => UpdateHelpPanel();

    private void UpdateHelpPanel()
    {
        if (_splitContainer.Panel2Collapsed)
            return;

        if (_listView.Items.Count == 0)
        {
            _htmlPanel.Text = HelpContent.GetWelcomeHtml();
            return;
        }

        if (_listView.SelectedItems.Count == 0)
        {
            _htmlPanel.Text = HelpContent.GetSelectFileHtml();
            return;
        }

        var item = _listView.SelectedItems[0];
        var resultText = item.SubItems[ColResult].Text;

        // Not yet analyzed
        if (resultText is "" or "Pending..." || resultText.EndsWith('%'))
        {
            _htmlPanel.Text = HelpContent.GetPendingHtml();
            return;
        }

        var errorText = item.SubItems[ColError].Text;

        // Extract positional info from the message column (e.g. "@ 00:12:34.567  [frame 123]")
        string? positionalInfo = null;
        var messageText = item.SubItems[ColMessage].Text;
        int atIndex = messageText.IndexOf("  @", StringComparison.Ordinal);
        if (atIndex >= 0)
            positionalInfo = messageText[(atIndex + 2)..].Trim();

        _htmlPanel.Text = string.IsNullOrEmpty(errorText)
            ? HelpContent.GetHtml(null, null)
            : HelpContent.GetHtml(errorText, positionalInfo);
    }

    private void SetAnalysisState(AnalysisState state)
    {
        _analysisState = state;
        bool active = state != AnalysisState.Idle;
        bool hasItems = _listView.Items.Count > 0;

        _cancelButton.Enabled = active;
        _menuScanCancel.Enabled = active;
        _clearButton.Enabled = !active && hasItems;
        _menuClearList.Enabled = !active && hasItems;

        string startText = state switch
        {
            AnalysisState.Analysing => "Pause",
            AnalysisState.Pausing => $"Waiting ({_startedFiles - _completedFiles})...",
            AnalysisState.Paused => "Resume",
            _ => "Start",
        };
        _startButton.Text = startText;
        _menuScanStart.Text = startText;

        _startButton.ToolTipText = state switch
        {
            AnalysisState.Analysing => "Pause the scan (F5)",
            AnalysisState.Pausing => "Waiting for in-flight files to finish…",
            AnalysisState.Paused => "Resume the scan (F5)",
            _ => "Start the scan (F5)",
        };

        _startButton.Image = state == AnalysisState.Analysing ? _iconPause : _iconPlay;

        bool startEnabled =
            state == AnalysisState.Analysing
            || state == AnalysisState.Paused
            || (state == AnalysisState.Idle && _queuedFiles.Count > 0);
        _startButton.Enabled = startEnabled;
        _menuScanStart.Enabled = startEnabled;

        _globalBar.Paused = state == AnalysisState.Paused;
    }

    private void OnFileCompleted(FileCompletedEventArgs args)
    {
        // Increment all counters on the worker thread so they are visible to the await
        // continuation via the task memory barrier, no BeginInvoke delay needed.
        int completed = Interlocked.Increment(ref _completedFiles);
        if (_fileSizes.TryGetValue(args.FilePath, out long fileBytes))
            Interlocked.Add(ref _processedBytes, fileBytes);
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

            // Duration comes from the checker's in-memory buffer, populated per row.
            if (args.Duration.HasValue)
                item.SubItems[ColDuration].Text = FormatTrackDuration(args.Duration);

            item.SubItems[ColResult].Text = isOk ? "OK" : "ISSUE";
            item.SubItems[ColSeverity].Text =
                severity == ResultSeverity.None ? "" : severity.ToString();
            item.SubItems[ColSeverity].ForeColor = color;
            item.SubItems[ColMessage].Text = ResultFormatting.BuildMessageColumnText(result);
            item.SubItems[ColError].Text = result.ErrorMessage ?? string.Empty;

            UpdateStatusBar();
            RefreshPausingState();
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
        string timeText = FormatElapsed(elapsed);

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
                    $"{_countCorruption} CORRUPTION: audio data demonstrably damaged."
                );
            if (_countError > 0)
                builder.AppendLine(
                    $"{_countError} ERROR: analysis tool failed, file state unknown."
                );
            if (_countStructure > 0)
                builder.AppendLine(
                    $"{_countStructure} STRUCTURE: stream anomaly, audio likely intact."
                );
            if (_countIndex > 0)
                builder.AppendLine($"{_countIndex} INDEX: seek/frame count mismatch.");
            if (_countMetadata > 0)
                builder.Append(
                    $"{_countMetadata} METADATA: tag inconsistency, no playback impact."
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
        _globalBarWrapper.Height = visible ? GlobalBarHeight + 8 : 0;
        if (visible)
            _progressBarTimer.Start();
        else
        {
            _progressBarTimer.Stop();
            _globalBar.Text = "";
        }
    }

    private void UpdateStatusBar()
    {
        int fileCount = _queuedFiles.Count;
        _labelFiles.Text = $"{fileCount} file{(fileCount == 1 ? "" : "s")}";

        bool hasSize = _totalBytes > 0;
        _sepSize.Visible = hasSize;
        _labelSize.Visible = hasSize;
        if (hasSize)
            _labelSize.Text = FormatBytes(_totalBytes);
    }

    // -------------------------------------------------------------------------
    // Memory: trim OS working set after analysis
    // -------------------------------------------------------------------------

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(
        IntPtr processHandle,
        IntPtr minimumSize,
        IntPtr maximumSize
    );

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    private const int WM_SETICON = 0x0080;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;

    private void LoadEmbeddedIcon()
    {
        var asm = typeof(MainForm).Assembly;
        using var stream = asm.GetManifestResourceStream("AudioIntegrityChecker.icon.ico");
        if (stream is null)
        {
            Icon = SystemIcons.Application;
            return;
        }

        // Load full multi-size icon for alt-tab / jumplist.
        Icon = new Icon(stream);

        // Load explicit small (titlebar) and big (alt-tab large) sizes so Windows
        // uses the pixel-perfect variants instead of rescaling a single HICON.
        var smallSize = SystemInformation.SmallIconSize;
        var bigSize = SystemInformation.IconSize;
        stream.Position = 0;
        _smallIcon = new Icon(stream, smallSize);
        stream.Position = 0;
        _bigIcon = new Icon(stream, bigSize);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_smallIcon is not null)
            SendMessage(Handle, WM_SETICON, ICON_SMALL, _smallIcon.Handle);
        if (_bigIcon is not null)
            SendMessage(Handle, WM_SETICON, ICON_BIG, _bigIcon.Handle);
    }

    private Image LoadOwnedIcon(string resourceName)
    {
        var icon = ToolStripIcons.Load(resourceName);
        _ownedIcons.Add(icon);
        return icon;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _smallIcon?.Dispose();
            _bigIcon?.Dispose();
            foreach (var icon in _ownedIcons)
                icon.Dispose();
            _ownedIcons.Clear();
        }
        base.Dispose(disposing);
    }

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

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds < 1)
            return $"{(int)elapsed.TotalMilliseconds}ms";
        if (elapsed.TotalSeconds < 60)
            return $"{elapsed.TotalSeconds:F1}s";
        return $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds:D2}s";
    }

    private void OnProgressBarTick(object? sender, EventArgs e)
    {
        if (_globalBar.Style == ProgressBarStyle.Marquee)
        {
            _globalBar.MarqueeOffset += 40;
            return;
        }
        _globalBar.Text = BuildProgressBarText();
    }

    private string BuildProgressBarText()
    {
        int pct = _totalFiles > 0 ? (int)((double)_completedFiles / _totalFiles * 100) : 0;
        string elapsed = _analysisStopwatch.Elapsed.ToString(@"hh\:mm\:ss");
        if (_analysisState is AnalysisState.Pausing or AnalysisState.Paused)
            return $"Paused - {pct}% - {_completedFiles}/{_totalFiles} files - Elapsed time: {elapsed}";
        return $"Progression: {pct}% - {_completedFiles}/{_totalFiles} files"
            + $" - Elapsed time: {elapsed} - Remaining time: {BuildEtaString(_analysisStopwatch.Elapsed)}";
    }

    private string BuildEtaString(TimeSpan elapsed)
    {
        if (elapsed.TotalSeconds < 3.0 || _completedFiles < 5)
            return "--:--:--";

        // Priority 1: bytes rate — more accurate than file count for variable sizes.
        long processed = Interlocked.Read(ref _processedBytes);
        if (processed > 0 && _totalBytes > processed)
        {
            double rate = processed / elapsed.TotalSeconds;
            return TimeSpan.FromSeconds((_totalBytes - processed) / rate).ToString(@"hh\:mm\:ss");
        }

        // Priority 2: file count (fallback).
        if (_completedFiles > 0)
        {
            double rate = _completedFiles / elapsed.TotalSeconds;
            double remaining = (_totalFiles - _completedFiles) / rate;
            if (remaining > 0)
                return TimeSpan.FromSeconds(remaining).ToString(@"hh\:mm\:ss");
        }

        return "--:--:--";
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
    // Ctrl+C: copy selected rows as JSON to clipboard
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
