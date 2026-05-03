using System.Diagnostics;
using System.Media;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using AudioIntegrityChecker.Core;
using AudioIntegrityChecker.UI.Exports;

namespace AudioIntegrityChecker.UI;

[SupportedOSPlatform("windows")]
internal sealed class AnalysisCompleteForm : Form
{
    private const int ColSeverity = 0;
    private const int ColFile = 1;
    private const int ColFormat = 2;
    private const int ColMessage = 3;

    private readonly UserPreferences _prefs;
    private readonly IReadOnlyList<CompletedFileSnapshot> _all;
    private readonly IReadOnlyList<CompletedFileSnapshot> _issuesOnly;
    private readonly ScanSummary _summary;

    private readonly BufferedListView _listView;
    private readonly ColumnHeader _msgColumn;
    private Image? _bannerImage;
    private int _sortColumn = ColSeverity;
    private bool _sortAscending = false;

    internal AnalysisCompleteForm(
        IReadOnlyList<CompletedFileSnapshot> snapshots,
        int totalFiles,
        TimeSpan elapsed,
        int countMetadata,
        int countIndex,
        int countStructure,
        int countCorruption,
        int countError,
        UserPreferences prefs
    )
    {
        _prefs = prefs;
        _all = snapshots;
        _issuesOnly = snapshots.Where(s => s.Result.Category != CheckCategory.Ok).ToList();
        _summary = new ScanSummary(
            TotalFiles: totalFiles,
            Elapsed: elapsed,
            Metadata: countMetadata,
            Index: countIndex,
            Structure: countStructure,
            Corruption: countCorruption,
            Error: countError,
            GeneratedAt: DateTime.Now
        );

        Text = "Analysis complete";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont!;
        FormBorderStyle = FormBorderStyle.Sizable;
        SizeGripStyle = SizeGripStyle.Auto;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(720, 520);

        _listView = new BufferedListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = true,
            HideSelection = false,
            GridLines = false,
            BorderStyle = BorderStyle.None,
            OwnerDraw = true,
            UseCompatibleStateImageBehavior = false,
        };
        _listView.Columns.Add(new ColumnHeader { Text = "Severity", Width = 80 });
        _listView.Columns.Add(new ColumnHeader { Text = "File", Width = 240 });
        _listView.Columns.Add(new ColumnHeader { Text = "Format", Width = 60 });
        _msgColumn = new ColumnHeader { Text = "Message", Width = 320 };
        _listView.Columns.Add(_msgColumn);
        _listView.ColumnClick += OnColumnClick;
        _listView.ItemActivate += OnItemActivate;
        _listView.KeyDown += OnListViewKeyDown;
        _listView.SizeChanged += (_, _) => ResizeMessageColumn();
        _listView.DrawColumnHeader += (_, e) => e.DrawDefault = true;
        _listView.DrawItem += (_, _) => { };
        _listView.DrawSubItem += (_, e) => ListViewSubItemPainter.Paint(_listView, e);

        var banner = BuildBannerPanel();
        var buttonPanel = BuildButtonPanel();

        Controls.Add(_listView);
        Controls.Add(banner);
        Controls.Add(buttonPanel);

        PopulateList();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        MinimumSize = new Size(
            560 + (Width - ClientSize.Width),
            380 + (Height - ClientSize.Height)
        );
        ResizeMessageColumn();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_summary.Corruption + _summary.Error > 0)
            SystemSounds.Hand.Play();
        else if (_summary.Structure + _summary.Index + _summary.Metadata > 0)
            SystemSounds.Exclamation.Play();
        else
            SystemSounds.Asterisk.Play();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _bannerImage?.Dispose();
        base.Dispose(disposing);
    }

    // ---------------------------------------------------------------------
    // Banner
    // ---------------------------------------------------------------------

    private Panel BuildBannerPanel()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 80,
            Padding = new Padding(20, 16, 20, 12),
            BackColor = SystemColors.Window,
        };
        panel.Paint += (s, e) =>
        {
            using var pen = new Pen(SystemColors.ControlLight);
            e.Graphics.DrawLine(pen, 0, panel.Height - 1, panel.Width, panel.Height - 1);
        };

        string iconName =
            _summary.Corruption + _summary.Error > 0 ? ToolStripIcons.Error
            : _summary.IssuesCount > 0 ? ToolStripIcons.Exclamation
            : ToolStripIcons.Tick;
        _bannerImage = ToolStripIcons.Load(iconName);

        var picture = new PictureBox
        {
            Image = _bannerImage,
            SizeMode = PictureBoxSizeMode.CenterImage,
            Size = new Size(40, 40),
            Location = new Point(20, 18),
        };

        var titleLabel = new Label
        {
            Text =
                $"Checked {_summary.TotalFiles} file{(_summary.TotalFiles == 1 ? "" : "s")} in {ScanResultExporter.FormatElapsed(_summary.Elapsed)}",
            Font = new Font(Font, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(72, 20),
        };

        string breakdownText = BuildBreakdownText();
        var breakdownLabel = new Label
        {
            Text = breakdownText,
            ForeColor = SystemColors.GrayText,
            AutoSize = true,
            Location = new Point(72, 44),
        };

        panel.Controls.AddRange([picture, titleLabel, breakdownLabel]);
        return panel;
    }

    private string BuildBreakdownText()
    {
        if (_summary.IssuesCount == 0)
            return "All files passed.";

        var parts = new List<string>(5);
        if (_summary.Corruption > 0)
            parts.Add($"{_summary.Corruption} corruption");
        if (_summary.Error > 0)
            parts.Add($"{_summary.Error} error{(_summary.Error == 1 ? "" : "s")}");
        if (_summary.Structure > 0)
            parts.Add($"{_summary.Structure} structure");
        if (_summary.Index > 0)
            parts.Add($"{_summary.Index} index");
        if (_summary.Metadata > 0)
            parts.Add($"{_summary.Metadata} metadata");
        string lead = $"{_summary.IssuesCount} issue{(_summary.IssuesCount == 1 ? "" : "s")} listed";
        return $"{lead}: {string.Join(" · ", parts)}";
    }

    // ---------------------------------------------------------------------
    // Bottom button bar
    // ---------------------------------------------------------------------

    private Panel BuildButtonPanel()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 56,
            Padding = new Padding(20, 12, 20, 12),
            BackColor = SystemColors.Control,
        };
        panel.Paint += (s, e) =>
        {
            using var pen = new Pen(SystemColors.ControlDark);
            e.Graphics.DrawLine(pen, 0, 0, panel.Width, 0);
        };

        var hint = new Label
        {
            Text = _issuesOnly.Count == 0
                ? ""
                : "Double-click a row to show the file in Explorer.",
            ForeColor = SystemColors.GrayText,
            AutoSize = true,
            Location = new Point(20, 20),
        };

        var closeBtn = new Button
        {
            Text = "Close",
            Size = new Size(96, 28),
            DialogResult = DialogResult.Cancel,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        closeBtn.Location = new Point(
            panel.ClientSize.Width - 96 - panel.Padding.Right,
            panel.Padding.Top
        );

        var exportBtn = new Button
        {
            Text = "Export…",
            Size = new Size(96, 28),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        exportBtn.Location = new Point(closeBtn.Location.X - 96 - 8, panel.Padding.Top);
        exportBtn.Click += OnExportClick;

        panel.Controls.AddRange([hint, exportBtn, closeBtn]);
        AcceptButton = closeBtn;
        CancelButton = closeBtn;
        return panel;
    }

    // ---------------------------------------------------------------------
    // List
    // ---------------------------------------------------------------------

    private void PopulateList()
    {
        _listView.BeginUpdate();
        _listView.Items.Clear();
        var ordered = SortIssues(_issuesOnly);
        foreach (var s in ordered)
            _listView.Items.Add(BuildItem(s));
        _listView.EndUpdate();
    }

    private ListViewItem BuildItem(CompletedFileSnapshot snapshot)
    {
        var severity = ResultFormatting.GetSeverity(snapshot.Result.Category);
        string sevText = severity == ResultSeverity.None ? "" : severity.ToString();

        var item = new ListViewItem(sevText) { Tag = snapshot };
        item.UseItemStyleForSubItems = false;
        item.SubItems[ColSeverity].ForeColor = ResultFormatting.GetSeverityColor(severity);

        item.SubItems.Add(System.IO.Path.GetFileName(snapshot.Path));
        item.SubItems.Add(snapshot.Format ?? "");
        item.SubItems.Add(
            snapshot.Result.Category == CheckCategory.Ok
                ? ""
                : ResultFormatting.BuildMessageColumnText(snapshot.Result)
        );
        item.ToolTipText = snapshot.Path;
        return item;
    }

    private List<CompletedFileSnapshot> SortIssues(IEnumerable<CompletedFileSnapshot> source)
    {
        IEnumerable<CompletedFileSnapshot> ordered = _sortColumn switch
        {
            ColFile => source.OrderBy(
                s => System.IO.Path.GetFileName(s.Path),
                StringComparer.OrdinalIgnoreCase
            ),
            ColFormat => source.OrderBy(s => s.Format ?? "", StringComparer.OrdinalIgnoreCase),
            ColMessage => source.OrderBy(
                s => ResultFormatting.BuildMessageColumnText(s.Result),
                StringComparer.OrdinalIgnoreCase
            ),
            _ => source
                .OrderByDescending(s => (int)ResultFormatting.GetSeverity(s.Result.Category))
                .ThenBy(s => s.Path, StringComparer.OrdinalIgnoreCase),
        };
        if (_sortAscending && _sortColumn == ColSeverity)
            ordered = source.OrderBy(s => (int)ResultFormatting.GetSeverity(s.Result.Category));
        else if (!_sortAscending && _sortColumn != ColSeverity)
            ordered = ordered.Reverse();
        return ordered.ToList();
    }

    private void ResizeMessageColumn()
    {
        int used = 0;
        for (int i = 0; i < _listView.Columns.Count; i++)
        {
            if (i == ColMessage)
                continue;
            used += _listView.Columns[i].Width;
        }
        int avail = _listView.ClientSize.Width - used - 4;
        if (avail > 120)
            _msgColumn.Width = avail;
    }

    private void OnColumnClick(object? sender, ColumnClickEventArgs e)
    {
        if (e.Column == _sortColumn)
            _sortAscending = !_sortAscending;
        else
        {
            _sortColumn = e.Column;
            _sortAscending = e.Column != ColSeverity;
        }
        PopulateList();
    }

    private void OnItemActivate(object? sender, EventArgs e)
    {
        if (_listView.SelectedItems.Count == 0)
            return;
        if (_listView.SelectedItems[0].Tag is not CompletedFileSnapshot snap)
            return;
        try
        {
            Process.Start(
                new ProcessStartInfo("explorer.exe", $"/select,\"{snap.Path}\"")
                {
                    UseShellExecute = true,
                }
            );
        }
        catch
        {
            // Reveal is best-effort; nothing to surface to the user beyond
            // the absence of a popping Explorer window.
        }
    }

    private void OnListViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.C)
        {
            CopySelectionAsJson();
            e.Handled = true;
        }
    }

    private void CopySelectionAsJson()
    {
        if (_listView.SelectedItems.Count == 0)
            return;
        var rows = new List<object>();
        foreach (ListViewItem item in _listView.SelectedItems)
        {
            if (item.Tag is not CompletedFileSnapshot s)
                continue;
            rows.Add(
                new
                {
                    path = s.Path,
                    format = s.Format,
                    severity = ResultFormatting.GetSeverity(s.Result.Category).ToString(),
                    result = s.Result.Category == CheckCategory.Ok ? "OK" : "ISSUE",
                    message = s.Result.Category == CheckCategory.Ok
                        ? ""
                        : ResultFormatting.BuildMessageColumnText(s.Result),
                    error = s.Result.ErrorMessage ?? "",
                }
            );
        }
        if (rows.Count == 0)
            return;
        string json = JsonSerializer.Serialize(
            rows,
            new JsonSerializerOptions { WriteIndented = true }
        );
        try
        {
            Clipboard.SetText(json);
        }
        catch
        {
            // Clipboard can throw if the host application is locked; not fatal.
        }
    }

    // ---------------------------------------------------------------------
    // Export
    // ---------------------------------------------------------------------

    private void OnExportClick(object? sender, EventArgs e)
    {
        var initialFormat = ParseFormat(_prefs.LastExportFormat);
        var initialScope = ParseScope(_prefs.LastExportScope);
        bool hasIssues = _issuesOnly.Count > 0;

        using var dlg = new ExportOptionsForm(initialFormat, initialScope, hasIssues);
        if (dlg.ShowDialog(this) != DialogResult.OK)
            return;

        var format = dlg.Format;
        var scope = dlg.Scope;

        using var save = new SaveFileDialog
        {
            Filter = ScanResultExporter.FilterFor(format),
            FileName = ScanResultExporter.SuggestFileName(format),
            OverwritePrompt = true,
            AddExtension = true,
        };
        if (save.ShowDialog(this) != DialogResult.OK)
            return;

        IReadOnlyList<CompletedFileSnapshot> rows =
            scope == ExportScope.AllFiles ? _all : _issuesOnly;

        try
        {
            using var fs = File.Create(save.FileName);
            ScanResultExporter.Write(format, rows, _summary, fs);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                $"Could not write the report:\n\n{ex.Message}",
                "Export failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error
            );
            return;
        }

        _prefs.LastExportFormat = format.ToString();
        _prefs.LastExportScope = scope == ExportScope.AllFiles ? "All" : "Issues";
        try
        {
            _prefs.Save();
        }
        catch
        {
            // Persistence of the export choice is best-effort.
        }
    }

    private static ExportFormat ParseFormat(string raw) =>
        raw?.Trim().ToLowerInvariant() switch
        {
            "csv" => ExportFormat.Csv,
            "html" => ExportFormat.Html,
            _ => ExportFormat.Text,
        };

    private static ExportScope ParseScope(string raw) =>
        raw?.Trim().ToLowerInvariant() == "all" ? ExportScope.AllFiles : ExportScope.IssuesOnly;
}
