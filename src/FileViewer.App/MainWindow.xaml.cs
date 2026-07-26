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

    private void RebuildColumns()
    {
        RowsDataGrid.Columns.Clear();
        if (_viewModel.Grid is not { } grid) return;

        for (int i = 0; i < grid.ColumnNames.Count; i++)
        {
            var column = new DataGridTextColumn
            {
                Header = grid.ColumnNames[i],
                Binding = new Binding($"[{i}]") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus },
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
            if (args.PropertyName == nameof(GridViewModel.FrozenColumnCount))
            {
                RowsDataGrid.FrozenColumnCount = grid.FrozenColumnCount;
            }
        };
    }
}
