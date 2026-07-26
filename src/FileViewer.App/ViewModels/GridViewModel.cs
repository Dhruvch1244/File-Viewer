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
/// commands. Sorting is scoped to the "_ID" column specifically — that's the only column Core's
/// indexer extracts an unmanaged <see cref="Core.Native.SortKey"/> for (PRS §6.3); sorting by an
/// arbitrary column would mean decoding every row up front, defeating the whole point of the
/// unmanaged-index design, so it isn't offered as a fast interactive sort here.
/// </summary>
public sealed class GridViewModel : ObservableObject
{
    private RowViewModel? _selectedRow;
    private IReadOnlyList<RowViewModel> _selectedRows = [];
    private int _frozenColumnCount;
    private string _searchText = string.Empty;

    public GridViewModel(FileViewerSession session)
    {
        Session = session;
        Rows = new VirtualizingRowCollection(session);
        ColumnNames = session.FileIndex.Header.ColumnNames;
        Columns = new ObservableCollection<GridColumnInfo>(
            ColumnNames.Select((name, index) => new GridColumnInfo(name, index)));

        SortByIdAscendingCommand = RelayCommand.Create(() => ApplySort(SortDirection.Ascending));
        SortByIdDescendingCommand = RelayCommand.Create(() => ApplySort(SortDirection.Descending));
        ClearSortCommand = RelayCommand.Create(() => { Session.ClearSort(); Rows.Invalidate(); });

        AddRowCommand = RelayCommand.Create(AddRow);
        DuplicateSelectedRowCommand = RelayCommand.Create(DuplicateSelectedRow, () => SelectedRow is not null);
        DeleteSelectedRowsCommand = RelayCommand.Create(DeleteSelectedRows, () => SelectedRows.Count > 0);
        UndoCommand = RelayCommand.Create(() => { Session.Overlay.Undo(); Rows.Invalidate(); }, () => Session.Overlay.CanUndo);

        ApplySearchCommand = RelayCommand.Create(() => Rows.ApplyFilter(SearchText));
        ClearSearchCommand = RelayCommand.Create(() => { SearchText = string.Empty; Rows.ApplyFilter(null); });
    }

    public FileViewerSession Session { get; }
    public VirtualizingRowCollection Rows { get; }
    public IReadOnlyList<string> ColumnNames { get; }
    public ObservableCollection<GridColumnInfo> Columns { get; }

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

    public ICommand SortByIdAscendingCommand { get; }
    public ICommand SortByIdDescendingCommand { get; }
    public ICommand ClearSortCommand { get; }
    public ICommand AddRowCommand { get; }
    public ICommand DuplicateSelectedRowCommand { get; }
    public ICommand DeleteSelectedRowsCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand ApplySearchCommand { get; }
    public ICommand ClearSearchCommand { get; }

    private void ApplySort(SortDirection direction)
    {
        Session.ApplySort(direction);
        Rows.Invalidate();
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

    private void DeleteSelectedRows()
    {
        Session.Overlay.BulkDelete(SelectedRows.Select(r => r.RowIndex).ToArray());
        Rows.Invalidate();
    }
}
