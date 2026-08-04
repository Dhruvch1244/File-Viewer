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
using FileViewer.App.Collections;
using FileViewer.App.ViewModels;
using FileViewer.App.Views;
using FileViewer.Core.Filtering;
using Microsoft.Win32;

namespace FileViewer.App;

public partial class MainWindow : Window
{
    /// <summary>Number of always-present, non-data columns (select checkbox, view button, delete button) prepended to every dynamically-built column set.</summary>
    private const int FixedColumnCount = 3;

    /// <summary>How long a per-column filter box waits after the last keystroke before actually applying the pattern — avoids kicking off a background decode-and-filter pass on every single character typed.</summary>
    private static readonly TimeSpan FilterRowDebounce = TimeSpan.FromMilliseconds(300);

    private readonly MainViewModel _viewModel = new();
    private string? _activeFilterColumn;
    private List<ColumnFilterValueOption> _activeFilterOptions = [];
    private ICollectionView? _activeFilterOptionsView;

    /// <summary>The header TextBlock for each data column — <see cref="UpdateColumnHeaderText"/> updates these directly now that <c>DataGridColumn.Header</c> is a StackPanel (text + filter box), not a plain string.</summary>
    private readonly Dictionary<string, TextBlock> _columnHeaderTextBlocks = new();

    /// <summary>The ag-Grid-style per-column filter TextBox for each data column, keyed by column name — used to sync a box's displayed text back to empty when its filter is cleared some other way (a chip's "×", "Clear all").</summary>
    private readonly Dictionary<string, TextBox> _columnFilterBoxes = new();

    private readonly Dictionary<TextBox, DispatcherTimer> _filterDebounceTimers = new();

    /// <summary>Boxes currently being updated programmatically (see <see cref="SyncColumnFilterBoxesFromViewModel"/>) — <see cref="OnColumnFilterRowTextChanged"/> ignores changes to these so an external clear never re-triggers as if the user had typed it.</summary>
    private readonly HashSet<TextBox> _suppressFilterTextChanged = new();

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

    /// <summary>Header checkbox: selects/clears every row matching the active filters, across every page — see <see cref="ViewModels.RowSelectionState"/>.</summary>
    private void OnSelectAllHeaderChecked(object sender, RoutedEventArgs e) => _viewModel.Grid?.SelectAllRows();

    private void OnSelectAllHeaderUnchecked(object sender, RoutedEventArgs e) => _viewModel.Grid?.ClearAllRowSelection();

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

    /// <summary>Per-row "Delete" button — removes this one record immediately, independent of checkbox selection.</summary>
    private void OnDeleteRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RowViewModel row } || _viewModel.Grid is not { } grid) return;
        grid.DeleteRow(row.RowIndex);
    }

    /// <summary>
    /// A single click on a column header opens its value-filter popup; a double-click sorts by it
    /// instead. Intercepting on Preview (mouse-down, before the header's own click/Sorting
    /// machinery runs) and marking the event handled suppresses WPF's built-in header-click sort
    /// entirely, so single-click never fires it. Clicks that land inside the header's own filter
    /// TextBox (the ag-Grid-style filter row) are left completely alone — that box needs normal
    /// text-editing mouse behavior, not sort/popup interception.
    /// </summary>
    private async void OnDataGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<TextBox>(e.OriginalSource as DependencyObject) is not null) return;
        if (FindAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject) is not { } header) return;
        if (header.Column?.SortMemberPath is not { Length: > 0 } columnName) return;
        if (_viewModel.Grid is not { } grid) return;

        e.Handled = true;

        if (e.ClickCount >= 2)
        {
            await grid.SortByColumnAsync(columnName);
        }
        else
        {
            RequestOpenColumnFilterPopup(columnName, header, grid);
        }
    }

    private void RebuildColumns()
    {
        RowsDataGrid.Columns.Clear();

        foreach (DispatcherTimer timer in _filterDebounceTimers.Values) timer.Stop();
        _filterDebounceTimers.Clear();
        _columnHeaderTextBlocks.Clear();
        _columnFilterBoxes.Clear();
        _suppressFilterTextChanged.Clear();

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
        RowsDataGrid.Columns.Add(new DataGridTemplateColumn
        {
            Header = string.Empty,
            CellTemplate = (DataTemplate)FindResource("DeleteButtonCellTemplate"),
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

            var headerText = new TextBlock
            {
                Text = HeaderTextFor(columnName, isFiltered: false),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            _columnHeaderTextBlocks[columnName] = headerText;

            var filterBox = new TextBox
            {
                Margin = new Thickness(0, 4, 0, 0),
                FontWeight = FontWeights.Normal,
                ToolTip = "Filter this column. Wrap in / / for regex, e.g. /^AB/.",
            };
            filterBox.TextChanged += (_, _) => OnColumnFilterRowTextChanged(columnName, filterBox);
            _columnFilterBoxes[columnName] = filterBox;

            var headerPanel = new StackPanel { Orientation = Orientation.Vertical };
            headerPanel.Children.Add(headerText);
            headerPanel.Children.Add(filterBox);

            var column = new DataGridTextColumn
            {
                Header = headerPanel,
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

        // Keeps each filter box's displayed text in sync when a filter is cleared some other way
        // (a chip's "×", "Clear all filters") — see SyncColumnFilterBoxesFromViewModel's remarks
        // for why a box the user is actively typing into is deliberately skipped.
        grid.Rows.CollectionChanged += (_, _) => SyncColumnFilterBoxesFromViewModel(grid);
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

    // ============================== ag-Grid-style per-column filter row ==============================

    /// <summary>
    /// Debounces a filter box's keystrokes — applying the pattern on every single character would
    /// mean kicking off a background decode-and-filter pass per keystroke, which is both wasteful
    /// and (since each pass takes real time) prone to results arriving out of order. Restarting the
    /// timer on every change means the pattern is only actually applied once typing pauses.
    /// </summary>
    private void OnColumnFilterRowTextChanged(string columnName, TextBox textBox)
    {
        if (_suppressFilterTextChanged.Contains(textBox)) return;

        if (!_filterDebounceTimers.TryGetValue(textBox, out DispatcherTimer? timer))
        {
            timer = new DispatcherTimer { Interval = FilterRowDebounce };
            _filterDebounceTimers[textBox] = timer;
        }

        timer.Stop();
        timer.Tag = columnName;
        timer.Tick -= OnFilterDebounceTick;
        timer.Tick += OnFilterDebounceTick;
        timer.Start();
    }

    private async void OnFilterDebounceTick(object? sender, EventArgs e)
    {
        if (sender is not DispatcherTimer timer) return;
        timer.Stop();
        timer.Tick -= OnFilterDebounceTick;

        if (timer.Tag is not string columnName) return;
        if (!_columnFilterBoxes.TryGetValue(columnName, out TextBox? textBox)) return;

        await ApplyColumnPatternFilterAsync(columnName, textBox);
    }

    /// <summary>
    /// Parses the box's current text ("/pattern/" is regex, anything else is a plain
    /// case-insensitive substring), validates it if it's a regex, and — only if valid — applies it.
    /// An invalid regex is a normal state while the user is still typing it, not an error to
    /// silently swallow or throw: the box's border turns red and the *previous* valid filter (if
    /// any) is left in place until the pattern becomes valid again.
    /// </summary>
    private async Task ApplyColumnPatternFilterAsync(string columnName, TextBox textBox)
    {
        if (_viewModel.Grid is not { } grid) return;

        (string pattern, bool useRegex) = ParseFilterRowInput(textBox.Text);

        if (useRegex && pattern.Length > 0 && !RowFilter.TryCompileRegex(pattern, out _, out _))
        {
            textBox.BorderBrush = (Brush)FindResource("PaleRedTextBrush");
            textBox.BorderThickness = new Thickness(1.5);
            return;
        }

        textBox.ClearValue(BorderBrushProperty);
        textBox.ClearValue(BorderThicknessProperty);
        await grid.SetColumnPatternFilterAsync(columnName, pattern, useRegex);
    }

    private static (string Pattern, bool UseRegex) ParseFilterRowInput(string rawText)
    {
        if (rawText.Length >= 2 && rawText[0] == '/' && rawText[^1] == '/')
        {
            return (rawText[1..^1], true);
        }
        return (rawText, false);
    }

    /// <summary>
    /// Resyncs every filter box's displayed text to the view model's actual current filter — needed
    /// because a filter can be cleared from somewhere other than its own box (an active-filter
    /// chip's "×", "Clear all filters"). Skips whichever box currently has keyboard focus: that's
    /// the box the user might still be actively typing into, and this can fire from an unrelated
    /// change elsewhere (a different column's filter, a row being added) — overwriting live input
    /// with the last *applied* value is exactly the "I lose my typing" bug this whole change exists
    /// to fix, not something to reintroduce here.
    /// </summary>
    private void SyncColumnFilterBoxesFromViewModel(GridViewModel grid)
    {
        foreach ((string columnName, TextBox textBox) in _columnFilterBoxes)
        {
            if (textBox.IsFocused) continue;

            ColumnPatternFilter? active = grid.Rows.GetColumnPatternFilter(columnName);
            string desired = active is { } filter ? (filter.UseRegex ? $"/{filter.Pattern}/" : filter.Pattern) : string.Empty;
            if (textBox.Text == desired) continue;

            _suppressFilterTextChanged.Add(textBox);
            textBox.Text = desired;
            textBox.ClearValue(BorderBrushProperty);
            textBox.ClearValue(BorderThicknessProperty);
            _suppressFilterTextChanged.Remove(textBox);
        }
    }

    // ============================== Column value filter (Excel-style) ==============================

    /// <summary>Right-click anywhere in a column header also opens its value-filter popup — kept as a secondary path alongside the primary single-click gesture. Also leaves filter-box clicks alone, same as the left-button handler.</summary>
    private void OnDataGridPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<TextBox>(e.OriginalSource as DependencyObject) is not null) return;
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

    /// <summary>
    /// Opens the popup immediately (with an empty, loading list) and only populates it once the
    /// distinct-value lookup — which decodes every candidate row — finishes on a background thread.
    /// The previous synchronous version froze the UI thread for however long that lookup took, and
    /// any click that landed during the freeze was either dropped or hit a control that had since
    /// moved; opening first and filling in afterward means the popup is always interactive the
    /// instant it's visible, never mid-freeze.
    /// </summary>
    private async void OpenColumnFilterPopup(string columnName, UIElement placementTarget, GridViewModel grid)
    {
        if (grid.IsBusy) return;

        _activeFilterColumn = columnName;
        ColumnFilterTitle.Text = $"FILTER — {columnName.ToUpperInvariant()}";
        ColumnFilterSearchBox.Text = string.Empty;
        _activeFilterOptions = [];
        _activeFilterOptionsView = null;
        ColumnFilterValuesList.ItemsSource = null;
        ColumnFilterLoadingText.Visibility = Visibility.Visible;

        ColumnFilterPopup.PlacementTarget = placementTarget;
        ColumnFilterPopup.IsOpen = true;

        List<string> distinctValues = await grid.GetDistinctValuesForColumnAsync(columnName);

        // The popup (or the column it was opened for) may have moved on while the lookup was
        // running — the user could have closed it, or clicked a different column's header instead.
        // Only apply a stale result if it's still the one this popup is actually showing.
        if (!ColumnFilterPopup.IsOpen || _activeFilterColumn != columnName) return;

        ColumnFilterLoadingText.Visibility = Visibility.Collapsed;
        HashSet<string>? currentSelection = grid.Rows.GetColumnValueFilter(columnName);
        _activeFilterOptions = [.. distinctValues.Select(v => new ColumnFilterValueOption(v, currentSelection is null || currentSelection.Contains(v)))];

        ICollectionView view = CollectionViewSource.GetDefaultView(_activeFilterOptions);
        _activeFilterOptionsView = view;
        ColumnFilterValuesList.ItemsSource = view;
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

    private async void OnColumnFilterApplyClick(object sender, RoutedEventArgs e)
    {
        if (_activeFilterColumn is not { } columnName || _viewModel.Grid is not { } grid) return;

        var selected = _activeFilterOptions.Where(o => o.IsSelected).Select(o => o.Value).ToHashSet();
        // Everything selected is equivalent to "no filter" — clear it rather than storing a
        // full-set filter, so newly-added rows/values aren't silently excluded later.
        bool isEffectivelyUnfiltered = selected.Count == _activeFilterOptions.Count;

        // Close and update the header text immediately — instant feedback that the click
        // registered — while the actual (potentially slow) recompute runs in the background.
        UpdateColumnHeaderText(columnName, isFiltered: !isEffectivelyUnfiltered);
        ColumnFilterPopup.IsOpen = false;

        await grid.SetColumnValueFilterAsync(columnName, isEffectivelyUnfiltered ? null : selected);
    }

    private void OnColumnFilterCancelClick(object sender, RoutedEventArgs e) => ColumnFilterPopup.IsOpen = false;

    private void UpdateColumnHeaderText(string columnName, bool isFiltered)
    {
        if (_columnHeaderTextBlocks.TryGetValue(columnName, out TextBlock? headerText))
        {
            headerText.Text = HeaderTextFor(columnName, isFiltered);
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
