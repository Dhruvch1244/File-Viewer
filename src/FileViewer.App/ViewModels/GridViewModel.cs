using System.Collections.ObjectModel;
using System.Windows.Input;
using FileViewer.App.Collections;
using FileViewer.App.Common;
using FileViewer.Core.Filtering;
using FileViewer.Core.Overlay;
using FileViewer.Core.Session;
using FileViewer.Core.Sorting;

namespace FileViewer.App.ViewModels;

/// <summary>
/// Owns the grid's data source, column list, current selection, and the row-operation/sort
/// commands. Clicking the "_ID" column header uses Core's fast unmanaged-key sort
/// (<see cref="FileViewerSession.ApplySort"/>, synchronous — it only permutes a pre-extracted key
/// array, no row decoding); clicking any other column header, applying the search, an Excel-style
/// column value filter, or the per-column filter row all fall back to reading candidate rows (PRS
/// §8's acknowledged slower path) and run on a background thread via <see cref="IsBusy"/>-gated
/// async methods — see <see cref="VirtualizingRowCollection"/>'s remarks for why: doing this
/// synchronously on the UI thread is what caused input to go missing while a filter was computing.
///
/// The search is a <see cref="SearchQuery"/> of any number of terms rather than a single substring:
/// the properties here (<see cref="SearchText"/>, <see cref="SearchColumn"/>,
/// <see cref="SearchMode"/>, <see cref="SearchCaseSensitive"/>) describe the one term currently
/// being typed, which <see cref="ApplySearchCommand"/> searches with on its own and
/// <see cref="AddSearchTermCommand"/> adds to the terms already applied.
/// </summary>
public sealed class GridViewModel : ObservableObject
{
    /// <summary>A file can have hundreds of columns; showing them all at once is unusable, so only the first this-many are visible by default — the rest are still reachable via the column chooser.</summary>
    public const int DefaultVisibleColumnCount = 20;

    /// <summary>The "any column" entry of the search-scope list — the default, and what the plain search box has always meant.</summary>
    public const string AllColumnsScope = "All columns";

    private RowViewModel? _selectedRow;
    private int _frozenColumnCount;
    private string _searchText = string.Empty;
    private string _searchColumn = AllColumnsScope;
    private SearchModeOption _searchMode = SearchModeOption.Default;
    private bool _searchCaseSensitive;
    private bool _matchAllSearchTerms = true;
    private string _columnSearchText = string.Empty;
    private string? _currentSortColumn;
    private SortDirection _currentSortDirection = SortDirection.Ascending;
    private bool _isBusy;

    public GridViewModel(FileViewerSession session)
    {
        Session = session;
        Selection.Changed += OnSelectionChanged;
        Rows = new VirtualizingRowCollection(session, Selection);
        Rows.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(PageIndex));
            OnPropertyChanged(nameof(PageCount));
            OnPropertyChanged(nameof(PageLabel));
            OnPropertyChanged(nameof(TotalRowCount));
            OnPropertyChanged(nameof(CanGoToPreviousPage));
            OnPropertyChanged(nameof(CanGoToNextPage));
            RebuildActiveFilterChips();
        };
        ColumnNames = session.FileIndex.Header.ColumnNames;
        SearchColumns = [AllColumnsScope, .. ColumnNames];
        Columns = new ObservableCollection<GridColumnInfo>(
            ColumnNames.Select((name, index) => new GridColumnInfo(name, index) { IsVisible = index < DefaultVisibleColumnCount }));

        ClearSortCommand = RelayCommand.Create(ClearSort, () => CurrentSortColumn is not null && !IsBusy);
        AddRowCommand = RelayCommand.Create(AddRow, () => !IsBusy);
        DuplicateSelectedRowCommand = RelayCommand.Create(DuplicateSelectedRow, () => SelectedRow is not null && !IsBusy);
        DeleteSelectedRowsCommand = RelayCommand.Create(DeleteSelectedRows, () => Selection.Count > 0 && !IsBusy);
        UndoCommand = RelayCommand.Create(() => { Session.Overlay.Undo(); Rows.Invalidate(); }, () => Session.Overlay.CanUndo && !IsBusy);

        ApplySearchCommand = new AsyncRelayCommand(ApplySearchAsync, () => !IsBusy);
        AddSearchTermCommand = new AsyncRelayCommand(AddSearchTermAsync, () => !IsBusy && !string.IsNullOrEmpty(SearchText));
        ClearSearchCommand = new AsyncRelayCommand(
            () => { SearchText = string.Empty; return RunBusyAsync(() => Rows.ApplySearchAsync(SearchQuery.Empty)); }, () => !IsBusy);
        ClearAllFiltersCommand = new AsyncRelayCommand(
            () => { SearchText = string.Empty; return RunBusyAsync(() => Rows.ClearAllFiltersAsync()); }, () => !IsBusy && HasActiveFilters);

        PreviousPageCommand = RelayCommand.Create(() => Rows.GoToPage(Rows.PageIndex - 1), () => CanGoToPreviousPage && !IsBusy);
        NextPageCommand = RelayCommand.Create(() => Rows.GoToPage(Rows.PageIndex + 1), () => CanGoToNextPage && !IsBusy);
    }

    public FileViewerSession Session { get; }
    public VirtualizingRowCollection Rows { get; }
    public IReadOnlyList<string> ColumnNames { get; }
    public ObservableCollection<GridColumnInfo> Columns { get; }

    /// <summary>
    /// True while a background filter/sort computation is in flight. Bound to disable the
    /// controls that would start another one (or an edit that would race with it) — not a hard
    /// correctness requirement (<see cref="EditOverlay"/> and <see cref="Caching.DecodedRowCache"/>
    /// are both safe under concurrent access regardless), just what keeps two overlapping
    /// operations from confusing the user or clobbering each other's results.
    /// </summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>Every currently-active filter, in a form the toolbar can render as removable chips — the "what am I filtering by" summary.</summary>
    public ObservableCollection<ActiveFilterChip> ActiveFilterChips { get; } = new();

    public bool HasActiveFilters => ActiveFilterChips.Count > 0;

    /// <summary>Name of the column the grid is currently sorted by, or null if unsorted (file order). Drives the header sort-arrow indicator in code-behind.</summary>
    public string? CurrentSortColumn
    {
        get => _currentSortColumn;
        private set => SetField(ref _currentSortColumn, value);
    }

    public SortDirection CurrentSortDirection
    {
        get => _currentSortDirection;
        private set => SetField(ref _currentSortDirection, value);
    }

    public int FrozenColumnCount
    {
        get => _frozenColumnCount;
        set => SetField(ref _frozenColumnCount, Math.Clamp(value, 0, ColumnNames.Count));
    }

    public RowViewModel? SelectedRow
    {
        get => _selectedRow;
        set => SetField(ref _selectedRow, value);
    }

    /// <summary>Which rows are checked for bulk actions (Delete) — survives paging/sorting/filtering; see <see cref="RowSelectionState"/>.</summary>
    public RowSelectionState Selection { get; } = new();

    /// <summary>Bindable mirror of <see cref="RowSelectionState.Count"/> for the toolbar's "N selected" indicator.</summary>
    public int SelectedCount => Selection.Count;

    /// <summary>
    /// The term currently typed into the search box. Applied on <see cref="ApplySearchCommand"/>
    /// (not per-keystroke — searching decodes candidate rows, so it's a deliberate action, not a
    /// live-as-you-type one), either replacing the search or, via
    /// <see cref="AddSearchTermCommand"/>, adding to it.
    /// </summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetField(ref _searchText, value)) CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>What the typed term applies to: <see cref="AllColumnsScope"/> or one specific column.</summary>
    public string SearchColumn
    {
        get => _searchColumn;
        set => SetField(ref _searchColumn, value);
    }

    /// <summary>How the typed term is compared — contains, equals, starts with, regex, or their negations.</summary>
    public SearchModeOption SearchMode
    {
        get => _searchMode;
        set => SetField(ref _searchMode, value);
    }

    public bool SearchCaseSensitive
    {
        get => _searchCaseSensitive;
        set => SetField(ref _searchCaseSensitive, value);
    }

    /// <summary>With several search terms active: true = a row must match all of them, false = any one is enough.</summary>
    public bool MatchAllSearchTerms
    {
        get => _matchAllSearchTerms;
        set
        {
            if (!SetField(ref _matchAllSearchTerms, value)) return;
            OnPropertyChanged(nameof(SearchCombineLabel));

            // Re-apply straight away so the toggle is visibly the thing that changed the row set.
            // While a filter is already running, the new mode simply takes effect on the next search
            // rather than starting a second overlapping one.
            if (!IsBusy && Rows.CurrentSearchQuery.Criteria.Count > 1)
            {
                _ = RunBusyAsync(() => Rows.ApplySearchAsync(Rows.CurrentSearchQuery with { Combine = CurrentCombineMode }));
            }
        }
    }

    public string SearchCombineLabel => MatchAllSearchTerms ? "Match all" : "Match any";

    /// <summary>Scope options for the search box: "All columns" followed by every column of this section.</summary>
    public IReadOnlyList<string> SearchColumns { get; }

    /// <summary>The comparison options offered next to the search box.</summary>
    public IReadOnlyList<SearchModeOption> SearchModes { get; } = SearchModeOption.All;

    private SearchCombineMode CurrentCombineMode => MatchAllSearchTerms ? SearchCombineMode.All : SearchCombineMode.Any;

    /// <summary>Live substring filter over <see cref="Columns"/>' names, applied per-keystroke by the column-chooser popup (cheap — it's just filtering an in-memory name list, not decoding rows).</summary>
    public string ColumnSearchText
    {
        get => _columnSearchText;
        set => SetField(ref _columnSearchText, value);
    }

    public int PageIndex => Rows.PageIndex;
    public int PageCount => Rows.PageCount;
    public int TotalRowCount => Rows.TotalRowCount;
    public string PageLabel => $"Page {PageIndex + 1} of {PageCount}";
    public bool CanGoToPreviousPage => Rows.PageIndex > 0;
    public bool CanGoToNextPage => Rows.PageIndex < Rows.PageCount - 1;

    public ICommand ClearSortCommand { get; }
    public ICommand AddRowCommand { get; }
    public ICommand DuplicateSelectedRowCommand { get; }
    public ICommand DeleteSelectedRowsCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand ApplySearchCommand { get; }
    public ICommand AddSearchTermCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand ClearAllFiltersCommand { get; }
    public ICommand PreviousPageCommand { get; }
    public ICommand NextPageCommand { get; }

    /// <summary>Invoked from the DataGrid's Sorting event (column header click). Toggles ascending/descending on repeated clicks of the same column. No-ops while <see cref="IsBusy"/> — the caller (MainWindow) doesn't need its own guard.</summary>
    public async Task SortByColumnAsync(string columnName)
    {
        if (IsBusy) return;

        SortDirection direction = columnName == CurrentSortColumn && CurrentSortDirection == SortDirection.Ascending
            ? SortDirection.Descending
            : SortDirection.Ascending;

        if (columnName == "_ID")
        {
            Rows.ClearCustomOrder();
            Session.ApplySort(direction);
        }
        else
        {
            await RunBusyAsync(async () =>
            {
                Session.ClearSort();
                List<long> candidates = CollectBaseAndAddedRowIndices();
                List<long> sorted = await Task.Run(
                    () => RowSorter.SortByColumn(candidates, columnName, direction, Session.FileIndex, Session.Overlay, Session.Cache));
                Rows.ApplyCustomOrder([.. sorted]);
            });
        }

        CurrentSortColumn = columnName;
        CurrentSortDirection = direction;
    }

    public void ClearSort()
    {
        Session.ClearSort();
        Rows.ClearCustomOrder();
        CurrentSortColumn = null;
    }

    /// <summary>Sets (or clears) the ag-Grid-style per-column filter row's pattern for one column. No-ops while <see cref="IsBusy"/> — a fast-typing user's earlier keystroke won't be applied out of order after a later one.</summary>
    public Task SetColumnPatternFilterAsync(string columnName, string? pattern, bool useRegex) =>
        IsBusy ? Task.CompletedTask : RunBusyAsync(() => Rows.SetColumnPatternFilterAsync(columnName, pattern, useRegex));

    /// <summary>Sets (or clears) an Excel-style column value filter. No-ops while <see cref="IsBusy"/>.</summary>
    public Task SetColumnValueFilterAsync(string columnName, HashSet<string>? allowedValues) =>
        IsBusy ? Task.CompletedTask : RunBusyAsync(() => Rows.SetColumnValueFilterAsync(columnName, allowedValues));

    /// <summary>
    /// Every distinct value a column takes, for the Excel-style filter popup — decodes every
    /// candidate row on a background thread. Returns an empty list (rather than running) while
    /// <see cref="IsBusy"/>; the caller (the popup) is expected to already be blocked from opening
    /// a second lookup in that state, this is just a safety net.
    /// </summary>
    public Task<DistinctValueResult> GetDistinctValuesForColumnAsync(string columnName) =>
        IsBusy
            ? Task.FromResult(new DistinctValueResult([], false))
            : RunBusyAsync(() => Rows.GetDistinctValuesForColumnAsync(columnName));

    /// <summary>Runs a background filter/sort computation with <see cref="IsBusy"/> set for its duration.</summary>
    private async Task RunBusyAsync(Func<Task> operation)
    {
        IsBusy = true;
        try
        {
            await operation();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Same as <see cref="RunBusyAsync(Func{Task})"/> but for an operation that returns a value.</summary>
    private async Task<T> RunBusyAsync<T>(Func<Task<T>> operation)
    {
        IsBusy = true;
        try
        {
            return await operation();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RebuildActiveFilterChips()
    {
        ActiveFilterChips.Clear();

        SearchQuery query = Rows.CurrentSearchQuery;
        for (int i = 0; i < query.Criteria.Count; i++)
        {
            int index = i; // captured per chip: removing one term must not disturb the others
            ActiveFilterChips.Add(new ActiveFilterChip(
                query.Criteria[i].Describe(),
                () => RunBusyAsync(() => Rows.ApplySearchAsync(Rows.CurrentSearchQuery.RemoveAt(index)))));
        }

        foreach ((string columnName, HashSet<string> allowedValues) in Rows.ColumnValueFilters)
        {
            ActiveFilterChips.Add(new ActiveFilterChip(
                $"{columnName}: {allowedValues.Count} selected",
                () => RunBusyAsync(() => Rows.SetColumnValueFilterAsync(columnName, null))));
        }

        foreach ((string columnName, ColumnPatternFilter filter) in Rows.ColumnPatternFilters)
        {
            string label = filter.UseRegex ? $"{columnName} ~ /{filter.Pattern}/" : $"{columnName}: \"{filter.Pattern}\"";
            ActiveFilterChips.Add(new ActiveFilterChip(
                label, () => RunBusyAsync(() => Rows.SetColumnPatternFilterAsync(columnName, null, filter.UseRegex))));
        }

        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(HasMultipleSearchTerms));
    }

    /// <summary>True once the search carries more than one term — what makes the match-all/match-any choice meaningful.</summary>
    public bool HasMultipleSearchTerms => Rows.CurrentSearchQuery.Criteria.Count > 1;

    /// <summary>Replaces the whole search with the term currently typed (an empty box clears it).</summary>
    private Task ApplySearchAsync() =>
        RunBusyAsync(() => Rows.ApplySearchAsync(
            string.IsNullOrEmpty(SearchText) ? SearchQuery.Empty : new SearchQuery([BuildCriterion()], CurrentCombineMode)));

    /// <summary>Adds the typed term to the search instead of replacing it, then clears the box ready for the next one.</summary>
    private Task AddSearchTermAsync()
    {
        if (string.IsNullOrEmpty(SearchText)) return Task.CompletedTask;

        SearchQuery query = (Rows.CurrentSearchQuery with { Combine = CurrentCombineMode }).Add(BuildCriterion());
        SearchText = string.Empty;
        return RunBusyAsync(() => Rows.ApplySearchAsync(query));
    }

    private SearchCriterion BuildCriterion() => new(
        SearchText,
        SearchMode.Mode,
        SearchColumn == AllColumnsScope ? null : SearchColumn,
        SearchCaseSensitive);

    private List<long> CollectBaseAndAddedRowIndices()
    {
        var candidates = new List<long>();
        for (nuint i = 0; i < Session.CurrentOrder.Count; i++)
        {
            candidates.Add(Session.CurrentOrder[i].RowIndex);
        }
        candidates.AddRange(Session.Overlay.GetLiveAddedOrDuplicatedRowIndices());
        return candidates;
    }

    private void AddRow()
    {
        Session.Overlay.AddRow(Session.FileIndex.Header.ColumnNames);
        Rows.Invalidate();
    }

    private void DuplicateSelectedRow()
    {
        if (SelectedRow is not { } selected) return;
        Core.Overlay.ResolvedRow? resolved = Session.Resolve(selected.RowIndex);
        if (resolved is null) return; // shouldn't happen for a currently-visible row, but stay defensive
        Session.Overlay.DuplicateRow(resolved.FieldValues);
        Rows.Invalidate();
    }

    /// <summary>Selects every row matching the current search/column filters, across every page — not just the page currently rendered by the grid.</summary>
    public void SelectAllRows() => Selection.SelectAll(Rows.GetAllRowIndices());

    public void ClearAllRowSelection() => Selection.Clear();

    private void OnSelectionChanged()
    {
        // Bulk selection changes (select all / clear all / a checkbox toggle) don't change row
        // membership or order, so a full Invalidate() (which also resets to page 0) would be
        // overkill — RefreshCurrentPage() just forces the currently-visible checkboxes to re-read
        // the new state.
        Rows.RefreshCurrentPage();
        OnPropertyChanged(nameof(SelectedCount));
        CommandManager.InvalidateRequerySuggested();
    }

    private void DeleteSelectedRows()
    {
        Session.Overlay.BulkDelete([.. Selection.SelectedRowIndices]);
        Selection.Clear();
        Rows.Invalidate();
    }

    /// <summary>Deletes one specific record immediately — the per-row trash-can button, independent of checkbox selection/Delete.</summary>
    public void DeleteRow(long rowIndex)
    {
        Session.Overlay.DeleteRow(rowIndex);
        Selection.SetSelected(rowIndex, false);
        Rows.Invalidate();
    }
}

/// <summary>One entry in <see cref="GridViewModel.ActiveFilterChips"/> — a human-readable description of an active filter, plus the command that clears just that one filter.</summary>
public sealed class ActiveFilterChip(string label, Func<Task> remove)
{
    public string Label { get; } = label;
    public ICommand RemoveCommand { get; } = new AsyncRelayCommand(remove);
}
