namespace FileViewer.App.ViewModels;

/// <summary>
/// Tracks which row indices are checked for bulk actions (Delete, export), independent of the
/// DataGrid's own native selection (<c>DataGridRow.IsSelected</c> / <c>SelectedItems</c>) and
/// independent of which rows are currently rendered. The grid only ever materializes the rows on
/// screen, so a checkbox bound to the DataGrid's own selection can never represent "select all"
/// across the full filtered result set — every scroll or individual toggle would silently narrow it
/// back down. Living here instead means the set survives paging, sorting, and filtering until
/// something explicitly changes it.
///
/// "Select all" is stored as a <em>mode</em>, not as a list. Selecting two million filtered rows
/// used to mean building a two-million-entry set — tens of megabytes, and a full enumeration of the
/// result before anything happened. In that mode this holds the exceptions instead: the rows the
/// user unticked afterwards, which is almost always a handful. The actual row indices are only
/// materialized when an action needs them (see <see cref="Resolve"/>), against the rows in view at
/// that moment.
/// </summary>
public sealed class RowSelectionState
{
    /// <summary>In normal mode: the selected rows. In select-all mode: the exceptions, i.e. rows unticked since.</summary>
    private readonly HashSet<long> _tracked = [];

    private bool _allMatchingSelected;
    private int _matchingRowCount;

    public event Action? Changed;

    /// <summary>True while "select all" is in force — the selection is "everything matching the current filters, minus <see cref="_tracked"/>".</summary>
    public bool IsSelectAllMode => _allMatchingSelected;

    public int Count => _allMatchingSelected
        ? Math.Max(0, _matchingRowCount - _tracked.Count)
        : _tracked.Count;

    public bool IsSelected(long rowIndex) =>
        _allMatchingSelected ? !_tracked.Contains(rowIndex) : _tracked.Contains(rowIndex);

    public void SetSelected(long rowIndex, bool selected)
    {
        // In select-all mode the set holds what is *not* selected, so the two cases are mirrored.
        bool changed = _allMatchingSelected
            ? (selected ? _tracked.Remove(rowIndex) : _tracked.Add(rowIndex))
            : (selected ? _tracked.Add(rowIndex) : _tracked.Remove(rowIndex));

        if (changed) Changed?.Invoke();
    }

    /// <summary>
    /// Selects every row matching the current filters without enumerating them.
    /// <paramref name="matchingRowCount"/> is how many that currently is, for the "N selected"
    /// indicator — keep it current with <see cref="UpdateMatchingRowCount"/> when the filters change.
    /// </summary>
    public void SelectAllMatching(int matchingRowCount)
    {
        _tracked.Clear();
        _allMatchingSelected = true;
        _matchingRowCount = Math.Max(0, matchingRowCount);
        Changed?.Invoke();
    }

    /// <summary>
    /// Tells select-all mode how many rows now match, after a filter or an edit changed the result
    /// set. Without this the "N selected" count would keep quoting the number from when select-all
    /// was pressed.
    /// </summary>
    public void UpdateMatchingRowCount(int matchingRowCount)
    {
        if (!_allMatchingSelected || _matchingRowCount == matchingRowCount) return;

        _matchingRowCount = Math.Max(0, matchingRowCount);
        Changed?.Invoke();
    }

    public void Clear()
    {
        if (!_allMatchingSelected && _tracked.Count == 0) return;

        _tracked.Clear();
        _allMatchingSelected = false;
        _matchingRowCount = 0;
        Changed?.Invoke();
    }

    /// <summary>
    /// The selected rows among <paramref name="candidateRows"/>, in that order — how an action
    /// (delete, export) turns the selection into actual work. In select-all mode this is where the
    /// row list is finally materialized, and only over the rows currently in view: "all matching"
    /// means matching the filters in force, not every row the file ever had.
    /// </summary>
    public IEnumerable<long> Resolve(IReadOnlyList<long> candidateRows)
    {
        foreach (long rowIndex in candidateRows)
        {
            if (IsSelected(rowIndex)) yield return rowIndex;
        }
    }
}
