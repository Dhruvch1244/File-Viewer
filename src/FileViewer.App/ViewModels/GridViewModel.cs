using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Input;
using FileViewer.App.Collections;
using FileViewer.App.Common;
using FileViewer.Core.Filtering;
using FileViewer.Core.Overlay;
using FileViewer.Core.Session;
using FileViewer.Core.Sorting;
using FileViewer.Core.Statistics;

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
    private bool _highlightInsteadOfFilter;
    private int _currentMatchOrdinal = -1;
    private string _columnSearchText = string.Empty;
    private string? _currentSortColumn;
    private SortDirection _currentSortDirection = SortDirection.Ascending;
    private bool _isBusy;

    public GridViewModel(FileViewerSession session, GridPreferences? preferences = null, IReadOnlyList<long>? restrictedRows = null)
    {
        Session = session;
        IsExtractedView = restrictedRows is not null;
        Preferences = preferences ?? new GridPreferences();
        Preferences.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(GridPreferences.PageSize)) ApplyPageSizePreference();
        };
        Selection.Changed += OnSelectionChanged;
        Rows = new VirtualizingRowCollection(session, Selection, restrictedRows);
        Rows.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(PageIndex));
            OnPropertyChanged(nameof(PageCount));
            OnPropertyChanged(nameof(PageLabel));
            OnPropertyChanged(nameof(TotalRowCount));
            OnPropertyChanged(nameof(CanGoToPreviousPage));
            OnPropertyChanged(nameof(CanGoToNextPage));
            OnPropertyChanged(nameof(RowRangeLabel));

            // "All matching" has to keep meaning what matches *now*, not what matched when the user
            // pressed it.
            Selection.UpdateMatchingRowCount(Rows.TotalRowCount);
            OnPropertyChanged(nameof(PageNumberText));
            OnPropertyChanged(nameof(IsPagingActive));
            OnPropertyChanged(nameof(MatchCount));
            OnPropertyChanged(nameof(HasMatches));
            OnPropertyChanged(nameof(HasHighlight));
            OnPropertyChanged(nameof(MatchLabel));
            RefreshHiddenMatchColumns();
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
            () => { SearchText = string.Empty; return RunBusyAsync(() => ApplyQueryAsync(SearchQuery.Empty)); }, () => !IsBusy);
        ClearAllFiltersCommand = new AsyncRelayCommand(
            () => { SearchText = string.Empty; return RunBusyAsync(() => Rows.ClearAllFiltersAsync()); }, () => !IsBusy && HasActiveFilters);
        NextMatchCommand = RelayCommand.Create(() => GoToMatch(1), () => HasMatches);
        PreviousMatchCommand = RelayCommand.Create(() => GoToMatch(-1), () => HasMatches);

        PreviousPageCommand = RelayCommand.Create(() => Rows.GoToPage(Rows.PageIndex - 1), () => CanGoToPreviousPage && !IsBusy);
        NextPageCommand = RelayCommand.Create(() => Rows.GoToPage(Rows.PageIndex + 1), () => CanGoToNextPage && !IsBusy);
        FirstPageCommand = RelayCommand.Create(() => Rows.GoToPage(0), () => CanGoToPreviousPage && !IsBusy);
        LastPageCommand = RelayCommand.Create(() => Rows.GoToPage(Rows.PageCount - 1), () => CanGoToNextPage && !IsBusy);

        ApplyPageSizePreference();
    }

    /// <summary>Row-count and paging settings shared with every other open grid — see <see cref="GridPreferences"/>.</summary>
    public GridPreferences Preferences { get; }

    /// <summary>
    /// True for a view holding a fixed set of rows pulled out of another view. It shares the file's
    /// index, cache and edit overlay with the view it came from — an edit here is an edit to the
    /// same file — but it has its own filters, search, sort and column layout.
    /// </summary>
    public bool IsExtractedView { get; }

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

    /// <summary>
    /// The row the DataGrid has focused. Rows from another tab are rejected: switching tabs
    /// re-points the grid's two-way SelectedItem binding while the outgoing selection is still being
    /// torn down, so without this guard tab B could end up holding a row that resolves against tab
    /// A's session — and "Duplicate" would then copy a row out of the wrong file.
    /// </summary>
    public RowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (value is not null && !value.BelongsTo(Session)) return;
            SetField(ref _selectedRow, value);
        }
    }

    /// <summary>
    /// Column widths and left-to-right order, by column name. The window rebuilds the DataGrid's
    /// columns from scratch every time it switches to a different grid, so anything the user did to
    /// them — dragging a width, reordering — lives here instead of on the (discarded) columns.
    /// </summary>
    public Dictionary<string, ColumnLayout> ColumnLayouts { get; } = new(StringComparer.Ordinal);

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
            bool wasEmpty = string.IsNullOrEmpty(_searchText);
            if (!SetField(ref _searchText, value)) return;

            // Only "+ Add term" depends on this, and only on whether the box is empty — so requery
            // on that transition alone. Doing it per keystroke re-evaluated CanExecute for every
            // command binding in the window (a page of grid rows included), which is what made
            // typing in the search box stutter on a large file.
            if (wasEmpty != string.IsNullOrEmpty(_searchText))
            {
                CommandManager.InvalidateRequerySuggested();
            }
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
            if (!IsBusy && ActiveQuery.Criteria.Count > 1)
            {
                _ = RunBusyAsync(() => ApplyQueryAsync(ActiveQuery with { Combine = CurrentCombineMode }));
            }
        }
    }

    public string SearchCombineLabel => MatchAllSearchTerms ? "Match all" : "Match any";

    /// <summary>
    /// False (the default) hides everything that doesn't match; true keeps every row visible and
    /// marks the matching cells instead. Highlighting is the right mode when the rows around a match
    /// are the context you're reading — filtering them away is exactly what you don't want.
    /// </summary>
    public bool HighlightInsteadOfFilter
    {
        get => _highlightInsteadOfFilter;
        set
        {
            if (!SetField(ref _highlightInsteadOfFilter, value)) return;
            OnPropertyChanged(nameof(SearchModeLabel));

            // Switching modes moves the current terms across rather than dropping them: whichever
            // mode is now off must stop acting on the grid.
            SearchQuery current = value ? Rows.CurrentSearchQuery : Rows.CurrentHighlightQuery;
            _ = RunBusyAsync(async () =>
            {
                if (value)
                {
                    await Rows.ApplySearchAsync(SearchQuery.Empty);
                    await Rows.ApplyHighlightAsync(current);
                }
                else
                {
                    await Rows.ApplyHighlightAsync(SearchQuery.Empty);
                    await Rows.ApplySearchAsync(current);
                }
            });
        }
    }

    public string SearchModeLabel => HighlightInsteadOfFilter ? "Highlight" : "Filter";

    /// <summary>
    /// Columns the highlight query matched in that are currently hidden. Only the first twenty
    /// columns show by default, so without this the grid can report "412 matches" over a screen with
    /// nothing marked on it — the matches being real, just off to the right in a column the user
    /// never chose to show.
    /// </summary>
    public ObservableCollection<string> HiddenMatchColumns { get; } = new();

    public bool HasHiddenMatches => HiddenMatchColumns.Count > 0;

    public string HiddenMatchesLabel => HiddenMatchColumns.Count switch
    {
        0 => string.Empty,
        1 => $"Also matching in hidden column {HiddenMatchColumns[0]}",
        _ => $"Also matching in hidden columns: {string.Join(", ", HiddenMatchColumns)}",
    };

    /// <summary>Shows the hidden columns the matches are in, so they stop being invisible.</summary>
    public void ShowHiddenMatchColumns()
    {
        foreach (string columnName in HiddenMatchColumns)
        {
            foreach (GridColumnInfo column in Columns.Where(c => c.Name == columnName))
            {
                column.IsVisible = true;
            }
        }
        RefreshHiddenMatchColumns();
    }

    /// <summary>
    /// Works out which hidden columns the current highlight matched in, from a bounded sample of the
    /// matching rows — enough to answer "is there something off-screen", without a second full pass
    /// over the result set just to phrase a hint.
    /// </summary>
    private void RefreshHiddenMatchColumns()
    {
        HiddenMatchColumns.Clear();

        CompiledSearchQuery highlight = Rows.CompiledHighlight;
        IReadOnlyList<int> positions = Rows.MatchPositions;
        if (!highlight.IsEmpty && positions.Count > 0)
        {
            var hiddenIndexes = new HashSet<int>(Columns.Where(column => !column.IsVisible).Select(column => column.Index));
            if (hiddenIndexes.Count > 0)
            {
                const int sampleSize = 250;
                var found = new SortedSet<int>();
                for (int i = 0; i < positions.Count && i < sampleSize && found.Count < hiddenIndexes.Count; i++)
                {
                    if (Rows.GetRowIndexAtPosition(positions[i]) is not long rowIndex) continue;

                    Core.Overlay.ResolvedRow? resolved = Session.Resolve(rowIndex);
                    if (resolved is null) continue;

                    foreach (int columnIndex in hiddenIndexes)
                    {
                        if (found.Contains(columnIndex)) continue;
                        if (columnIndex < resolved.FieldValues.Count
                            && highlight.HighlightsCell(columnIndex, resolved.FieldValues[columnIndex]))
                        {
                            found.Add(columnIndex);
                        }
                    }
                }

                foreach (int columnIndex in found)
                {
                    HiddenMatchColumns.Add(ColumnNames[columnIndex]);
                }
            }
        }

        OnPropertyChanged(nameof(HasHiddenMatches));
        OnPropertyChanged(nameof(HiddenMatchesLabel));
    }

    /// <summary>
    /// Raised when match navigation moves to a row, so the view can scroll it into sight. An event
    /// rather than a property change on <see cref="SelectedRow"/>: only navigation should steal the
    /// scroll position, not an ordinary click on a row.
    /// </summary>
    public event Action<RowViewModel>? MatchFocused;

    /// <summary>True while a highlight query is active — what makes the match count and ‹ › navigation appear (including when the answer is "no matches").</summary>
    public bool HasHighlight => Rows.HasHighlight;

    /// <summary>How many rows the highlight query matches — 0 when highlighting is off.</summary>
    public int MatchCount => Rows.MatchPositions.Count;

    public bool HasMatches => MatchCount > 0;

    /// <summary>"3 of 412", or just the total before any match has been stepped to.</summary>
    public string MatchLabel => MatchCount == 0
        ? (Rows.HasHighlight ? "No matches" : string.Empty)
        : _currentMatchOrdinal >= 0
            ? $"{_currentMatchOrdinal + 1:N0} of {MatchCount:N0}"
            : $"{MatchCount:N0} match(es)";

    /// <summary>
    /// Steps to the next (<paramref name="direction"/> = 1) or previous (-1) match, wrapping at
    /// either end, and turns the page so it is on screen. Bound to F3 / Shift+F3 and the ‹ › buttons.
    /// </summary>
    public void GoToMatch(int direction)
    {
        IReadOnlyList<int> positions = Rows.MatchPositions;
        if (positions.Count == 0) return;

        _currentMatchOrdinal = _currentMatchOrdinal < 0
            ? (direction >= 0 ? 0 : positions.Count - 1)
            : ((_currentMatchOrdinal + direction) % positions.Count + positions.Count) % positions.Count;

        int position = positions[_currentMatchOrdinal];
        Rows.GoToPage(position / Rows.PageSize);

        // Turning to the page isn't enough on its own: with several matches on one page, "next"
        // would look like nothing happened. Selecting the row is what actually points at it.
        if (Rows.GetRowAtPosition(position) is { } row)
        {
            SelectedRow = row;
            MatchFocused?.Invoke(row);
        }

        OnPropertyChanged(nameof(MatchLabel));
    }

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

    /// <summary>The rows-per-page choices offered in the pagination bar.</summary>
    public IReadOnlyList<PageSizeOption> PageSizeOptions { get; } = PageSizeOption.All;

    /// <summary>
    /// How many rows to show at once. Shared across grids (see <see cref="Preferences"/>), so the
    /// choice holds for the next file too.
    /// </summary>
    public PageSizeOption SelectedPageSize
    {
        get => Preferences.PageSize;
        set
        {
            if (value is null || value == Preferences.PageSize) return;
            Preferences.PageSize = value; // raises back into ApplyPageSizePreference
        }
    }

    /// <summary>True while the page size is measured from the window, which is what lets MainWindow keep it in step with resizes.</summary>
    public bool IsPageSizeFitToWindow => SelectedPageSize.Kind == PageSizeKind.FitToWindow;

    /// <summary>False when every row is on one page, so the page buttons can step aside rather than sit there disabled and meaningless.</summary>
    public bool IsPagingActive => !Rows.ShowsAllRows;

    /// <summary>"Showing 51–100 of 2,000,000" — what is actually on screen, which a page number alone doesn't say.</summary>
    public string RowRangeLabel => TotalRowCount == 0
        ? "No rows"
        : $"Showing {Rows.FirstRowNumberOnPage:N0}–{Rows.LastRowNumberOnPage:N0} of {TotalRowCount:N0}";

    private void ApplyPageSizePreference()
    {
        OnPropertyChanged(nameof(SelectedPageSize));
        OnPropertyChanged(nameof(IsPageSizeFitToWindow));

        // Fit-to-window is measured by the window, which reapplies it whenever the grid is laid out;
        // the others are exact and can be set straight away.
        if (!IsPageSizeFitToWindow)
        {
            Rows.SetPageSize(SelectedPageSize.RowsPerPage);
        }

        OnPropertyChanged(nameof(IsPagingActive));
    }

    /// <summary>Jumps to a 1-based page number typed into the pagination bar; anything unparseable or out of range is ignored.</summary>
    public void GoToPageNumber(string text)
    {
        if (int.TryParse(text, out int pageNumber))
        {
            Rows.GoToPage(pageNumber - 1);
        }
        OnPropertyChanged(nameof(PageNumberText));
    }

    /// <summary>The current page number, as the jump box shows it.</summary>
    public string PageNumberText => (PageIndex + 1).ToString("N0");

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
    public ICommand NextMatchCommand { get; }
    public ICommand PreviousMatchCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand ClearAllFiltersCommand { get; }
    public ICommand PreviousPageCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand FirstPageCommand { get; }
    public ICommand LastPageCommand { get; }

    /// <summary>Invoked from the DataGrid's Sorting event (column header click). Toggles ascending/descending on repeated clicks of the same column. No-ops while <see cref="IsBusy"/> — the caller (MainWindow) doesn't need its own guard.</summary>
    public async Task SortByColumnAsync(string columnName, SortDirection? requestedDirection = null)
    {
        if (IsBusy) return;

        // No direction asked for means "toggle" — what a header click does. The column menu asks for
        // one explicitly.
        SortDirection direction = requestedDirection
            ?? (columnName == CurrentSortColumn && CurrentSortDirection == SortDirection.Ascending
                ? SortDirection.Descending
                : SortDirection.Ascending);

        // The "_ID" fast path permutes the session's own order, which an extracted view shares with
        // the view it came from — sorting here would reorder that one too. Extracted views are a
        // hand-picked set of rows, so the general path costs little and keeps them independent.
        if (columnName == "_ID" && !IsExtractedView)
        {
            Rows.ClearCustomOrder();
            Session.ApplySort(direction);
        }
        else
        {
            await RunBusyAsync(async () =>
            {
                if (!IsExtractedView) Session.ClearSort();
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
        if (!IsExtractedView) Session.ClearSort();
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

    /// <summary>
    /// Rows past this are not copied. The clipboard is a single string held twice over (here and in
    /// the system buffer), so an unbounded copy of a multi-million-row result is a way to run the
    /// machine out of memory rather than a feature.
    /// </summary>
    public const int MaxClipboardRows = 100_000;

    /// <summary>
    /// The current selection as tab-separated text — one header line, then a line per row, covering
    /// the visible columns in their current order. Tab-separated so it pastes straight into Excel.
    /// Falls back to the focused row when nothing is ticked, because "copy" with no selection
    /// obviously means the row you are looking at.
    ///
    /// Built on a background thread (it resolves rows) and handed back as a string: putting it on
    /// the clipboard is the window's job, since that has to happen on the UI thread.
    /// </summary>
    public Task<ClipboardPayload> BuildClipboardTextAsync() => BuildClipboardTextAsync(ClipboardOptions.Default);

    /// <inheritdoc cref="BuildClipboardTextAsync()"/>
    /// <param name="options">Which rows to take, in which format, with or without a header line.</param>
    public async Task<ClipboardPayload> BuildClipboardTextAsync(ClipboardOptions options)
    {
        int[] columnIndexes = [.. Columns.Where(column => column.IsVisible).Select(column => column.Index)];
        if (columnIndexes.Length == 0) return new ClipboardPayload(string.Empty, 0, false);

        IReadOnlyList<long> rowsInView = Rows.GetAllRowIndices();
        List<long> rows = options.Scope == ClipboardScope.AllRowsInView
            ? [.. rowsInView]
            : Selection.Count > 0
                ? [.. Selection.Resolve(rowsInView)]
                : SelectedRow is { } focused ? [focused.RowIndex] : [];

        if (rows.Count == 0) return new ClipboardPayload(string.Empty, 0, false);

        bool truncated = rows.Count > MaxClipboardRows;
        if (truncated) rows = rows[..MaxClipboardRows];

        FileViewerSession session = Session;
        IReadOnlyList<string> columnNames = ColumnNames;
        bool csv = options.Format == ClipboardFormat.Csv;
        char separator = csv ? ',' : '\t';

        return await Task.Run(() =>
        {
            var builder = new StringBuilder();

            if (options.IncludeHeaders)
            {
                for (int i = 0; i < columnIndexes.Length; i++)
                {
                    if (i > 0) builder.Append(separator);
                    builder.Append(Encode(columnNames[columnIndexes[i]], csv));
                }
                builder.Append('\n');
            }

            foreach (long rowIndex in rows)
            {
                Core.Overlay.ResolvedRow? resolved = session.Resolve(rowIndex);
                if (resolved is null) continue; // deleted while we were building

                for (int i = 0; i < columnIndexes.Length; i++)
                {
                    if (i > 0) builder.Append(separator);
                    int columnIndex = columnIndexes[i];
                    builder.Append(Encode(
                        columnIndex < resolved.FieldValues.Count ? resolved.FieldValues[columnIndex] : string.Empty,
                        csv));
                }
                builder.Append('\n');
            }

            return new ClipboardPayload(builder.ToString(), rows.Count, truncated);
        });
    }

    /// <summary>
    /// CSV quotes a value that contains the delimiter, a quote or a newline, doubling embedded
    /// quotes — the same rule <c>CsvExporter</c> writes files by, so a pasted copy and an exported
    /// file agree. Tab-separated text has no quoting convention a spreadsheet reads back, so there
    /// the separators inside a value collapse to spaces instead, as the TSV exporter does.
    /// </summary>
    private static string Encode(string value, bool csv)
    {
        if (csv)
        {
            return value.IndexOfAny([',', '"', '\r', '\n']) < 0
                ? value
                : "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        return value.IndexOfAny(['\t', '\r', '\n']) < 0
            ? value
            : value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }

    /// <summary>Stops a filter pass that is still running — see <see cref="VirtualizingRowCollection"/>.</summary>
    public void CancelFilter() => Rows.CancelPendingRecompute();

    /// <summary>Summarizes a column over the rows in view — the column menu's Stats view. Returns an empty summary (rather than running) while <see cref="IsBusy"/>.</summary>
    public Task<ColumnStatistics> GetColumnStatisticsAsync(string columnName) =>
        IsBusy
            ? Task.FromResult(ColumnStatistics.Empty(columnName))
            : RunBusyAsync(() => Rows.GetColumnStatisticsAsync(columnName));

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
    public bool HasMultipleSearchTerms => ActiveQuery.Criteria.Count > 1;

    /// <summary>Replaces the whole search with the term currently typed (an empty box clears it), in whichever mode is active.</summary>
    private Task ApplySearchAsync()
    {
        SearchQuery query = string.IsNullOrEmpty(SearchText)
            ? SearchQuery.Empty
            : new SearchQuery([BuildCriterion()], CurrentCombineMode);
        return RunBusyAsync(() => ApplyQueryAsync(query));
    }

    /// <summary>Adds the typed term to the search instead of replacing it, then clears the box ready for the next one.</summary>
    private Task AddSearchTermAsync()
    {
        if (string.IsNullOrEmpty(SearchText)) return Task.CompletedTask;

        SearchQuery query = (ActiveQuery with { Combine = CurrentCombineMode }).Add(BuildCriterion());
        SearchText = string.Empty;
        return RunBusyAsync(() => ApplyQueryAsync(query));
    }

    /// <summary>The terms currently in force, from whichever of the two modes is active.</summary>
    private SearchQuery ActiveQuery => HighlightInsteadOfFilter ? Rows.CurrentHighlightQuery : Rows.CurrentSearchQuery;

    private Task ApplyQueryAsync(SearchQuery query)
    {
        _currentMatchOrdinal = -1;
        return HighlightInsteadOfFilter ? Rows.ApplyHighlightAsync(query) : Rows.ApplySearchAsync(query);
    }

    private SearchCriterion BuildCriterion() => new(
        SearchText,
        SearchMode.Mode,
        SearchColumn == AllColumnsScope ? null : SearchColumn,
        SearchCaseSensitive);

    /// <summary>
    /// The rows a sort should order: whatever this view starts from. Taken from the row collection
    /// rather than rebuilt from the session, so an extracted view sorts its own rows instead of
    /// silently pulling in the whole file.
    /// </summary>
    private List<long> CollectBaseAndAddedRowIndices() => [.. Rows.GetBaseRowOrder()];

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

    /// <summary>
    /// Selects every row matching the current filters — across every page, not just what is
    /// rendered. Recorded as a mode rather than a list, so this costs nothing even when "every row"
    /// means millions (see <see cref="RowSelectionState"/>).
    /// </summary>
    public void SelectAllRows() => Selection.SelectAllMatching(Rows.TotalRowCount);

    public void ClearAllRowSelection() => Selection.Clear();

    private void OnSelectionChanged()
    {
        // Selection changes don't affect row membership or order, so nothing about the collection
        // needs rebuilding — the visible checkboxes just need to re-read their state.
        Rows.RefreshSelectionVisuals();
        OnPropertyChanged(nameof(SelectedCount));
        CommandManager.InvalidateRequerySuggested();
    }

    private void DeleteSelectedRows()
    {
        // Materialized here, against the rows currently in view — in select-all mode the selection
        // is a rule rather than a list until something acts on it.
        Session.Overlay.BulkDelete([.. Selection.Resolve(Rows.GetAllRowIndices())]);
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

/// <summary>Text destined for the clipboard, with what had to be left out of it.</summary>
/// <param name="Text">Tab-separated rows, header first.</param>
/// <param name="RowCount">How many rows it covers.</param>
/// <param name="Truncated">True if the selection was larger than <see cref="GridViewModel.MaxClipboardRows"/>.</param>
public readonly record struct ClipboardPayload(string Text, int RowCount, bool Truncated);

/// <summary>Which rows a copy takes.</summary>
public enum ClipboardScope
{
    /// <summary>The ticked rows, or the focused row when nothing is ticked.</summary>
    SelectedRows,

    /// <summary>Every row the current filters leave, ticked or not — across pages, not just this one.</summary>
    AllRowsInView,
}

/// <summary>Which text format a copy produces.</summary>
public enum ClipboardFormat
{
    /// <summary>Tab-separated: what a spreadsheet pastes into cells without an import step.</summary>
    TabSeparated,

    /// <summary>Comma-separated with the usual quoting, for pasting into a text file or a tool that wants CSV.</summary>
    Csv,
}

/// <summary>
/// One entry of the Copy dropdown. The plain Copy button is <see cref="Default"/>; the rest exist
/// because "copy" means something different depending on where it is going — a spreadsheet wants
/// headers and tabs, a diff or a config file usually wants neither.
/// </summary>
public readonly record struct ClipboardOptions(
    ClipboardScope Scope = ClipboardScope.SelectedRows,
    ClipboardFormat Format = ClipboardFormat.TabSeparated,
    bool IncludeHeaders = true)
{
    public static readonly ClipboardOptions Default = new();
}

/// <summary>A column's remembered on-screen size and position — see <see cref="GridViewModel.ColumnLayouts"/>.</summary>
/// <param name="Width">Rendered width in pixels, or 0 if it was never measured.</param>
/// <param name="DisplayIndex">Position among the visible columns, or -1 if unknown.</param>
public readonly record struct ColumnLayout(double Width, int DisplayIndex);

/// <summary>One entry in <see cref="GridViewModel.ActiveFilterChips"/> — a human-readable description of an active filter, plus the command that clears just that one filter.</summary>
public sealed class ActiveFilterChip(string label, Func<Task> remove)
{
    public string Label { get; } = label;
    public ICommand RemoveCommand { get; } = new AsyncRelayCommand(remove);
}
