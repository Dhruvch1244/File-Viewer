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
        var dialog = new OpenFileDialog { Filter = "DIF files (*.dif)|*.dif|All files (*.*)|*.*" };
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

    /// <summary>Selects every currently-visible row (respecting the active sort/filter) so bulk operations like Delete can act on all of them — equivalent to Ctrl+A, offered as a discoverable button.</summary>
    private void OnSelectAllClick(object sender, RoutedEventArgs e) => RowsDataGrid.SelectAll();

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

        RowsDataGrid.FrozenColumnCount = grid.FrozenColumnCount;
        grid.PropertyChanged += (_, args) =>
        {
            switch (args.PropertyName)
            {
                case nameof(GridViewModel.FrozenColumnCount):
                    RowsDataGrid.FrozenColumnCount = grid.FrozenColumnCount;
                    break;
                case nameof(GridViewModel.CurrentSortColumn):
                case nameof(GridViewModel.CurrentSortDirection):
                    UpdateColumnSortIndicators(grid);
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
