using System.Collections;
using System.Collections.Specialized;
using FileViewer.App.ViewModels;
using FileViewer.Core.Filtering;
using FileViewer.Core.Native;
using FileViewer.Core.Overlay;
using FileViewer.Core.Session;

namespace FileViewer.App.Collections;

/// <summary>
/// On-demand data source for the WPF <c>DataGrid</c>: resolves
/// <c>pageVisualIndex -&gt; base/synthetic row index -&gt; <see cref="RowViewModel"/></c> only when the
/// grid actually asks for that index, so rendering never materializes every row in the file (the
/// crux of keeping a 2 GB file's grid off the GC heap). Implements the plain, non-generic
/// <see cref="IList"/> — what <c>ItemsControl</c>/<c>DataGrid</c> actually probe for to support
/// virtualization without realizing the whole collection up front.
///
/// "Effective order" (which rows are visible, and in what order) is computed from the session's
/// current sort order plus any live Added/Duplicated rows, skipping Deleted rows entirely — a
/// deleted row disappears from the grid rather than rendering as a gap or a blank row. This is
/// cached and only rebuilt when <see cref="Invalidate"/> is called (after a structural edit:
/// add/delete/duplicate/undo/sort — never for a plain cell edit, which doesn't change row
/// membership or order).
///
/// On top of that, the collection only ever *exposes* one <see cref="PageSize"/>-row page of the
/// effective order at a time — <see cref="Count"/>/the indexer/enumerator all operate on the
/// current page window, not the full row set. This means "select all" (which operates against
/// whatever the DataGrid's ItemsSource currently contains) naturally scopes to "select all on this
/// page" rather than materializing a selection across potentially millions of rows.
/// </summary>
public sealed class VirtualizingRowCollection(FileViewerSession session) : IList, INotifyCollectionChanged
{
    public const int PageSize = 20;

    private long[]? _effectiveOrder;
    private string? _filterText;
    private long[]? _customOrderOverride;
    private int _pageIndex;
    private readonly Dictionary<string, HashSet<string>> _columnValueFilters = new();

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    private long[] EffectiveOrder => _effectiveOrder ??= [.. BuildCandidates(excludingColumn: null)];

    public int TotalRowCount => EffectiveOrder.Length;
    public int PageIndex => _pageIndex;
    public int PageCount => Math.Max(1, (int)Math.Ceiling(EffectiveOrder.Length / (double)PageSize));

    /// <summary>
    /// Builds the current candidate row list: base/added rows, minus deleted, minus the search
    /// filter, minus every active per-column value filter except <paramref name="excludingColumn"/>.
    /// Excluding one column's own filter is what lets its value-picker popup still offer that
    /// column's full available value set (relative to every OTHER active filter) rather than only
    /// the values that survive its own current selection — the same "what could I pick instead"
    /// behavior Excel's column filter menus have.
    /// </summary>
    private List<long> BuildCandidates(string? excludingColumn)
    {
        IEnumerable<long> candidates;
        if (_customOrderOverride is not null)
        {
            // A generic (non-"_ID") column-header sort was applied — it already reflects the full
            // base+added row set as of when it ran (see GridViewModel.SortByColumn). Rows added
            // afterward won't appear here until the sort is reapplied or cleared; that's an
            // accepted limitation of this "arbitrary column" slow path, not a bug.
            candidates = _customOrderOverride;
        }
        else
        {
            var order = new List<long>();
            UnmanagedArray<SortKey> current = session.CurrentOrder;
            for (nuint i = 0; i < current.Count; i++)
            {
                order.Add(current[i].RowIndex);
            }
            foreach (RowOp op in session.Overlay.RowOps)
            {
                if ((op.Type is RowOpType.Add or RowOpType.Duplicate) && session.Overlay.GetRowState(op.RowIndex) != RowState.Deleted)
                {
                    order.Add(op.RowIndex);
                }
            }
            candidates = order;
        }

        var result = new List<long>();
        foreach (long rowIndex in candidates)
        {
            if (session.Overlay.GetRowState(rowIndex) != RowState.Deleted)
            {
                result.Add(rowIndex);
            }
        }

        if (!string.IsNullOrEmpty(_filterText))
        {
            result = RowFilter.Filter(result, _filterText, session.FileIndex, session.Overlay, session.Cache);
        }

        foreach ((string columnName, HashSet<string> allowedValues) in _columnValueFilters)
        {
            if (columnName == excludingColumn) continue;
            result = RowFilter.FilterByColumnValues(result, columnName, allowedValues, session.FileIndex, session.Overlay, session.Cache);
        }

        return result;
    }

    /// <summary>Recomputes which rows are visible and in what order, resets to the first page, and notifies the grid to re-query everything.</summary>
    public void Invalidate()
    {
        _effectiveOrder = null;
        _pageIndex = 0;
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>Moves to a different page of the *current* effective order without recomputing it.</summary>
    public void GoToPage(int pageIndex)
    {
        int clamped = Math.Clamp(pageIndex, 0, PageCount - 1);
        if (clamped == _pageIndex) return;
        _pageIndex = clamped;
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>Sets (or clears, via null/empty) the arbitrary-column substring filter and invalidates the effective order.</summary>
    public void ApplyFilter(string? filterText)
    {
        _filterText = filterText;
        Invalidate();
    }

    /// <summary>Overrides the base-row order with a precomputed sort (used for clicking a non-"_ID" column header — see <see cref="ViewModels.GridViewModel.SortByColumn"/>).</summary>
    public void ApplyCustomOrder(long[] order)
    {
        _customOrderOverride = order;
        Invalidate();
    }

    /// <summary>Reverts to <see cref="Session.FileViewerSession.CurrentOrder"/> (file order, or the fast "_ID" sort).</summary>
    public void ClearCustomOrder()
    {
        _customOrderOverride = null;
        Invalidate();
    }

    /// <summary>Every row index currently matching the active search/column filters, across every page — the full set "select all" should act on, not just the page currently on screen.</summary>
    public IReadOnlyList<long> GetAllRowIndices() => EffectiveOrder;

    /// <summary>Every distinct value <paramref name="columnName"/> takes among rows passing every OTHER active filter — the value list an Excel-style filter popup for that column should offer.</summary>
    public List<string> GetDistinctValuesForColumn(string columnName) =>
        RowFilter.GetDistinctValues(BuildCandidates(excludingColumn: columnName), columnName, session.FileIndex, session.Overlay, session.Cache);

    /// <summary>The currently-selected value set for a column's filter, or null if no filter is active on it.</summary>
    public HashSet<string>? GetColumnValueFilter(string columnName) =>
        _columnValueFilters.TryGetValue(columnName, out HashSet<string>? values) ? values : null;

    public bool HasColumnValueFilter(string columnName) => _columnValueFilters.ContainsKey(columnName);

    /// <summary>Sets (or clears, by passing null) which values of <paramref name="columnName"/> are allowed through.</summary>
    public void SetColumnValueFilter(string columnName, HashSet<string>? allowedValues)
    {
        if (allowedValues is null)
        {
            _columnValueFilters.Remove(columnName);
        }
        else
        {
            _columnValueFilters[columnName] = allowedValues;
        }
        Invalidate();
    }

    private long RowIndexAtPageOffset(int pageOffset) => EffectiveOrder[_pageIndex * PageSize + pageOffset];

    public int Count => Math.Min(PageSize, Math.Max(0, EffectiveOrder.Length - _pageIndex * PageSize));

    public object? this[int index]
    {
        get => new RowViewModel(session, RowIndexAtPageOffset(index));
        set => throw new NotSupportedException();
    }

    public bool IsFixedSize => false;
    public bool IsReadOnly => true;
    public bool IsSynchronized => false;
    public object SyncRoot { get; } = new();

    public IEnumerator GetEnumerator()
    {
        int count = Count;
        for (int i = 0; i < count; i++)
        {
            yield return new RowViewModel(session, RowIndexAtPageOffset(i));
        }
    }

    public bool Contains(object? value) =>
        value is RowViewModel row && IndexOfRowIndex(row.RowIndex) >= 0;

    public int IndexOf(object? value) =>
        value is RowViewModel row ? IndexOfRowIndex(row.RowIndex) : -1;

    private int IndexOfRowIndex(long rowIndex)
    {
        int count = Count;
        for (int i = 0; i < count; i++)
        {
            if (RowIndexAtPageOffset(i) == rowIndex) return i;
        }
        return -1;
    }

    public void CopyTo(Array array, int index)
    {
        int count = Count;
        for (int i = 0; i < count; i++)
        {
            array.SetValue(new RowViewModel(session, RowIndexAtPageOffset(i)), index + i);
        }
    }

    int IList.Add(object? value) => throw new NotSupportedException();
    void IList.Clear() => throw new NotSupportedException();
    void IList.Insert(int index, object? value) => throw new NotSupportedException();
    void IList.Remove(object? value) => throw new NotSupportedException();
    void IList.RemoveAt(int index) => throw new NotSupportedException();
}
