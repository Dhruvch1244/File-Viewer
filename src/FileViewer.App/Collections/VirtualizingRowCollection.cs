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
/// current page window, not the full row set. Checkbox-based selection (which rows survive
/// paging) is tracked separately in <see cref="ViewModels.RowSelectionState"/>, not here — see its
/// doc comment for why that has to live outside any single page of <see cref="RowViewModel"/>s.
///
/// Filtering decodes every candidate row (PRS §8's acknowledged slow path — there's no
/// pre-extracted key for anything but "_ID"), so every filter-mutating entry point here is async:
/// it computes the new effective order on a background thread via <see cref="Task.Run(Action)"/>
/// and only touches UI-bound state once that's done. WPF's DataGrid/IList contract is inherently
/// synchronous (no async IList), so the *result* still has to land back on the calling thread —
/// what's backgrounded is the row-decoding work, not the final assignment. Doing this synchronously
/// on the UI thread (the previous design) is what caused clicks to go missing: the thread would
/// freeze for however long the decode loop took, and any input that arrived during that freeze was
/// either dropped or landed on a control that had since moved/changed. <see cref="ViewModels.GridViewModel.IsBusy"/>
/// is the other half of the fix — it's what stops a second filter operation (or an edit that would
/// race with <see cref="Overlay.EditOverlay"/>/<see cref="Caching.DecodedRowCache"/> reads the
/// background computation is doing) from starting while one is already in flight.
/// </summary>
public sealed class VirtualizingRowCollection(FileViewerSession session, RowSelectionState selection) : IList, INotifyCollectionChanged
{
    /// <summary>Rows-per-page before MainWindow has had a chance to measure the DataGrid's actual visible height and call <see cref="SetPageSize"/> — a starting point now, not a hard limit.</summary>
    public const int DefaultPageSize = 20;

    private long[]? _effectiveOrder;
    private string? _filterText;
    private long[]? _customOrderOverride;
    private int _pageIndex;
    private int _pageSize = DefaultPageSize;
    private readonly Dictionary<string, HashSet<string>> _columnValueFilters = new();
    private readonly Dictionary<string, ColumnPatternFilter> _columnPatternFilters = new();

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    private long[] EffectiveOrder => _effectiveOrder ??= [.. BuildCandidates(excludingColumn: null)];

    public int TotalRowCount => EffectiveOrder.Length;
    public int PageIndex => _pageIndex;
    public int PageSize => _pageSize;
    public int PageCount => Math.Max(1, (int)Math.Ceiling(EffectiveOrder.Length / (double)PageSize));

    /// <summary>The active search-box text, or null/empty if none — for the "what am I filtering by" summary.</summary>
    public string? CurrentSearchText => _filterText;

    /// <summary>Every column with an active Excel-style value filter, and the value set it's restricted to — for the "what am I filtering by" summary.</summary>
    public IReadOnlyDictionary<string, HashSet<string>> ColumnValueFilters => _columnValueFilters;

    /// <summary>Every column with an active per-column pattern filter (the ag-Grid-style filter row) — for the "what am I filtering by" summary.</summary>
    public IReadOnlyDictionary<string, ColumnPatternFilter> ColumnPatternFilters => _columnPatternFilters;

    /// <summary>
    /// Builds the current candidate row list: base/added rows, minus deleted, minus the search
    /// filter, minus every active per-column value filter except <paramref name="excludingColumn"/>,
    /// minus every active per-column pattern filter. Excluding one column's own value filter is
    /// what lets its value-picker popup still offer that column's full available value set
    /// (relative to every OTHER active filter) rather than only the values that survive its own
    /// current selection — the same "what could I pick instead" behavior Excel's column filter
    /// menus have. Runs entirely off plain fields/session state with no WPF dependency, so it's
    /// safe to call from a background thread (see the class remarks).
    /// </summary>
    private List<long> BuildCandidates(string? excludingColumn)
    {
        IEnumerable<long> candidates;
        if (_customOrderOverride is not null)
        {
            // A generic (non-"_ID") column-header sort was applied — it already reflects the full
            // base+added row set as of when it ran (see GridViewModel.SortByColumnAsync). Rows added
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
            order.AddRange(session.Overlay.GetLiveAddedOrDuplicatedRowIndices());
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

        foreach ((string columnName, ColumnPatternFilter filter) in _columnPatternFilters)
        {
            result = RowFilter.FilterByColumnPattern(result, columnName, filter.Pattern, filter.UseRegex, session.FileIndex, session.Overlay, session.Cache);
        }

        return result;
    }

    /// <summary>Recomputes which rows are visible and in what order (synchronously, off whatever filters/sort are already set), resets to the first page, and notifies the grid to re-query everything. Cheap when no filter is active (just copies sort-key row indices); use the async filter-mutating methods below when a filter itself is what's changing, so that decode work never blocks the caller.</summary>
    public void Invalidate()
    {
        _effectiveOrder = null;
        _pageIndex = 0;
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>
    /// Forces the grid to re-query every row on the current page (fresh <see cref="RowViewModel"/>
    /// instances) without touching the effective order, page index, or scroll position — used
    /// after a bulk selection change (select all / clear all) so on-screen checkboxes reflect it
    /// immediately, without the page-reset side effect <see cref="Invalidate"/> would cause.
    /// </summary>
    public void RefreshCurrentPage() =>
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));

    /// <summary>Moves to a different page of the *current* effective order without recomputing it.</summary>
    public void GoToPage(int pageIndex)
    {
        int clamped = Math.Clamp(pageIndex, 0, PageCount - 1);
        if (clamped == _pageIndex) return;
        _pageIndex = clamped;
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>
    /// Changes how many rows one page holds — driven by MainWindow measuring how many rows actually
    /// fit in the DataGrid's current visible height, so a page fills the available screen space
    /// instead of being pinned to a fixed row count. Keeps the current page index valid against the
    /// new page count but otherwise leaves the effective order/scroll target alone — resizing the
    /// window shouldn't discard an active filter or sort, or jump back to page 0.
    /// </summary>
    public void SetPageSize(int pageSize)
    {
        int clamped = Math.Max(1, pageSize);
        if (clamped == _pageSize) return;

        _pageSize = clamped;
        _pageIndex = Math.Clamp(_pageIndex, 0, PageCount - 1);
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>Sets (or clears, via null/empty) the arbitrary-column substring filter. Decodes every candidate row on a background thread — see the class remarks.</summary>
    public async Task ApplyFilterAsync(string? filterText)
    {
        _filterText = filterText;
        await RecomputeAndInstallAsync();
    }

    /// <summary>Overrides the base-row order with a precomputed sort (used for clicking a non-"_ID" column header — see <see cref="ViewModels.GridViewModel.SortByColumnAsync"/>). The sort itself is already computed by the caller; installing it here is cheap, so this stays synchronous.</summary>
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

    /// <summary>
    /// Every distinct value <paramref name="columnName"/> takes among rows passing every OTHER
    /// active filter — the value list an Excel-style filter popup for that column should offer.
    /// Decodes every candidate row on a background thread; the caller should keep the popup showing
    /// a loading state (or simply not open it yet) until this completes, rather than populating it
    /// with a partial/stale list synchronously.
    /// </summary>
    public Task<List<string>> GetDistinctValuesForColumnAsync(string columnName) =>
        Task.Run(() => RowFilter.GetDistinctValues(BuildCandidates(excludingColumn: columnName), columnName, session.FileIndex, session.Overlay, session.Cache));

    /// <summary>The currently-selected value set for a column's filter, or null if no filter is active on it.</summary>
    public HashSet<string>? GetColumnValueFilter(string columnName) =>
        _columnValueFilters.TryGetValue(columnName, out HashSet<string>? values) ? values : null;

    public bool HasColumnValueFilter(string columnName) => _columnValueFilters.ContainsKey(columnName);

    /// <summary>Sets (or clears, by passing null) which values of <paramref name="columnName"/> are allowed through. Background-recomputes the effective order — see the class remarks.</summary>
    public async Task SetColumnValueFilterAsync(string columnName, HashSet<string>? allowedValues)
    {
        if (allowedValues is null)
        {
            _columnValueFilters.Remove(columnName);
        }
        else
        {
            _columnValueFilters[columnName] = allowedValues;
        }
        await RecomputeAndInstallAsync();
    }

    /// <summary>The active pattern filter for a column (the ag-Grid-style filter row), or null if none.</summary>
    public ColumnPatternFilter? GetColumnPatternFilter(string columnName) =>
        _columnPatternFilters.TryGetValue(columnName, out ColumnPatternFilter filter) ? filter : null;

    /// <summary>
    /// Sets (or clears, by passing a null/empty pattern) a per-column text/regex filter — the
    /// ag-Grid-style "floating filter row" directly under the column headers, one box per column,
    /// applied together (AND) with the search box and any Excel-style value filters. Background-
    /// recomputes the effective order — see the class remarks. An invalid regex pattern is accepted
    /// here (it simply matches nothing until corrected — see <see cref="RowFilter.FilterByColumnPattern"/>);
    /// callers driving live-as-you-type validation should call <see cref="RowFilter.TryCompileRegex"/>
    /// themselves to flag the input red instead of silently returning zero rows.
    /// </summary>
    public async Task SetColumnPatternFilterAsync(string columnName, string? pattern, bool useRegex)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            _columnPatternFilters.Remove(columnName);
        }
        else
        {
            _columnPatternFilters[columnName] = new ColumnPatternFilter(pattern, useRegex);
        }
        await RecomputeAndInstallAsync();
    }

    /// <summary>Clears every active filter (search, column value filters, column pattern filters) in one step — the "what am I filtering by" summary's "Clear all" action.</summary>
    public async Task ClearAllFiltersAsync()
    {
        _filterText = null;
        _columnValueFilters.Clear();
        _columnPatternFilters.Clear();
        await RecomputeAndInstallAsync();
    }

    private async Task RecomputeAndInstallAsync()
    {
        long[] computed = await Task.Run(() => (long[])[.. BuildCandidates(excludingColumn: null)]);
        _effectiveOrder = computed;
        _pageIndex = 0;
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private long RowIndexAtPageOffset(int pageOffset) => EffectiveOrder[_pageIndex * PageSize + pageOffset];

    public int Count => Math.Min(PageSize, Math.Max(0, EffectiveOrder.Length - _pageIndex * PageSize));

    public object? this[int index]
    {
        get => new RowViewModel(session, RowIndexAtPageOffset(index), selection);
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
            yield return new RowViewModel(session, RowIndexAtPageOffset(i), selection);
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
            array.SetValue(new RowViewModel(session, RowIndexAtPageOffset(i), selection), index + i);
        }
    }

    int IList.Add(object? value) => throw new NotSupportedException();
    void IList.Clear() => throw new NotSupportedException();
    void IList.Insert(int index, object? value) => throw new NotSupportedException();
    void IList.Remove(object? value) => throw new NotSupportedException();
    void IList.RemoveAt(int index) => throw new NotSupportedException();
}

/// <summary>A single column's ag-Grid-style filter-row entry: the raw text the user typed, and whether it should be interpreted as a regex (vs. a plain case-insensitive substring).</summary>
public readonly record struct ColumnPatternFilter(string Pattern, bool UseRegex);
