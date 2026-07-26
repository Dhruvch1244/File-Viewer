using System.Collections.ObjectModel;
using System.Windows.Input;
using FileViewer.App.Collections;
using FileViewer.App.Common;
using FileViewer.Core.Overlay;
using FileViewer.Core.Session;
using FileViewer.Core.Sorting;

namespace FileViewer.App.ViewModels;

/// <summary>
/// Owns the grid's data source, column list, current selection, and the row-operation/sort
/// commands. Clicking the "_ID" column header uses Core's fast unmanaged-key sort
/// (<see cref="FileViewerSession.ApplySort"/>); clicking any other column header falls back to
/// <see cref="RowSorter.SortByColumn"/>, which decodes every candidate row — the same acknowledged
/// slower path as search/filter (PRS §8), since there's no pre-extracted sort key for anything but
/// "_ID".
/// </summary>
public sealed class GridViewModel : ObservableObject
{
    /// <summary>A file can have hundreds of columns; showing them all at once is unusable, so only the first this-many are visible by default — the rest are still reachable via the column chooser.</summary>
    public const int DefaultVisibleColumnCount = 20;

    private RowViewModel? _selectedRow;
    private IReadOnlyList<RowViewModel> _selectedRows = [];
    private int _frozenColumnCount;
    private string _searchText = string.Empty;
    private string _columnSearchText = string.Empty;
    private string? _currentSortColumn;
    private SortDirection _currentSortDirection = SortDirection.Ascending;

    public GridViewModel(FileViewerSession session)
    {
        Session = session;
        Rows = new VirtualizingRowCollection(session);
        Rows.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(PageIndex));
            OnPropertyChanged(nameof(PageCount));
            OnPropertyChanged(nameof(PageLabel));
            OnPropertyChanged(nameof(TotalRowCount));
            OnPropertyChanged(nameof(CanGoToPreviousPage));
            OnPropertyChanged(nameof(CanGoToNextPage));
        };
        ColumnNames = session.FileIndex.Header.ColumnNames;
        Columns = new ObservableCollection<GridColumnInfo>(
            ColumnNames.Select((name, index) => new GridColumnInfo(name, index) { IsVisible = index < DefaultVisibleColumnCount }));

        ClearSortCommand = RelayCommand.Create(ClearSort, () => CurrentSortColumn is not null);
        AddRowCommand = RelayCommand.Create(AddRow);
        DuplicateSelectedRowCommand = RelayCommand.Create(DuplicateSelectedRow, () => SelectedRow is not null);
        DeleteSelectedRowsCommand = RelayCommand.Create(DeleteSelectedRows, () => SelectedRows.Count > 0);
        UndoCommand = RelayCommand.Create(() => { Session.Overlay.Undo(); Rows.Invalidate(); }, () => Session.Overlay.CanUndo);

        ApplySearchCommand = RelayCommand.Create(() => Rows.ApplyFilter(SearchText));
        ClearSearchCommand = RelayCommand.Create(() => { SearchText = string.Empty; Rows.ApplyFilter(null); });

        PreviousPageCommand = RelayCommand.Create(() => Rows.GoToPage(Rows.PageIndex - 1), () => CanGoToPreviousPage);
        NextPageCommand = RelayCommand.Create(() => Rows.GoToPage(Rows.PageIndex + 1), () => CanGoToNextPage);
    }

    public FileViewerSession Session { get; }
    public VirtualizingRowCollection Rows { get; }
    public IReadOnlyList<string> ColumnNames { get; }
    public ObservableCollection<GridColumnInfo> Columns { get; }

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

    /// <summary>Populated from code-behind on DataGrid.SelectionChanged (DataGrid.SelectedItems isn't a bindable DependencyProperty).</summary>
    public IReadOnlyList<RowViewModel> SelectedRows
    {
        get => _selectedRows;
        set => SetField(ref _selectedRows, value);
    }

    /// <summary>Substring to search for across all columns. Applied on <see cref="ApplySearchCommand"/> (not per-keystroke — an arbitrary-column filter decodes every candidate row, so it's a deliberate action, not a live-as-you-type one).</summary>
    public string SearchText
    {
        get => _searchText;
        set => SetField(ref _searchText, value);
    }

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
    public ICommand ClearSearchCommand { get; }
    public ICommand PreviousPageCommand { get; }
    public ICommand NextPageCommand { get; }

    /// <summary>Invoked from the DataGrid's Sorting event (column header click). Toggles ascending/descending on repeated clicks of the same column.</summary>
    public void SortByColumn(string columnName)
    {
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
            Session.ClearSort();
            List<long> candidates = CollectBaseAndAddedRowIndices();
            List<long> sorted = RowSorter.SortByColumn(candidates, columnName, direction, Session.FileIndex, Session.Overlay, Session.Cache);
            Rows.ApplyCustomOrder([.. sorted]);
        }

        CurrentSortColumn = columnName;
        CurrentSortDirection = direction;
        Rows.Invalidate();
    }

    public void ClearSort()
    {
        Session.ClearSort();
        Rows.ClearCustomOrder();
        CurrentSortColumn = null;
        Rows.Invalidate();
    }

    private List<long> CollectBaseAndAddedRowIndices()
    {
        var candidates = new List<long>();
        for (nuint i = 0; i < Session.CurrentOrder.Count; i++)
        {
            candidates.Add(Session.CurrentOrder[i].RowIndex);
        }
        foreach (RowOp op in Session.Overlay.RowOps)
        {
            if ((op.Type is RowOpType.Add or RowOpType.Duplicate) && Session.Overlay.GetRowState(op.RowIndex) != RowState.Deleted)
            {
                candidates.Add(op.RowIndex);
            }
        }
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
    public void SelectAllRows()
    {
        SelectedRows = [.. Rows.GetAllRowIndices().Select(index => new RowViewModel(Session, index))];
    }

    public void ClearAllRowSelection() => SelectedRows = [];

    private void DeleteSelectedRows()
    {
        Session.Overlay.BulkDelete(SelectedRows.Select(r => r.RowIndex).ToArray());
        Rows.Invalidate();
    }
}
