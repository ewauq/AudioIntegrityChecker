using System.Collections;
using System.Runtime.Versioning;

namespace AudioIntegrityChecker.UI;

/// <summary>
/// Sorts a ListView column alphabetically (ascending or descending).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ColumnSorter : IComparer
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
