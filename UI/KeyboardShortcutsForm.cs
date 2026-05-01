using System.Runtime.Versioning;
using System.Text;

namespace AudioIntegrityChecker.UI;

[SupportedOSPlatform("windows")]
internal sealed class KeyboardShortcutsForm : Form
{
    private static readonly Dictionary<string, (Keys Keys, string Description)[]> ExtraByGroup =
        new()
        {
            ["Scan"] = [(Keys.Escape, "Cancel (during scan)")],
            ["View"] = [(Keys.F1, "Help panel (alias of F9)")],
        };

    private static readonly (Keys Keys, string Description)[] ResultsListShortcuts =
    [
        (Keys.Control | Keys.C, "Copy selected rows as JSON"),
        (Keys.Enter, "Reveal in Explorer (or double-click)"),
    ];

    private readonly ListView _listView;
    private readonly ColumnHeader _shortcutColumn;
    private readonly ColumnHeader _descriptionColumn;

    internal KeyboardShortcutsForm(MenuStrip menu)
    {
        Text = "Keyboard shortcuts";
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont!;
        FormBorderStyle = FormBorderStyle.Sizable;
        SizeGripStyle = SizeGripStyle.Auto;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(460, 480);

        var groups = BuildGroups(menu);

        _shortcutColumn = new ColumnHeader { Text = "Shortcut", Width = 140 };
        _descriptionColumn = new ColumnHeader { Text = "Description", Width = 280 };

        _listView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = true,
            GridLines = false,
            BorderStyle = BorderStyle.None,
            ShowGroups = true,
            UseCompatibleStateImageBehavior = false,
        };
        _listView.Columns.AddRange([_shortcutColumn, _descriptionColumn]);
        PopulateItems(groups);
        _listView.SizeChanged += (_, _) => ResizeDescriptionColumn();
        _listView.ItemSelectionChanged += (_, e) =>
        {
            if (e.IsSelected && e.Item is { } item)
                item.Selected = false;
        };

        var listHost = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12, 12, 12, 12),
            BackColor = SystemColors.Window,
        };
        listHost.Controls.Add(_listView);

        var buttonPanel = BuildButtonPanel();

        Controls.Add(listHost);
        Controls.Add(buttonPanel);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        MinimumSize = new Size(
            380 + (Width - ClientSize.Width),
            360 + (Height - ClientSize.Height)
        );
        ResizeDescriptionColumn();
    }

    private void ResizeDescriptionColumn()
    {
        int avail = _listView.ClientSize.Width - _shortcutColumn.Width - 4;
        if (avail > 80)
            _descriptionColumn.Width = avail;
    }

    private void PopulateItems(List<(string Group, List<(Keys K, string Desc)> Items)> groups)
    {
        foreach (var (groupName, items) in groups)
        {
            var lvGroup = new ListViewGroup(groupName)
            {
                HeaderAlignment = HorizontalAlignment.Left,
            };
            _listView.Groups.Add(lvGroup);
            foreach (var (keys, description) in items)
            {
                var lvi = new ListViewItem(FormatShortcut(keys), lvGroup);
                lvi.SubItems.Add(description);
                _listView.Items.Add(lvi);
            }
        }
    }

    private Panel BuildButtonPanel()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 56,
            Padding = new Padding(16, 12, 16, 12),
            BackColor = SystemColors.Control,
        };
        panel.Paint += (s, e) =>
        {
            using var pen = new Pen(SystemColors.ControlDark);
            e.Graphics.DrawLine(pen, 0, 0, panel.Width, 0);
        };

        var closeBtn = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.Cancel,
            Size = new Size(88, 28),
            Anchor = AnchorStyles.Right | AnchorStyles.Top,
        };
        closeBtn.Location = new Point(
            panel.ClientSize.Width - panel.Padding.Right - closeBtn.Width,
            panel.Padding.Top
        );
        panel.Controls.Add(closeBtn);

        AcceptButton = closeBtn;
        CancelButton = closeBtn;
        return panel;
    }

    private static List<(string Group, List<(Keys K, string Desc)> Items)> BuildGroups(
        MenuStrip menu
    )
    {
        var groups = new List<(string Group, List<(Keys K, string Desc)> Items)>();

        foreach (ToolStripItem topLevel in menu.Items)
        {
            if (topLevel is not ToolStripMenuItem topMenu)
                continue;

            string groupName = StripAccelerator(topMenu.Text ?? string.Empty);
            var items = new List<(Keys, string)>();
            CollectShortcuts(topMenu, items);

            if (ExtraByGroup.TryGetValue(groupName, out var extras))
                items.AddRange(extras);

            if (items.Count > 0)
                groups.Add((groupName, items));
        }

        groups.Add(("Results list", ResultsListShortcuts.ToList()));
        return groups;
    }

    private static void CollectShortcuts(
        ToolStripMenuItem parent,
        List<(Keys K, string Desc)> bucket
    )
    {
        foreach (ToolStripItem child in parent.DropDownItems)
        {
            if (child is not ToolStripMenuItem mi)
                continue;
            if (mi.ShortcutKeys != Keys.None)
                bucket.Add((mi.ShortcutKeys, StripAccelerator(mi.Text ?? string.Empty)));
            if (mi.HasDropDownItems)
                CollectShortcuts(mi, bucket);
        }
    }

    private static string StripAccelerator(string text) => text.Replace("&", string.Empty);

    private static string FormatShortcut(Keys keys)
    {
        var sb = new StringBuilder();
        if ((keys & Keys.Control) == Keys.Control)
            sb.Append("Ctrl+");
        if ((keys & Keys.Shift) == Keys.Shift)
            sb.Append("Shift+");
        if ((keys & Keys.Alt) == Keys.Alt)
            sb.Append("Alt+");

        Keys keyCode = keys & Keys.KeyCode;
        sb.Append(
            keyCode switch
            {
                Keys.Oemcomma => ",",
                Keys.OemPeriod => ".",
                Keys.OemMinus => "-",
                Keys.Oemplus => "+",
                Keys.Escape => "Esc",
                Keys.Return => "Enter",
                Keys.Back => "Backspace",
                Keys.Delete => "Del",
                Keys.PageUp => "PgUp",
                Keys.PageDown => "PgDn",
                _ => keyCode.ToString(),
            }
        );
        return sb.ToString();
    }
}
