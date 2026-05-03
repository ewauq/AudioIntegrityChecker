using System.Runtime.Versioning;

namespace AudioIntegrityChecker.UI.Exports;

[SupportedOSPlatform("windows")]
internal sealed class ExportOptionsForm : Form
{
    private readonly RadioButton _rbText;
    private readonly RadioButton _rbCsv;
    private readonly RadioButton _rbHtml;
    private readonly RadioButton _rbIssues;
    private readonly RadioButton _rbAll;
    private readonly bool _hasIssues;

    internal ExportFormat Format
    {
        get
        {
            if (_rbCsv.Checked)
                return ExportFormat.Csv;
            if (_rbHtml.Checked)
                return ExportFormat.Html;
            return ExportFormat.Text;
        }
    }

    internal ExportScope Scope =>
        _rbAll.Checked ? ExportScope.AllFiles : ExportScope.IssuesOnly;

    internal ExportOptionsForm(ExportFormat initialFormat, ExportScope initialScope, bool hasIssues)
    {
        _hasIssues = hasIssues;

        Text = "Export results";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont!;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(360, 260);

        var formatGroup = new GroupBox
        {
            Text = "Format",
            Location = new Point(16, 12),
            Size = new Size(328, 92),
        };
        _rbText = new RadioButton
        {
            Text = "Plain text (.txt)",
            Location = new Point(16, 22),
            AutoSize = true,
        };
        _rbCsv = new RadioButton
        {
            Text = "CSV (.csv)",
            Location = new Point(16, 44),
            AutoSize = true,
        };
        _rbHtml = new RadioButton
        {
            Text = "HTML (.html)",
            Location = new Point(16, 66),
            AutoSize = true,
        };
        formatGroup.Controls.AddRange([_rbText, _rbCsv, _rbHtml]);

        var scopeGroup = new GroupBox
        {
            Text = "Scope",
            Location = new Point(16, 112),
            Size = new Size(328, 70),
        };
        _rbIssues = new RadioButton
        {
            Text = "Issues only",
            Location = new Point(16, 22),
            AutoSize = true,
            Enabled = hasIssues,
        };
        _rbAll = new RadioButton
        {
            Text = "All files (issues and OK)",
            Location = new Point(16, 44),
            AutoSize = true,
        };
        scopeGroup.Controls.AddRange([_rbIssues, _rbAll]);

        var saveButton = new Button
        {
            Text = "Save…",
            Size = new Size(96, 28),
            DialogResult = DialogResult.OK,
            Location = new Point(ClientSize.Width - 96 - 16 - 96 - 8, ClientSize.Height - 28 - 16),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };
        var cancelButton = new Button
        {
            Text = "Cancel",
            Size = new Size(96, 28),
            DialogResult = DialogResult.Cancel,
            Location = new Point(ClientSize.Width - 96 - 16, ClientSize.Height - 28 - 16),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
        };

        Controls.AddRange([formatGroup, scopeGroup, saveButton, cancelButton]);

        AcceptButton = saveButton;
        CancelButton = cancelButton;

        SetInitialSelection(initialFormat, initialScope);
    }

    private void SetInitialSelection(ExportFormat format, ExportScope scope)
    {
        switch (format)
        {
            case ExportFormat.Csv:
                _rbCsv.Checked = true;
                break;
            case ExportFormat.Html:
                _rbHtml.Checked = true;
                break;
            default:
                _rbText.Checked = true;
                break;
        }

        if (!_hasIssues || scope == ExportScope.AllFiles)
            _rbAll.Checked = true;
        else
            _rbIssues.Checked = true;
    }
}
