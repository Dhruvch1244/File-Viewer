using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FileViewer.App.ViewModels;
using FileViewer.App.Views;
using Microsoft.Win32;

namespace FileViewer.App;

public partial class MainWindow : Window
{
    /// <summary>Number of always-present, non-data columns (select checkbox, view button) prepended to every dynamically-built column set.</summary>
    private const int FixedColumnCount = 2;

    private readonly MainViewModel _viewModel = new();
    private string? _activeFilterColumn;
    private List<ColumnFilterValueOption> _activeFilterOptions = [];
    private ICollectionView? _activeFilterOptionsView;

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

    /// <summary>Fades and drops the popup's content in on every open — Popup reuses its visual tree across opens, so a Loaded-based trigger would only ever fire once; Opened fires every time.</summary>
    private void OnPopupOpened(object sender, EventArgs e)
    {
        if (sender is not Popup { Child: UIElement child }) return;

        child.Opacity = 0;
        var transform = new TranslateTransform(0, -6);
        child.RenderTransform = transform;

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140));
        var slide = new DoubleAnimation(-6, 0, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        child.BeginAnimation(UIElement.OpacityProperty, fade);
        transform.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private void OnSelectAllColumnsClick(object sender, RoutedEventArgs e) => SetVisibilityForFilteredColumns(isVisible: true);

    private void OnHideAllColumnsClick(object sender, RoutedEventArgs e) => SetVisibilityForFilteredColumns(isVisible: false);

    /// <summary>Applies to whatever the column-search box currently shows (everything, if it's empty) — so searching "PRICE" then hitting "Hide all" only hides matching columns.</summary>
    private void SetVisibilityForFilteredColumns(bool isVisible)
    {
        if (_viewModel.Grid is not { } grid) return;
        foreach (object item in CollectionViewSource.GetDefaultView(grid.Columns))
        {
            if (item is GridColumnInfo info)
            {
                info.IsVisible = isVisible;
            }
        }
    }

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

    /// <summary>
    /// Header checkbox: checks every row box on the current page for visual feedback, then widens
    /// the actual selection to every row matching the active filters across every page — so
    /// Delete/bulk actions act on the full result set, not just the ~20 rows the grid renders at
    /// once. SelectAll() fires SelectionChanged synchronously (narrowing Grid.SelectedRows back to
    /// just this page), so SelectAllRows() must run after it to have the final say.
    /// </summary>
    private void OnSelectAllHeaderChecked(object sender, RoutedEventArgs e)
    {
        RowsDataGrid.SelectAll();
        _viewModel.Grid?.SelectAllRows();
    }

    private void OnSelectAllHeaderUnchecked(object sender, RoutedEventArgs e)
    {
        RowsDataGrid.UnselectAll();
        _viewModel.Grid?.ClearAllRowSelection();
    }

    /// <summary>
    /// See the comment on SelectCheckBoxCellTemplate in MainWindow.xaml: DataGrid's default
    /// click-to-select handling fires on the same mouse event as this checkbox's own click and
    /// would otherwise replace the whole selection with just the clicked row. Toggling manually
    /// and marking the event handled here lets multiple rows accumulate in the selection.
    /// </summary>
    private void OnRowCheckBoxPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is CheckBox checkBox)
        {
            checkBox.IsChecked = !(checkBox.IsChecked ?? false);
        }
        e.Handled = true;
    }

    /// <summary>Per-row "View" button — opens a dialog listing every column/value pair for that record.</summary>
    private void OnViewRecordClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RowViewModel row } || _viewModel.Grid is not { } grid) return;

        var dialog = new RowDetailView(row) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            // The dialog edited a different RowViewModel instance than whatever the grid currently
            // has rendered for this row (each cell access creates its own), so the grid's own
            // instance won't raise PropertyChanged for the change on its own — force a re-query.
            grid.Rows.Invalidate();
        }
    }

    /// <summary>
    /// A single click on a column header opens its value-filter popup; a double-click sorts by it
    /// instead. Intercepting on Preview (mouse-down, before the header's own click/Sorting
    /// machinery runs) and marking the event handled suppresses WPF's built-in header-click sort
    /// entirely, so single-click never fires it.
    /// </summary>
    private void OnDataGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject) is not { } header) return;
        if (header.Column?.SortMemberPath is not { Length: > 0 } columnName) return;
        if (_viewModel.Grid is not { } grid) return;

        e.Handled = true;

        if (e.ClickCount >= 2)
        {
            grid.SortByColumn(columnName);
        }
        else
        {
            RequestOpenColumnFilterPopup(columnName, header, grid);
        }
    }

    private void RebuildColumns()
    {
        RowsDataGrid.Columns.Clear();
        if (_viewModel.Grid is not { } grid) return;

        RowsDataGrid.Columns.Add(new DataGridTemplateColumn
        {
            HeaderTemplate = (DataTemplate)FindResource("SelectAllHeaderTemplate"),
            HeaderStyle = (Style)FindResource("CenteredHeaderStyle"),
            CellTemplate = (DataTemplate)FindResource("SelectCheckBoxCellTemplate"),
            CellStyle = (Style)FindResource("CenteredCellStyle"),
            Width = 40,
            CanUserResize = false,
            CanUserSort = false,
            CanUserReorder = false,
        });
        RowsDataGrid.Columns.Add(new DataGridTemplateColumn
        {
            Header = string.Empty,
            CellTemplate = (DataTemplate)FindResource("ViewButtonCellTemplate"),
            CellStyle = (Style)FindResource("CenteredCellStyle"),
            Width = 64,
            CanUserResize = false,
            CanUserSort = false,
            CanUserReorder = false,
        });

        for (int i = 0; i < grid.ColumnNames.Count; i++)
        {
            string columnName = grid.ColumnNames[i];
            GridColumnInfo definition = grid.Columns[i];
            var column = new DataGridTextColumn
            {
                Header = HeaderTextFor(columnName, isFiltered: false),
                SortMemberPath = columnName,
                HeaderStyle = (Style)FindResource("DataColumnHeaderStyle"),
                Binding = new Binding($"[{i}]") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus },
                EditingElementStyle = (Style)FindResource("CellEditTextBoxStyle"),
                // Bug fix: this must be set here, at creation, not only inside the PropertyChanged
                // handler below — otherwise every column starts Visible regardless of
                // GridColumnInfo.IsVisible's initial value (e.g. the "only first 20 by default" rule).
                Visibility = definition.IsVisible ? Visibility.Visible : Visibility.Collapsed,
            };
            RowsDataGrid.Columns.Add(column);

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

    private static string HeaderTextFor(string columnName, bool isFiltered) =>
        isFiltered ? $"{columnName.ToUpperInvariant()}  ●" : columnName.ToUpperInvariant();

    // ============================== Column value filter (Excel-style) ==============================

    /// <summary>Right-click anywhere in a column header also opens its value-filter popup — kept as a secondary path alongside the primary single-click gesture.</summary>
    private void OnDataGridPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject) is not { } header) return;
        if (header.Column?.SortMemberPath is not { Length: > 0 } columnName) return;
        if (_viewModel.Grid is not { } grid) return;

        e.Handled = true;
        RequestOpenColumnFilterPopup(columnName, header, grid);
    }

    /// <summary>
    /// Opening the Popup synchronously inside the same mouse-down that triggers it collides with
    /// the column header's own mouse capture for its pressed/click visual state — the header only
    /// releases capture once this mouse-down finishes bubbling, and the Popup (StaysOpen="False")
    /// reads that capture loss as "clicked outside" and closes itself an instant after opening.
    /// Deferring to a low dispatcher priority runs this after the current input cycle (mouse-down
    /// and its capture handling) has fully settled, so the popup actually stays open.
    /// </summary>
    private void RequestOpenColumnFilterPopup(string columnName, UIElement placementTarget, GridViewModel grid) =>
        Dispatcher.BeginInvoke(() => OpenColumnFilterPopup(columnName, placementTarget, grid), DispatcherPriority.ContextIdle);

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void OpenColumnFilterPopup(string columnName, UIElement placementTarget, GridViewModel grid)
    {
        _activeFilterColumn = columnName;
        ColumnFilterTitle.Text = $"FILTER — {columnName.ToUpperInvariant()}";
        ColumnFilterSearchBox.Text = string.Empty;

        List<string> distinctValues = grid.Rows.GetDistinctValuesForColumn(columnName);
        HashSet<string>? currentSelection = grid.Rows.GetColumnValueFilter(columnName);

        _activeFilterOptions = [.. distinctValues.Select(v => new ColumnFilterValueOption(v, currentSelection is null || currentSelection.Contains(v)))];

        ICollectionView view = CollectionViewSource.GetDefaultView(_activeFilterOptions);
        _activeFilterOptionsView = view;
        ColumnFilterValuesList.ItemsSource = view;

        ColumnFilterPopup.PlacementTarget = placementTarget;
        ColumnFilterPopup.IsOpen = true;
    }

    private void OnColumnFilterSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_activeFilterOptionsView is null) return;
        string text = ColumnFilterSearchBox.Text;
        _activeFilterOptionsView.Filter = o => o is ColumnFilterValueOption option
            && (string.IsNullOrEmpty(text) || option.DisplayText.Contains(text, StringComparison.OrdinalIgnoreCase));
        _activeFilterOptionsView.Refresh();
    }

    private void OnColumnFilterSelectAllClick(object sender, RoutedEventArgs e)
    {
        if (_activeFilterOptionsView is null) return;
        foreach (object item in _activeFilterOptionsView)
        {
            if (item is ColumnFilterValueOption option) option.IsSelected = true;
        }
    }

    private void OnColumnFilterClearClick(object sender, RoutedEventArgs e)
    {
        if (_activeFilterOptionsView is null) return;
        foreach (object item in _activeFilterOptionsView)
        {
            if (item is ColumnFilterValueOption option) option.IsSelected = false;
        }
    }

    private void OnColumnFilterApplyClick(object sender, RoutedEventArgs e)
    {
        if (_activeFilterColumn is not { } columnName || _viewModel.Grid is not { } grid) return;

        var selected = _activeFilterOptions.Where(o => o.IsSelected).Select(o => o.Value).ToHashSet();
        // Everything selected is equivalent to "no filter" — clear it rather than storing a
        // full-set filter, so newly-added rows/values aren't silently excluded later.
        bool isEffectivelyUnfiltered = selected.Count == _activeFilterOptions.Count;
        grid.Rows.SetColumnValueFilter(columnName, isEffectivelyUnfiltered ? null : selected);

        UpdateColumnHeaderText(columnName, isFiltered: !isEffectivelyUnfiltered);
        ColumnFilterPopup.IsOpen = false;
    }

    private void OnColumnFilterCancelClick(object sender, RoutedEventArgs e) => ColumnFilterPopup.IsOpen = false;

    private void UpdateColumnHeaderText(string columnName, bool isFiltered)
    {
        foreach (DataGridColumn column in RowsDataGrid.Columns)
        {
            if (column.SortMemberPath == columnName)
            {
                column.Header = HeaderTextFor(columnName, isFiltered);
                break;
            }
        }
    }
}

/// <summary>One selectable value in a column's Excel-style filter popup.</summary>
public sealed class ColumnFilterValueOption(string value, bool isSelected) : INotifyPropertyChanged
{
    private bool _isSelected = isSelected;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Value { get; } = value;
    public string DisplayText => Value.Length == 0 ? "(Blank)" : Value;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
}
