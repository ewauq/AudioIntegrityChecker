using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AudioIntegrityChecker.Checkers.Flac;
using AudioIntegrityChecker.Checkers.Mp3;
using AudioIntegrityChecker.Core;
using AudioIntegrityChecker.Pipeline;

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
    private int _errorCount;
    private int _warningCount;
    private long _totalBytes;
    private TimeSpan _totalDuration;

    private bool _isAnalysing;
    private int _sortColumn = -1;
    private bool _sortAscending = true;

    private readonly ListView _listView;
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

    private const int ColFormat = 2;
    private const int ColStatus = 3;
    private const int ColDetails = 4;

    private const int ButtonRowHeight = 40;
    private const int GlobalBarHeight = 20;

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
            GridLines = true,
        };
        _listView.Columns.Add("Directory", 200);
        _listView.Columns.Add("File", 200);
        _listView.Columns.Add("Format", 55);
        _listView.Columns.Add("Status", 75);
        _listView.Columns.Add("Details", 450);
        _listView.ColumnClick += OnColumnClick;
        _listView.ItemActivate += OnItemActivate;

        var listPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6) };
        listPanel.Controls.Add(_listView);

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
            Text = "Drop audio files into the window to get started.",
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Width = 600,
            Height = 26,
        };

        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = ButtonRowHeight,
            Padding = new Padding(4, 4, 4, 0),
            FlowDirection = FlowDirection.LeftToRight,
        };
        buttonRow.Controls.Add(_startButton);
        buttonRow.Controls.Add(_cancelButton);
        buttonRow.Controls.Add(_statusLabel);

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

        _labelFiles = new ToolStripStatusLabel();
        _sepSize = new ToolStripSeparator { Visible = false };
        _labelSize = new ToolStripStatusLabel { Visible = false };
        _sepDuration = new ToolStripSeparator { Visible = false };
        _labelDuration = new ToolStripStatusLabel { Visible = false };
        int workerCount = Math.Min(Environment.ProcessorCount, 8);
        var sepRam = new ToolStripSeparator();
        _labelRam = new ToolStripStatusLabel("RAM: —");
        var sepWorkers = new ToolStripSeparator();
        _labelWorkers = new ToolStripStatusLabel($"Workers: {workerCount}");

        _statusStrip = new StatusStrip();
        _statusStrip.Items.AddRange([
            _labelFiles,
            _sepSize,
            _labelSize,
            _sepDuration,
            _labelDuration,
            sepRam,
            _labelRam,
            sepWorkers,
            _labelWorkers,
        ]);

        _ramTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _ramTimer.Tick += (_, _) =>
            _labelRam.Text = $"RAM: {FormatBytes(Process.GetCurrentProcess().WorkingSet64)}";
        _ramTimer.Start();

        Controls.Add(listPanel);
        Controls.Add(_bottomPanel);
        Controls.Add(_statusStrip);

        AllowDrop = true;
        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        _listView.AllowDrop = true;
        _listView.DragEnter += OnDragEnter;
        _listView.DragDrop += OnDragDrop;
        _startButton.Click += OnStartClick;
        _cancelButton.Click += OnCancelClick;

        RegisterCheckers();
        FormClosed += (_, _) => Mp3Mpg123Backend.Shutdown();
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

        if (!Mp3Mpg123Backend.IsLibraryAvailable())
            SetStatus("mpg123.dll not found — MP3 decode verification disabled");

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

        _ = ScanAsync(droppedPaths, _scanCts.Token);
    }

    private async Task ScanAsync(string[] droppedPaths, CancellationToken cancellationToken)
    {
        _globalBar.Style = ProgressBarStyle.Marquee;
        _globalBar.MarqueeAnimationSpeed = 30;
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
                    CollectFiles(
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

        var accepted = entries.Where(e => !e.Skipped).ToList();
        int skipped = entries.Count(e => e.Skipped);

        _listView.BeginUpdate();
        foreach (var entry in accepted)
        {
            var format = _registry.Resolve(entry.FilePath)?.FormatId ?? entry.Format;
            var item = new ListViewItem(entry.DirectoryName) { ToolTipText = entry.FilePath };
            item.SubItems.Add(Path.GetFileName(entry.FilePath));
            item.SubItems.Add(format);
            item.SubItems.Add(""); // Status — empty until analysis
            item.SubItems.Add(""); // Details

            _queuedFiles.Add(entry.FilePath);
            _itemByPath[entry.FilePath] = item;
            _totalBytes += entry.Bytes;
            if (entry.Duration.HasValue)
                _totalDuration += entry.Duration.Value;

            _listView.Items.Add(item);
        }
        _listView.EndUpdate();

        int fileCount = _queuedFiles.Count;
        string statusMessage = $"{fileCount} file(s) queued.";
        if (skipped > 0)
            statusMessage += $"  {skipped} unsupported ignored.";
        SetStatus(statusMessage);

        UpdateStatusBar();
        SetAnalysing(false);
    }

    // Runs on a thread-pool thread — no UI access permitted.
    private static List<FileEntry> CollectFiles(
        string[] paths,
        HashSet<string> supportedExtensions,
        CancellationToken cancellationToken,
        IProgress<int>? scanProgress = null
    )
    {
        var entries = new List<FileEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int counter = 0;

        void AddFile(string filePath)
        {
            if (!seen.Add(filePath))
                return;
            if (++counter % 50 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanProgress?.Report(entries.Count(e => !e.Skipped));
            }

            var extension = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
            if (!supportedExtensions.Contains(extension))
            {
                entries.Add(
                    new FileEntry(
                        filePath,
                        "",
                        extension.ToUpperInvariant(),
                        0,
                        null,
                        Skipped: true
                    )
                );
                return;
            }

            long bytes = 0;
            try
            {
                bytes = new FileInfo(filePath).Length;
            }
            catch { }

            var (totalSamples, sampleRate) = FlacMetadataReader.TryReadStreamInfo(filePath);
            TimeSpan? duration =
                (totalSamples > 0 && sampleRate > 0)
                    ? TimeSpan.FromSeconds((double)totalSamples / sampleRate)
                    : null;

            var directoryName = Path.GetFileName(Path.GetDirectoryName(filePath)) ?? string.Empty;
            entries.Add(
                new FileEntry(
                    filePath,
                    directoryName,
                    extension.ToUpperInvariant(),
                    bytes,
                    duration,
                    Skipped: false
                )
            );
        }

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(path))
            {
                foreach (
                    var filePath in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                )
                    AddFile(filePath);
            }
            else if (File.Exists(path))
            {
                AddFile(path);
            }
        }

        return entries;
    }

    private record FileEntry(
        string FilePath,
        string DirectoryName,
        string Format,
        long Bytes,
        TimeSpan? Duration,
        bool Skipped
    );

    private async void OnStartClick(object? sender, EventArgs e)
    {
        if (_queuedFiles.Count == 0)
            return;

        SetAnalysing(true);
        _errorCount = 0;
        _warningCount = 0;
        _totalFiles = _queuedFiles.Count;
        _completedFiles = 0;

        var defaultForeColor = _listView.ForeColor;
        foreach (ListViewItem item in _listView.Items)
        {
            item.ForeColor = defaultForeColor;
            item.SubItems[ColStatus].Text = "Waiting...";
            item.SubItems[ColDetails].Text = "";
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

            int okCount = _completedFiles - _errorCount - _warningCount;
            var parts = new List<string>();
            if (_errorCount > 0)
                parts.Add($"{_errorCount} error{(_errorCount == 1 ? "" : "s")}");
            if (_warningCount > 0)
                parts.Add($"{_warningCount} warning{(_warningCount == 1 ? "" : "s")}");
            if (okCount > 0)
                parts.Add($"{okCount} OK");
            string timeText =
                stopwatch.Elapsed.TotalSeconds < 60
                    ? $"{stopwatch.Elapsed.TotalSeconds:F1}s"
                    : $"{(int)stopwatch.Elapsed.TotalMinutes}m {stopwatch.Elapsed.Seconds:D2}s";
            string summary = parts.Count > 0 ? $" ({string.Join(", ", parts)})" : "";
            SetStatus(
                $"Processed {_completedFiles} file{(_completedFiles == 1 ? "" : "s")} in {timeText}{summary}"
            );

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
            TrimWorkingSet();
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
        _errorCount = 0;
        _warningCount = 0;

        UpdateStatusBar();
        SetStatus("Drop audio files into the window to get started.");
        SetAnalysing(false);
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
        Interlocked.Increment(ref _completedFiles);

        var result = args.Result;
        if (!result.IsValid && !result.IsSkipped)
            Interlocked.Increment(ref _errorCount);
        if (result.IsWarning)
            Interlocked.Increment(ref _warningCount);

        BeginInvoke(() =>
        {
            string status =
                result.IsSkipped ? "SKIPPED"
                : result.IsWarning ? "WARNING"
                : result.IsValid ? "OK"
                : "ERROR";

            if (!_itemByPath.TryGetValue(args.FilePath, out var item))
                return;

            item.SubItems[ColStatus].Text = status;
            item.SubItems[ColDetails].Text = BuildDetailsText(result);
            item.SubItems[ColDetails].Tag = result.ErrorMessage;

            if (status == "ERROR")
                item.ForeColor = Color.Red;
            if (status == "WARNING")
                item.ForeColor = Color.DarkOrange;

            int completed = _completedFiles;
            SetStatus($"Processing: {completed}/{_totalFiles}");
        });
    }

    private void OnFileProgress(FileProgressEventArgs args)
    {
        BeginInvoke(() =>
        {
            if (!_itemByPath.TryGetValue(args.FilePath, out var item))
                return;
            var current = item.SubItems[ColStatus].Text;
            if (current is "OK" or "ERROR" or "WARNING" or "SKIPPED")
                return;
            item.SubItems[ColStatus].Text = $"{(int)(args.Progress.Value * 100)}%";
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

        if (_errorCount == 0 && _warningCount == 0)
        {
            builder.Append("No errors found.");
        }
        else
        {
            if (_errorCount > 0)
                builder.AppendLine(
                    $"{_errorCount} error{(_errorCount == 1 ? "" : "s")} found: audio data corrupt."
                );
            if (_warningCount > 0)
                builder.Append(
                    $"{_warningCount} warning{(_warningCount == 1 ? "" : "s")}: audio likely intact, minor structural issues."
                );
        }

        var icon =
            _errorCount > 0 ? MessageBoxIcon.Error
            : _warningCount > 0 ? MessageBoxIcon.Warning
            : MessageBoxIcon.Information;

        MessageBox.Show(
            builder.ToString().TrimEnd(),
            "Analysis Complete",
            MessageBoxButtons.OK,
            icon
        );
    }

    private static string BuildDetailsText(CheckResult result)
    {
        if (result.IsSkipped)
            return result.ErrorMessage ?? string.Empty;
        if (result.IsValid && !result.IsWarning)
            return string.Empty;

        var builder = new System.Text.StringBuilder();
        if (result.ErrorTimecode.HasValue)
            builder.Append($"@ {result.ErrorTimecode.Value:hh\\:mm\\:ss\\.fff}  ");
        if (result.ErrorFrameIndex.HasValue)
            builder.Append($"[frame {result.ErrorFrameIndex.Value}]  ");
        if (!string.IsNullOrEmpty(result.ErrorMessage))
        {
            if (builder.Length > 0)
                builder.Append("— ");
            builder.Append(result.ErrorMessage);
        }
        return builder.ToString().TrimEnd();
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

    private sealed class ColumnSorter : IComparer
    {
        private readonly int _column;
        private readonly bool _ascending;

        public ColumnSorter(int column, bool ascending)
        {
            _column = column;
            _ascending = ascending;
        }

        public int Compare(object? x, object? y)
        {
            if (x is not ListViewItem itemA || y is not ListViewItem itemB)
                return 0;

            string textA =
                _column < itemA.SubItems.Count
                    ? (_column == 0 ? itemA.Text : itemA.SubItems[_column].Text)
                    : string.Empty;
            string textB =
                _column < itemB.SubItems.Count
                    ? (_column == 0 ? itemB.Text : itemB.SubItems[_column].Text)
                    : string.Empty;

            int comparison = string.Compare(textA, textB, StringComparison.OrdinalIgnoreCase);
            return _ascending ? comparison : -comparison;
        }
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
        // Passing -1/-1 instructs Windows to trim the process working set.
        // This is a cosmetic operation (pages become eligible for paging out)
        // and does not force any managed heap collection.
        SetProcessWorkingSetSize(
            Process.GetCurrentProcess().Handle,
            new IntPtr(-1),
            new IntPtr(-1)
        );
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
    // Double-buffered ListView (eliminates column-resize flicker)
    // -------------------------------------------------------------------------

    private sealed class BufferedListView : ListView
    {
        public BufferedListView() => DoubleBuffered = true;
    }
}
