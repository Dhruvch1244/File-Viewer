using System.Collections;
using System.Collections.Specialized;
using FileViewer.App.ViewModels;
using FileViewer.Core.Filtering;
using FileViewer.Core.Native;
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
/// Filtering decodes candidate rows (PRS §8's acknowledged slow path — there's no pre-extracted key
/// for anything but "_ID"), so every filter-mutating entry point here is async: all active filters
/// are compiled into one <see cref="RowPredicateSet"/> and run as a single parallel pass
/// (<see cref="RowQueryEngine"/>) on a background thread, and only the finished result touches
/// UI-bound state. WPF's DataGrid/IList contract is inherently synchronous (no async IList), so the
/// *result* still has to land back on the calling thread — what's backgrounded is the row-decoding
/// work, not the final assignment. Doing this synchronously on the UI thread (the original design)
/// is what caused clicks to go missing: the thread would freeze for however long the decode loop
/// took, and any input that arrived during that freeze was either dropped or landed on a control
/// that had since moved/changed.
///
/// A recompute in flight is cancellable, and is cancelled when it is superseded or when
/// <see cref="Dispose"/> runs. The second is what matters most in practice: closing a tab (or the
/// window) while a filter is scanning a multi-gigabyte file stops that scan then and there, instead
/// of leaving it to finish a computation whose result nothing will ever read.
/// </summary>
public sealed class VirtualizingRowCollection(FileViewerSession session, RowSelectionState selection) : IList, INotifyCollectionChanged, IDisposable
{
    /// <summary>Rows-per-page before MainWindow has had a chance to measure the DataGrid's actual visible height and call <see cref="SetPageSize"/> — a starting point now, not a hard limit.</summary>
    public const int DefaultPageSize = 20;

    private long[]? _effectiveOrder;
    private SearchQuery _searchQuery = SearchQuery.Empty;
    private long[]? _customOrderOverride;
    private int _pageIndex;
    private int _pageSize = DefaultPageSize;
    private readonly Dictionary<string, HashSet<string>> _columnValueFilters = new();
    private readonly Dictionary<string, ColumnPatternFilter> _columnPatternFilters = new();

    /// <summary>
    /// The <see cref="RowViewModel"/>s handed to the grid for the page currently on screen, reused
    /// until the page changes. The DataGrid re-queries an index several times while measuring,
    /// rendering and re-templating a row; without this each of those queries produced a fresh view
    /// model that had to resolve (and therefore decode) the row all over again.
    /// </summary>
    private readonly Dictionary<long, RowViewModel> _pageRowCache = new();

    private CancellationTokenSource? _recomputeCts;

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    private long[] EffectiveOrder => _effectiveOrder ??= [.. BuildCandidates(excludingColumn: null, CancellationToken.None)];

    public int TotalRowCount => EffectiveOrder.Length;
    public int PageIndex => _pageIndex;
    public int PageSize => _pageSize;
    public int PageCount => Math.Max(1, (int)Math.Ceiling(EffectiveOrder.Length / (double)PageSize));

    /// <summary>The main search's current (possibly multi-term) query — for the "what am I filtering by" summary.</summary>
    public SearchQuery CurrentSearchQuery => _searchQuery;

    /// <summary>Every column with an active Excel-style value filter, and the value set it's restricted to — for the "what am I filtering by" summary.</summary>
    public IReadOnlyDictionary<string, HashSet<string>> ColumnValueFilters => _columnValueFilters;

    /// <summary>Every column with an active per-column pattern filter (the ag-Grid-style filter row) — for the "what am I filtering by" summary.</summary>
    public IReadOnlyDictionary<string, ColumnPatternFilter> ColumnPatternFilters => _columnPatternFilters;

    /// <summary>
    /// Builds the current candidate row list: base/added rows, minus deleted, minus everything the
    /// active filters reject — all of it in a single pass (see <see cref="RowPredicateSet"/>).
    /// <paramref name="excludingColumn"/> leaves out one column's own value filter, which is what
    /// lets its value-picker popup still offer that column's full available value set (relative to
    /// every OTHER active filter) rather than only the values that survive its own current
    /// selection — the same "what could I pick instead" behavior Excel's column filter menus have.
    /// Runs entirely off plain fields/session state with no WPF dependency, so it's safe to call
    /// from a background thread (see the class remarks).
    /// </summary>
    private List<long> BuildCandidates(string? excludingColumn, CancellationToken cancellationToken)
    {
        List<long> candidates = BuildBaseOrder();

        IReadOnlyDictionary<string, HashSet<string>> valueFilters = _columnValueFilters;
        if (excludingColumn is not null && _columnValueFilters.ContainsKey(excludingColumn))
        {
            var withoutColumn = new Dictionary<string, HashSet<string>>(_columnValueFilters, StringComparer.Ordinal);
            withoutColumn.Remove(excludingColumn);
            valueFilters = withoutColumn;
        }

        RowPredicateSet predicates = RowPredicateSet.Compile(
            _searchQuery, valueFilters, _columnPatternFilters, session.FileIndex.Header);

        return RowQueryEngine.Filter(candidates, predicates, session.FileIndex, session.Overlay, session.Cache, cancellationToken);
    }

    private List<long> BuildBaseOrder()
    {
        if (_customOrderOverride is not null)
        {
            // A generic (non-"_ID") column-header sort was applied — it already reflects the full
            // base+added row set as of when it ran (see GridViewModel.SortByColumnAsync). Rows added
            // afterward won't appear here until the sort is reapplied or cleared; that's an
            // accepted limitation of this "arbitrary column" slow path, not a bug.
            return [.. _customOrderOverride];
        }

        UnmanagedArray<SortKey> current = session.CurrentOrder;
        var order = new List<long>(checked((int)current.Count) + 8);
        for (nuint i = 0; i < current.Count; i++)
        {
            order.Add(current[i].RowIndex);
        }
        order.AddRange(session.Overlay.GetLiveAddedOrDuplicatedRowIndices());
        return order;
    }

    /// <summary>Recomputes which rows are visible and in what order (synchronously, off whatever filters/sort are already set), resets to the first page, and notifies the grid to re-query everything. Cheap when no filter is active (just copies sort-key row indices); use the async filter-mutating methods below when a filter itself is what's changing, so that decode work never blocks the caller.</summary>
    public void Invalidate()
    {
        _effectiveOrder = null;
        _pageIndex = 0;
        RaiseReset();
    }

    /// <summary>
    /// Forces the grid to re-query every row on the current page (fresh <see cref="RowViewModel"/>
    /// instances) without touching the effective order, page index, or scroll position — used
    /// after a bulk selection change (select all / clear all) so on-screen checkboxes reflect it
    /// immediately, without the page-reset side effect <see cref="Invalidate"/> would cause.
    /// </summary>
    public void RefreshCurrentPage() => RaiseReset();

    /// <summary>Moves to a different page of the *current* effective order without recomputing it.</summary>
    public void GoToPage(int pageIndex)
    {
        int clamped = Math.Clamp(pageIndex, 0, PageCount - 1);
        if (clamped == _pageIndex) return;
        _pageIndex = clamped;
        RaiseReset();
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
        RaiseReset();
    }

    /// <summary>Sets (or clears, via <see cref="SearchQuery.Empty"/>) the main search — one or many terms. Decodes candidate rows on a background thread; see the class remarks.</summary>
    public async Task ApplySearchAsync(SearchQuery query)
    {
        _searchQuery = query;
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
    /// Decodes candidate rows on a background thread; the caller should keep the popup showing a
    /// loading state (or simply not open it yet) until this completes, rather than populating it
    /// with a partial/stale list synchronously. The result says whether it stopped at its cap, so
    /// the popup can say so instead of silently presenting a partial list as complete.
    /// </summary>
    public Task<DistinctValueResult> GetDistinctValuesForColumnAsync(string columnName) =>
        Task.Run(() => RowQueryEngine.GetDistinctValues(
            BuildCandidates(excludingColumn: columnName, CancellationToken.None),
            columnName, session.FileIndex, session.Overlay, session.Cache));

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
    /// here (it simply matches nothing until corrected); callers driving live-as-you-type validation
    /// should call <see cref="RowFilter.TryCompileRegex"/> themselves to flag the input red instead
    /// of silently returning zero rows.
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
        _searchQuery = SearchQuery.Empty;
        _columnValueFilters.Clear();
        _columnPatternFilters.Clear();
        await RecomputeAndInstallAsync();
    }

    private async Task RecomputeAndInstallAsync()
    {
        // Any pass already running can only produce a result for filters that no longer apply —
        // signal it to stop rather than letting it finish and then discarding its work. Each call
        // disposes its own token source in its own finally, once its await has returned and nothing
        // is using the token any more.
        CancellationTokenSource? previous = _recomputeCts;
        var cts = new CancellationTokenSource();
        _recomputeCts = cts;
        previous?.Cancel();

        try
        {
            long[] computed = await Task.Run(() => (long[])[.. BuildCandidates(excludingColumn: null, cts.Token)], cts.Token);
            if (cts.IsCancellationRequested) return;

            _effectiveOrder = computed;
            _pageIndex = 0;
            RaiseReset();
        }
        catch (OperationCanceledException)
        {
            // Superseded, or the file/section was closed while this was computing.
        }
        finally
        {
            if (ReferenceEquals(_recomputeCts, cts)) _recomputeCts = null;
            cts.Dispose();
        }
    }

    private void RaiseReset()
    {
        _pageRowCache.Clear();
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private long RowIndexAtPageOffset(int pageOffset) => EffectiveOrder[_pageIndex * PageSize + pageOffset];

    private RowViewModel RowAtPageOffset(int pageOffset)
    {
        long rowIndex = RowIndexAtPageOffset(pageOffset);
        if (_pageRowCache.TryGetValue(rowIndex, out RowViewModel? existing))
        {
            return existing;
        }

        var row = new RowViewModel(session, rowIndex, selection);
        _pageRowCache[rowIndex] = row;
        return row;
    }

    public int Count => Math.Min(PageSize, Math.Max(0, EffectiveOrder.Length - _pageIndex * PageSize));

    public object? this[int index]
    {
        get => RowAtPageOffset(index);
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
            yield return RowAtPageOffset(i);
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
            array.SetValue(RowAtPageOffset(i), index + i);
        }
    }

    /// <summary>Stops any filter pass still running (the tab or window is going away) and drops the page's row view models.</summary>
    public void Dispose()
    {
        // Not disposed here: the call that created it disposes it in its own finally, once its
        // await has returned.
        _recomputeCts?.Cancel();
        _recomputeCts = null;
        _pageRowCache.Clear();
    }

    int IList.Add(object? value) => throw new NotSupportedException();
    void IList.Clear() => throw new NotSupportedException();
    void IList.Insert(int index, object? value) => throw new NotSupportedException();
    void IList.Remove(object? value) => throw new NotSupportedException();
    void IList.RemoveAt(int index) => throw new NotSupportedException();
}
