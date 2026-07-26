namespace FileViewer.App.ViewModels;

/// <summary>
/// Tracks which row indices are checked for bulk actions (Delete), independent of the DataGrid's
/// own native selection (<c>DataGridRow.IsSelected</c> / <c>SelectedItems</c>) and independent of
/// which page is currently rendered. The grid only ever materializes one page of
/// <see cref="RowViewModel"/> instances at a time (see <see cref="Collections.VirtualizingRowCollection"/>),
/// so a checkbox bound to the DataGrid's own per-page selection can never represent "select all"
/// across the full filtered result set — every page change or individual checkbox toggle would
/// silently narrow it back down to whatever's on the current page. Living here instead means the
/// set survives paging, sorting, and filtering until something explicitly changes it.
/// </summary>
public sealed class RowSelectionState
{
    private readonly HashSet<long> _selected = [];

    public event Action? Changed;

    public int Count => _selected.Count;

    public IReadOnlyCollection<long> SelectedRowIndices => _selected;

    public bool IsSelected(long rowIndex) => _selected.Contains(rowIndex);

    public void SetSelected(long rowIndex, bool selected)
    {
        bool changed = selected ? _selected.Add(rowIndex) : _selected.Remove(rowIndex);
        if (changed) Changed?.Invoke();
    }

    public void SelectAll(IEnumerable<long> rowIndices)
    {
        _selected.Clear();
        foreach (long index in rowIndices)
        {
            _selected.Add(index);
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (_selected.Count == 0) return;
        _selected.Clear();
        Changed?.Invoke();
    }
}
