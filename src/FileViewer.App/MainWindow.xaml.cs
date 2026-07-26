using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using FileViewer.App.ViewModels;
using FileViewer.App.Views;
using Microsoft.Win32;

namespace FileViewer.App;

public partial class MainWindow : Window
{
    /// <summary>Number of always-present, non-data columns (select checkbox, view button) prepended to every dynamically-built column set.</summary>
    private const int FixedColumnCount = 2;

    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Grid))
        {
            RebuildColumns();
        }
    }

    private async void OnOpenFileClick(object sender, RoutedEventArgs e)
    {
        // Any file is accepted — the app itself decides whether the content is a valid DIF file
        // and fails gracefully (with diagnostics) if not, so the dialog shouldn't gatekeep by extension.
        var dialog = new OpenFileDialog { Filter = "All files (*.*)|*.*|DIF files (*.dif)|*.dif", FilterIndex = 1 };
        if (dialog.ShowDialog() != true) return;

        await _viewModel.OpenFileAsync(dialog.FileName);
    }

    private void OnColumnsButtonClick(object sender, RoutedEventArgs e) => ColumnsPopup.IsOpen = !ColumnsPopup.IsOpen;

    private void OnExportButtonClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Grid is not { } grid) return;

        var dialog = new ExportDialogView(grid.Session) { Owner = this };
        dialog.ShowDialog();
    }

    private void OnSearchTextBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _viewModel.Grid is not { } grid) return;
        if (grid.ApplySearchCommand.CanExecute(null))
        {
            grid.ApplySearchCommand.Execute(null);
        }
    }

    private void OnGridSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_viewModel.Grid is not { } grid) return;
        grid.SelectedRows = [.. RowsDataGrid.SelectedItems.Cast<RowViewModel>()];
    }

    /// <summary>Header checkbox: selects/clears every row on the current page (bulk operations like Delete then act on the whole page).</summary>
    private void OnSelectAllHeaderChecked(object sender, RoutedEventArgs e) => RowsDataGrid.SelectAll();

    private void OnSelectAllHeaderUnchecked(object sender, RoutedEventArgs e) => RowsDataGrid.UnselectAll();

    /// <summary>Per-row "View" button — opens a dialog listing every column/value pair for that record.</summary>
    private void OnViewRecordClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RowViewModel row }) return;
        var dialog = new RowDetailView(row) { Owner = this };
        dialog.ShowDialog();
    }

    /// <summary>
    /// The grid's ItemsSource is a plain <see cref="Collections.VirtualizingRowCollection"/>, not
    /// an <see cref="System.ComponentModel.ICollectionView"/> — WPF's built-in header-click sort
    /// has nothing to act on, so we take over entirely and drive it through
    /// <see cref="GridViewModel.SortByColumn"/> instead.
    /// </summary>
    private void OnDataGridSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (_viewModel.Grid is not { } grid || string.IsNullOrEmpty(e.Column.SortMemberPath)) return;

        grid.SortByColumn(e.Column.SortMemberPath);
    }

    private void RebuildColumns()
    {
        RowsDataGrid.Columns.Clear();
        if (_viewModel.Grid is not { } grid) return;

        RowsDataGrid.Columns.Add(new DataGridTemplateColumn
        {
            HeaderTemplate = (DataTemplate)FindResource("SelectAllHeaderTemplate"),
            CellTemplate = (DataTemplate)FindResource("SelectCheckBoxCellTemplate"),
            Width = 36,
            CanUserResize = false,
            CanUserSort = false,
            CanUserReorder = false,
        });
        RowsDataGrid.Columns.Add(new DataGridTemplateColumn
        {
            Header = string.Empty,
            CellTemplate = (DataTemplate)FindResource("ViewButtonCellTemplate"),
            Width = 68,
            CanUserResize = false,
            CanUserSort = false,
            CanUserReorder = false,
        });

        for (int i = 0; i < grid.ColumnNames.Count; i++)
        {
            string columnName = grid.ColumnNames[i];
            var column = new DataGridTextColumn
            {
                Header = columnName.ToUpperInvariant(),
                SortMemberPath = columnName,
                Binding = new Binding($"[{i}]") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus },
                EditingElementStyle = (Style)FindResource("CellEditTextBoxStyle"),
            };
            RowsDataGrid.Columns.Add(column);

            GridColumnInfo definition = grid.Columns[i];
            definition.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(GridColumnInfo.IsVisible))
                {
                    column.Visibility = definition.IsVisible ? Visibility.Visible : Visibility.Collapsed;
                }
            };
        }

        // The select-checkbox and view-button columns are always frozen in addition to however
        // many data columns the user asks to freeze — you always want them visible.
        RowsDataGrid.FrozenColumnCount = FixedColumnCount + grid.FrozenColumnCount;

        // Live filter over the column-chooser popup's name list, driven by GridViewModel.ColumnSearchText.
        ICollectionView columnsView = CollectionViewSource.GetDefaultView(grid.Columns);
        columnsView.Filter = o => o is GridColumnInfo info
            && (string.IsNullOrEmpty(grid.ColumnSearchText) || info.Name.Contains(grid.ColumnSearchText, StringComparison.OrdinalIgnoreCase));

        grid.PropertyChanged += (_, args) =>
        {
            switch (args.PropertyName)
            {
                case nameof(GridViewModel.FrozenColumnCount):
                    RowsDataGrid.FrozenColumnCount = FixedColumnCount + grid.FrozenColumnCount;
                    break;
                case nameof(GridViewModel.CurrentSortColumn):
                case nameof(GridViewModel.CurrentSortDirection):
                    UpdateColumnSortIndicators(grid);
                    break;
                case nameof(GridViewModel.ColumnSearchText):
                    columnsView.Refresh();
                    break;
            }
        };
    }

    private void UpdateColumnSortIndicators(GridViewModel grid)
    {
        foreach (DataGridColumn column in RowsDataGrid.Columns)
        {
            string? columnName = column.SortMemberPath;
            if (string.IsNullOrEmpty(columnName))
            {
                column.SortDirection = null;
                continue;
            }

            column.SortDirection = columnName == grid.CurrentSortColumn
                ? grid.CurrentSortDirection == Core.Sorting.SortDirection.Ascending
                    ? ListSortDirection.Ascending
                    : ListSortDirection.Descending
                : null;
        }
    }
}
