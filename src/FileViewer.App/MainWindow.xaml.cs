using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using FileViewer.App.Collections;
using FileViewer.App.ViewModels;
using FileViewer.App.Views;
using Microsoft.Win32;

namespace FileViewer.App;

public partial class MainWindow : Window
{
    /// <summary>Number of always-present, non-data columns (select checkbox, view button, delete button) prepended to every dynamically-built column set.</summary>
    private const int FixedColumnCount = 3;

    /// <summary>How long a burst of DataGrid resize events (a live window-resize drag) waits before actually recomputing the page size — avoids rebuilding the visible page on every intermediate frame of the drag.</summary>
    private static readonly TimeSpan PageSizeDebounce = TimeSpan.FromMilliseconds(150);

    private readonly MainViewModel _viewModel = new();
    private string? _activeFilterColumn;
    private List<ColumnFilterValueOption> _activeFilterOptions = [];
    private ICollectionView? _activeFilterOptionsView;
    private DispatcherTimer? _pageSizeDebounceTimer;

    /// <summary>The header TextBlock for each data column — <see cref="UpdateColumnHeaderText"/> updates these directly now that <c>DataGridColumn.Header</c> is a Grid (name + menu button), not a plain string.</summary>
    private readonly Dictionary<string, TextBlock> _columnHeaderTextBlocks = new();

    /// <summary>The 3 bars of each data column's header "menu" icon (see <see cref="BuildColumnMenuIcon"/>) — recolored to the accent brush by <see cref="UpdateColumnHeaderText"/> while that column has an active Excel-style value filter, mirroring the "●" text indicator.</summary>
    private readonly Dictionary<string, Rectangle[]> _columnMenuIconBars = new();

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
            // The grid may just have gone from Collapsed (no file open) to Visible with a brand
            // new size the DataGrid's own SizeChanged hasn't necessarily fired for yet by this
            // point in the property-changed cycle — deferring to Loaded priority runs this once
            // WPF has actually finished laying the now-visible grid out.
            Dispatcher.BeginInvoke(ApplyDynamicPageSize, DispatcherPriority.Loaded);
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
    /// Recomputes how many rows fit in the DataGrid's current visible height and installs that as
    /// the page size, so a page fills the available screen space (no dead whitespace below the last
    /// row, no need to scroll/page through a mostly-empty page) instead of being pinned to a fixed
    /// row count. Debounced (see <see cref="PageSizeDebounce"/>) since a live window-resize drag
    /// fires SizeChanged continuously.
    /// </summary>
    private void OnRowsDataGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _pageSizeDebounceTimer ??= new DispatcherTimer { Interval = PageSizeDebounce };
        _pageSizeDebounceTimer.Stop();
        _pageSizeDebounceTimer.Tick -= OnPageSizeDebounceTick;
        _pageSizeDebounceTimer.Tick += OnPageSizeDebounceTick;
        _pageSizeDebounceTimer.Start();
    }

    private void OnPageSizeDebounceTick(object? sender, EventArgs e)
    {
        _pageSizeDebounceTimer!.Stop();
        ApplyDynamicPageSize();
    }

    private void ApplyDynamicPageSize()
    {
        if (_viewModel.Grid is not { } grid) return;

        double headerHeight = double.IsNaN(RowsDataGrid.ColumnHeaderHeight) ? 36 : RowsDataGrid.ColumnHeaderHeight;
        double rowHeight = double.IsNaN(RowsDataGrid.RowHeight) || RowsDataGrid.RowHeight <= 0 ? 28 : RowsDataGrid.RowHeight;
        double available = RowsDataGrid.ActualHeight - headerHeight;
        if (available <= 0) return; // not laid out yet (e.g. no file open) — leave the current page size alone

        int rowsThatFit = Math.Max(1, (int)Math.Floor(available / rowHeight));
        grid.Rows.SetPageSize(rowsThatFit);
    }

    private void RebuildColumns()
    {
        RowsDataGrid.Columns.Clear();

        _columnHeaderTextBlocks.Clear();
        _columnMenuIconBars.Clear();

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
                VerticalAlignment = VerticalAlignment.Center,
            };
            _columnHeaderTextBlocks[columnName] = headerText;

            // The ag-Grid-style "menu" button: the sole way to open this column's Excel-style
            // value-filter popup (see OnColumnMenuButtonClick for why a dedicated Button, rather
            // than intercepting the header's own click, is what actually fixes the popup
            // open-then-instantly-close bug).
            (Viewbox menuIcon, Rectangle[] menuIconBars) = BuildColumnMenuIcon();
            _columnMenuIconBars[columnName] = menuIconBars;
            var menuButton = new Button
            {
                Content = menuIcon,
                Width = 20,
                Height = 20,
                Padding = new Thickness(0),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                Tag = columnName,
                ToolTip = "Filter this column by value",
            };
            AutomationProperties.SetName(menuButton, $"Filter {columnName}");
            menuButton.Click += OnColumnMenuButtonClick;

            var headerRow = new Grid();
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(headerText, 0);
            Grid.SetColumn(menuButton, 1);
            headerRow.Children.Add(headerText);
            headerRow.Children.Add(menuButton);

            var column = new DataGridTextColumn
            {
                Header = headerRow,
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
                case nameof(GridViewModel.ColumnSearchText):
                    columnsView.Refresh();
                    break;
            }
        };
    }

    private static string HeaderTextFor(string columnName, bool isFiltered) =>
        isFiltered ? $"{columnName.ToUpperInvariant()}  ●" : columnName.ToUpperInvariant();

    /// <summary>
    /// A small hand-drawn 3-bar "hamburger" icon, matching the hand-drawn Path/Shape glyphs used
    /// elsewhere (the row View/Delete buttons) rather than an icon font. Built fresh per column
    /// (WPF visuals can only have one parent) with its bars exposed separately so the caller can
    /// keep a reference and recolor them later without rebuilding the whole icon.
    /// </summary>
    private static (Viewbox Icon, Rectangle[] Bars) BuildColumnMenuIcon()
    {
        Rectangle[] bars = [MakeBar(5), MakeBar(11), MakeBar(17)];
        var canvas = new Canvas { Width = 24, Height = 24 };
        foreach (Rectangle bar in bars) canvas.Children.Add(bar);

        var icon = new Viewbox { Width = 11, Height = 11, Stretch = Stretch.Uniform, Child = canvas };
        return (icon, bars);

        static Rectangle MakeBar(double top)
        {
            var bar = new Rectangle { Width = 20, Height = 2.6, RadiusX = 1.3, RadiusY = 1.3 };
            bar.SetResourceReference(Shape.FillProperty, "TextSecondaryBrush");
            Canvas.SetLeft(bar, 2);
            Canvas.SetTop(bar, top);
            return bar;
        }
    }

    // ============================== Column value filter (Excel-style) ==============================

    /// <summary>
    /// The per-column "menu" button (the small hamburger icon in the header, built by
    /// <see cref="BuildColumnMenuIcon"/>) is the sole way to open the Excel-style value-filter
    /// popup, and it toggles: clicking it again while its own column's popup is already open closes
    /// it instead of reopening. This used to be a single click anywhere on the header, opened by
    /// deferring past the header's own mouse-down capture handling — but opening a
    /// StaysOpen="False" Popup synchronously inside the same mouse-down that also drives an
    /// ancestor ButtonBase's (the header's) own press/capture state meant the popup saw that
    /// capture loss as "clicked outside" and closed itself an instant after opening: the classic
    /// "have to hold the mouse down to see it" symptom. A plain Button.Click fires cleanly after
    /// mouse-up, once WPF's own capture handling for that click has already settled, so routing the
    /// open through this independent Button (excluded from the header's own click handling — see
    /// OnDataGridPreviewMouseLeftButtonDown) sidesteps the race entirely instead of racing it.
    /// </summary>
    private void OnColumnMenuButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string columnName } button) return;
        if (_viewModel.Grid is not { } grid) return;

        if (ColumnFilterPopup.IsOpen && _activeFilterColumn == columnName)
        {
            ColumnFilterPopup.IsOpen = false;
            return;
        }

        OpenColumnFilterPopup(columnName, button, grid);
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

        // The search box was interactive the whole time the values were loading — if the user
        // typed into it before this point, that text was silently ignored (no view existed yet to
        // filter). Apply whatever it currently holds now that there's finally a view to apply it to.
        ApplyColumnFilterSearch();
    }

    private void OnColumnFilterSearchChanged(object sender, TextChangedEventArgs e) => ApplyColumnFilterSearch();

    private void ApplyColumnFilterSearch()
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

        // Recolors the menu icon to the accent brush while this column has an active value filter —
        // the same at-a-glance "this is filtered" signal ag-Grid gives its own column menu icon.
        if (_columnMenuIconBars.TryGetValue(columnName, out Rectangle[]? bars))
        {
            string brushKey = isFiltered ? "AccentDarkBrush" : "TextSecondaryBrush";
            foreach (Rectangle bar in bars)
            {
                bar.SetResourceReference(Shape.FillProperty, brushKey);
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
