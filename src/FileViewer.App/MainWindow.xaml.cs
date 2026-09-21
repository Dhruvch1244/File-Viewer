using System.ComponentModel;
using System.IO;
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
using FileViewer.App.Logging;
using FileViewer.App.ViewModels;
using FileViewer.App.Views;
using FileViewer.Core.Dif;
using FileViewer.Core.Filtering;
using FileViewer.Core.Sorting;
using FileViewer.Core.Statistics;
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

    /// <summary>How long a toast stays at full opacity before it starts fading.</summary>
    private static readonly TimeSpan ToastDwell = TimeSpan.FromMilliseconds(2600);

    private readonly DispatcherTimer _toastTimer = new() { Interval = ToastDwell };

    /// <summary>Bumped by every <see cref="ShowToast"/> so a fade left over from the previous toast can tell it has been superseded and leave the new one alone.</summary>
    private int _toastGeneration;

    /// <summary>The header TextBlock for each data column — <see cref="UpdateColumnHeaderText"/> updates these directly now that <c>DataGridColumn.Header</c> is a Grid (name + menu button), not a plain string.</summary>
    private readonly Dictionary<string, TextBlock> _columnHeaderTextBlocks = new();

    /// <summary>
    /// The grid whose change notifications the current column set is wired to, and the handlers
    /// wired to it. With several tabs (and several sections per tab) sharing one DataGrid, the same
    /// <see cref="GridViewModel"/> is rebuilt against every time the user switches back to it — so
    /// the previous wiring has to come off first, or each visit would leave another live handler
    /// behind updating a DataGridColumn that is no longer in the grid.
    /// </summary>
    private GridViewModel? _wiredGrid;
    private PropertyChangedEventHandler? _wiredGridHandler;
    private readonly List<(GridColumnInfo Definition, PropertyChangedEventHandler Handler)> _wiredColumnDefinitions = new();

    /// <summary>The 3 bars of each data column's header "menu" icon (see <see cref="BuildColumnMenuIcon"/>) — recolored to the accent brush by <see cref="UpdateColumnHeaderText"/> while that column has an active Excel-style value filter, mirroring the "●" text indicator.</summary>
    private readonly Dictionary<string, Rectangle[]> _columnMenuIconBars = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.ConfirmDiscardingEdits = ConfirmDiscardingEdits;
        _toastTimer.Tick += OnToastTimerTick;
        Loaded += OnWindowLoaded;
    }

    /// <summary>
    /// Opens whatever was passed on the command line — how the app behaves when a .dif is opened
    /// with it, or dragged onto its shortcut. Several paths open as several tabs.
    /// </summary>
    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnWindowLoaded;

        // Skip element 0: that is this executable's own path, not a file to open.
        foreach (string argument in Environment.GetCommandLineArgs().Skip(1))
        {
            if (argument.StartsWith('-') || argument.StartsWith('/')) continue; // a switch, not a path
            if (!File.Exists(argument)) continue;

            await _viewModel.OpenFileAsync(argument);
        }
    }

    /// <summary>Files dropped onto the window open as tabs — the same as choosing them from Open File.</summary>
    private async void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        e.Handled = true;
        foreach (string path in paths.Where(File.Exists))
        {
            await _viewModel.OpenFileAsync(path);
        }
    }

    /// <summary>Shows the copy cursor only for a file drop, so dragging anything else reads as "not accepted" rather than silently doing nothing.</summary>
    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// Application-wide keyboard shortcuts. Handled here rather than as InputBindings so that one
    /// place decides what a key does, and so a shortcut can act on the focused element (Ctrl+F puts
    /// the caret in the search box) rather than only invoking a command.
    ///
    /// Only modifier combinations and the function keys are claimed — anything a user might be
    /// typing into a cell or the search box passes straight through.
    /// </summary>
    private async void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool control = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

        if (e.Key == Key.F3)
        {
            e.Handled = true;
            _viewModel.Grid?.GoToMatch(shift ? -1 : 1);
            return;
        }

        if (e.Key == Key.Escape)
        {
            // Close whichever popup is open; otherwise let Escape do its normal job (cancelling a
            // cell edit, which the DataGrid handles itself).
            if (ColumnsPopup.IsOpen || ColumnFilterPopup.IsOpen || RecentFilesPopup.IsOpen)
            {
                ColumnsPopup.IsOpen = false;
                ColumnFilterPopup.IsOpen = false;
                RecentFilesPopup.IsOpen = false;
                e.Handled = true;
            }
            return;
        }

        if (!control) return;

        switch (e.Key)
        {
            case Key.O:
                e.Handled = true;
                await OpenFileViaDialogAsync();
                break;

            case Key.F:
                e.Handled = true;
                SearchTextBox.Focus();
                SearchTextBox.SelectAll();
                break;

            case Key.W:
                e.Handled = true;
                _viewModel.CloseActiveTab();
                break;

            case Key.E:
                e.Handled = true;
                OnExportButtonClick(this, new RoutedEventArgs());
                break;

            case Key.C:
                // Only when the grid has the focus: Ctrl+C inside the search box must stay ordinary
                // text copying.
                if (RowsDataGrid.IsKeyboardFocusWithin)
                {
                    e.Handled = true;
                    await CopySelectionAsync(ClipboardOptions.Default);
                }
                break;

            case Key.Z:
                if (_viewModel.Grid is { } grid && grid.UndoCommand.CanExecute(null))
                {
                    e.Handled = true;
                    grid.UndoCommand.Execute(null);
                }
                break;

            case Key.Tab:
                e.Handled = true;
                await _viewModel.CycleTabAsync(shift ? -1 : 1);
                break;
        }
    }

    /// <summary>
    /// Asks before a file's in-memory edits are thrown away. The app never writes to the source
    /// file, so an unexported edit is gone the moment its tab closes.
    /// </summary>
    private bool ConfirmDiscardingEdits(FileTabViewModel tab) =>
        MessageBox.Show(
            $"'{tab.Title}' has edits that haven't been exported.\n\nClose it and discard them?",
            "Bloomberg File Viewer",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel) == MessageBoxResult.OK;

    /// <summary>The same question for the whole window: closing it discards every open file's edits at once.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || !_viewModel.HasUnsavedEdits) return;

        bool discard = MessageBox.Show(
            "Some open files have edits that haven't been exported.\n\nClose anyway and discard them?",
            "Bloomberg File Viewer",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel) == MessageBoxResult.OK;

        e.Cancel = !discard;
    }

    /// <summary>Releases every open file's index/handles on close — each tab holds unmanaged row-index memory and an open file handle.</summary>
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _viewModel.Dispose();
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

    private async void OnOpenFileClick(object sender, RoutedEventArgs e) => await OpenFileViaDialogAsync();

    private async Task OpenFileViaDialogAsync()
    {
        // Any file is accepted — the app itself decides whether the content is a valid DIF file
        // and fails gracefully (with diagnostics) if not, so the dialog shouldn't gatekeep by extension.
        var dialog = new OpenFileDialog
        {
            Filter = "All files (*.*)|*.*|DIF files (*.dif)|*.dif",
            FilterIndex = 1,
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true) return;

        foreach (string path in dialog.FileNames)
        {
            await _viewModel.OpenFileAsync(path);
        }
    }

    private void OnRecentFilesButtonClick(object sender, RoutedEventArgs e) => RecentFilesPopup.IsOpen = !RecentFilesPopup.IsOpen;

    /// <summary>Closes the recent-files popup as soon as one is chosen, so the list doesn't hang over the file it just opened.</summary>
    private void OnRecentFileClick(object sender, RoutedEventArgs e) => RecentFilesPopup.IsOpen = false;

    /// <summary>Tab strip: brings that file's grid to the front (indexing its section first if it has never been shown).</summary>
    private async void OnFileTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FileTabViewModel tab }) return;
        await _viewModel.ActivateTabAsync(tab);
    }

    /// <summary>Middle-click closes a tab, the way it does in every other tabbed app.</summary>
    private void OnTabMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if (sender is not FrameworkElement { DataContext: FileTabViewModel tab }) return;

        e.Handled = true;
        _viewModel.CloseTab(tab);
    }

    /// <summary>Section bar (bulk files): switches the active tab to one of its DATA= sections, indexing it on first use.</summary>
    private async void OnOpenAllSectionsInTabsClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.ActiveTab is { } tab) await _viewModel.OpenAllSectionsAsTabsAsync(tab);
    }

    private async void OnSectionChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FileSectionViewModel section }) return;
        if (_viewModel.ActiveTab is not { } tab) return;
        await _viewModel.ShowSectionAsync(tab, section);
    }

    /// <summary>Flips between filtering rows away and highlighting them in place, carrying the current terms across.</summary>
    private void OnToggleSearchModeClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Grid is not { } grid) return;
        grid.HighlightInsteadOfFilter = !grid.HighlightInsteadOfFilter;
    }

    /// <summary>Flips the multi-term search between "a row must match every term" and "any one term is enough".</summary>
    private void OnToggleCombineModeClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Grid is not { } grid) return;
        grid.MatchAllSearchTerms = !grid.MatchAllSearchTerms;
    }

    private void OnColumnsButtonClick(object sender, RoutedEventArgs e) => ColumnsPopup.IsOpen = !ColumnsPopup.IsOpen;

    /// <summary>Copies the ticked rows (or the focused one) as tab-separated text, ready to paste into a spreadsheet.</summary>
    private async void OnCopyClick(object sender, RoutedEventArgs e) => await CopySelectionAsync(ClipboardOptions.Default);

    private void OnCopyOptionsClick(object sender, RoutedEventArgs e) => CopyOptionsPopup.IsOpen = !CopyOptionsPopup.IsOpen;

    /// <summary>
    /// Resets every control back to what the plain Copy button does, each time the popup opens —
    /// deliberately not remembered from the last time it was opened, so picking "All columns" once
    /// doesn't silently change what a later plain click of Copy itself does, or what a later open
    /// of this same popup starts on.
    /// </summary>
    private void OnCopyOptionsPopupOpened(object sender, EventArgs e)
    {
        OnPopupOpened(sender, e); // still gets the shared fade-in animation

        CopyRowsSelectedRadio.IsChecked = true;
        CopyColumnsVisibleRadio.IsChecked = true;
        CopyFormatTsvRadio.IsChecked = true;
        CopyIncludeHeadersCheckBox.IsChecked = true;
        CopyIncludeHeadersCheckBox.IsEnabled = true;
    }

    /// <summary>JSON rows carry their own field names as object keys, so the headers checkbox has nothing to do there.</summary>
    private void OnCopyFormatChanged(object sender, RoutedEventArgs e)
    {
        bool isJson = ReferenceEquals(sender, CopyFormatJsonRadio);
        CopyIncludeHeadersCheckBox.IsEnabled = !isJson;
    }

    private async void OnCopyApplyClick(object sender, RoutedEventArgs e)
    {
        CopyOptionsPopup.IsOpen = false;

        var options = new ClipboardOptions(
            Scope: CopyRowsAllInViewRadio.IsChecked == true ? ClipboardScope.AllRowsInView : ClipboardScope.SelectedRows,
            ColumnScope: CopyColumnsAllRadio.IsChecked == true ? ClipboardColumnScope.AllColumns : ClipboardColumnScope.VisibleColumns,
            Format: CopyFormatJsonRadio.IsChecked == true ? ClipboardFormat.Json
                  : CopyFormatCsvRadio.IsChecked == true ? ClipboardFormat.Csv
                  : ClipboardFormat.TabSeparated,
            IncludeHeaders: CopyIncludeHeadersCheckBox.IsChecked == true);

        await CopySelectionAsync(options);
    }

    private async Task CopySelectionAsync(ClipboardOptions options)
    {
        if (_viewModel.Grid is not { } grid) return;

        ClipboardPayload payload = await grid.BuildClipboardTextAsync(options);
        if (payload.RowCount == 0)
        {
            ShowToast(options.Scope == ClipboardScope.AllRowsInView
                ? "Nothing to copy — no rows in view."
                : "Nothing to copy — tick some rows, or click one.");
            return;
        }

        try
        {
            Clipboard.SetText(payload.Text);
        }
        catch (Exception ex)
        {
            // The clipboard is a shared OS resource and another process can be holding it open;
            // failing to copy is not a reason to take the app down.
            FileLogger.Instance.LogWarning($"Could not write to the clipboard: {ex.Message}");
            ShowToast("Could not copy — another program is using the clipboard.");
            return;
        }

        string what = options.Format switch
        {
            ClipboardFormat.Csv => "as CSV",
            ClipboardFormat.Json => "as JSON",
            _ => "to clipboard",
        };
        if (options.ColumnScope == ClipboardColumnScope.AllColumns) what += ", all columns";
        ShowToast(payload.Truncated
            ? $"Copied the first {payload.RowCount:N0} of your rows {what} — more than the {GridViewModel.MaxClipboardRows:N0} row limit."
            : $"Copied {payload.RowCount:N0} row{(payload.RowCount == 1 ? "" : "s")} {what}.");
    }

    private void OnCancelFilterClick(object sender, RoutedEventArgs e) => _viewModel.Grid?.CancelFilter();

    /// <summary>Pulls the ticked rows out into a view of their own.</summary>
    private async void OnExtractSelectionClick(object sender, RoutedEventArgs e) => await _viewModel.ExtractSelectionAsync();

    /// <summary>
    /// Opens a second, independent window — its own files, its own tabs. For comparing two files
    /// side by side on one screen, which tabs in a single window can't do.
    /// </summary>
    private void OnNewWindowClick(object sender, RoutedEventArgs e) => new MainWindow().Show();

    private void OnShowHiddenMatchColumnsClick(object sender, RoutedEventArgs e) => _viewModel.Grid?.ShowHiddenMatchColumns();

    /// <summary>Opens the file-info panel, filling it from whatever file is on screen right now.</summary>
    private void OnFileInfoClick(object sender, RoutedEventArgs e)
    {
        if (FileInfoPopup.IsOpen)
        {
            FileInfoPopup.IsOpen = false;
            return;
        }
        if (_viewModel.Grid is not { } grid) return;

        FileInfoList.ItemsSource = DescribeFile(grid);

        var diagnostics = grid.Session.FileIndex.Diagnostics
            .Select(d => new FileDiagnosticLine($"{d.Severity} at byte {d.Offset:N0}", d.Message))
            .ToList();
        FileDiagnosticsList.ItemsSource = diagnostics;
        FileDiagnosticsHeading.Visibility = diagnostics.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        FileInfoPopup.IsOpen = true;
    }

    /// <summary>The header/trailer metadata and section details a user needs when a file looks wrong.</summary>
    private static List<ColumnStatLine> DescribeFile(GridViewModel grid)
    {
        DifFileHeader header = grid.Session.FileIndex.Header;
        var lines = new List<ColumnStatLine>
        {
            new("File", grid.Session.FileIndex.FilePath),
            new("Size", $"{grid.Session.FileIndex.FileLength / 1024.0 / 1024.0:N1} MB"),
            new("Rows indexed", $"{grid.Session.FileIndex.RowIndex.Count:N0}"),
            new("Columns", $"{header.ColumnNames.Count:N0}"),
            new("Delimiter", header.Delimiter == '\t' ? "(tab)" : header.Delimiter.ToString()),
            new("Header marker", header.HeaderMarker),
        };

        if (header.IsMultiSection)
        {
            lines.Add(new ColumnStatLine("Section", $"{header.SectionName}  ({header.SectionIndex + 1} of {header.SectionCount})"));
        }
        if (header.DeclaredDataRecords is int declared)
        {
            lines.Add(new ColumnStatLine("DATARECORDS", $"{declared:N0}"));
        }

        foreach ((string key, string value) in header.HeaderMetadata) lines.Add(new ColumnStatLine(key, value));
        foreach ((string key, string value) in header.PostFieldsMetadata) lines.Add(new ColumnStatLine(key, value));
        foreach ((string key, string value) in header.TrailerMetadata) lines.Add(new ColumnStatLine(key, value));

        return lines;
    }

    /// <summary>
    /// A short-lived confirmation over the grid. Copying otherwise says nothing at all about whether
    /// it worked or how much it took, and the status bar — a grey line at the bottom of the window —
    /// is not where anyone looks straight after pressing Ctrl+C. The message still goes to the status
    /// bar as well, so it is readable after the toast has gone.
    /// </summary>
    private void ShowToast(string message)
    {
        _viewModel.ReportStatus(message);

        ToastText.Text = message;
        // A held animation keeps ownership of Opacity, so a second toast arriving mid-fade would be
        // stuck at whatever the last one faded to. Clearing it hands the property back.
        Toast.BeginAnimation(UIElement.OpacityProperty, null);
        Toast.Opacity = 1;
        Toast.Visibility = Visibility.Visible;

        _toastGeneration++;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void OnToastTimerTick(object? sender, EventArgs e)
    {
        _toastTimer.Stop();

        int generation = _toastGeneration;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260));
        fade.Completed += (_, _) =>
        {
            if (generation != _toastGeneration) return; // a newer toast took over while this faded
            Toast.Visibility = Visibility.Collapsed;
        };
        Toast.BeginAnimation(UIElement.OpacityProperty, fade);
    }

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

    private void OnDeselectAllColumnsClick(object sender, RoutedEventArgs e) => SetVisibilityForFilteredColumns(isVisible: false);

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

        var dialog = new ExportDialogView(grid, _viewModel.ActiveTab) { Owner = this };
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

    /// <summary>Enter in the page box jumps to that page; anything unparseable snaps back to the current one.</summary>
    private void OnPageNumberBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        _viewModel.Grid?.GoToPageNumber(PageNumberBox.Text);
        PageNumberBox.Text = _viewModel.Grid?.PageNumberText ?? PageNumberBox.Text;
    }

    /// <summary>Clicking away from a half-typed page number restores what is actually on screen, rather than leaving a number that means nothing.</summary>
    private void OnPageNumberBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Grid is { } grid) PageNumberBox.Text = grid.PageNumberText;
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
            // The dialog edited a different RowViewModel instance than the one the grid has
            // rendered for this row, so the grid's own instance won't hear about the change. Only
            // that row needs re-reading — rebuilding the collection would throw away the scroll
            // position (and the page) for a change that affects one row's text.
            grid.Rows.RefreshRow(row.RowIndex);
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

        // Only the fit-to-window choice is measured from the layout; a fixed count or "all rows"
        // means exactly what it says and must not be overwritten on every resize.
        if (!grid.IsPageSizeFitToWindow) return;

        double headerHeight = double.IsNaN(RowsDataGrid.ColumnHeaderHeight) ? 36 : RowsDataGrid.ColumnHeaderHeight;
        double rowHeight = double.IsNaN(RowsDataGrid.RowHeight) || RowsDataGrid.RowHeight <= 0 ? 28 : RowsDataGrid.RowHeight;
        double available = RowsDataGrid.ActualHeight - headerHeight;
        if (available <= 0) return; // not laid out yet (e.g. no file open) — leave the current page size alone

        int rowsThatFit = Math.Max(1, (int)Math.Floor(available / rowHeight));
        grid.Rows.SetPageSize(rowsThatFit);
    }

    private void RebuildColumns()
    {
        DetachColumnWiring();

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
                CellStyle = BuildHighlightableCellStyle(i),
                // Bug fix: this must be set here, at creation, not only inside the PropertyChanged
                // handler below — otherwise every column starts Visible regardless of
                // GridColumnInfo.IsVisible's initial value (e.g. the "only first 20 by default" rule).
                Visibility = definition.IsVisible ? Visibility.Visible : Visibility.Collapsed,
            };
            // Put back whatever width/position this column had the last time this grid was shown.
            if (grid.ColumnLayouts.TryGetValue(columnName, out ColumnLayout layout) && layout.Width > 0)
            {
                column.Width = new DataGridLength(layout.Width);
            }

            RowsDataGrid.Columns.Add(column);

            // A column filtered before the user switched away is still filtered — the header has to
            // say so again, since this header was just rebuilt from scratch.
            if (grid.Rows.HasColumnValueFilter(columnName))
            {
                UpdateColumnHeaderText(columnName, isFiltered: true);
            }

            void OnDefinitionPropertyChanged(object? _, PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(GridColumnInfo.IsVisible))
                {
                    column.Visibility = definition.IsVisible ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            definition.PropertyChanged += OnDefinitionPropertyChanged;
            _wiredColumnDefinitions.Add((definition, OnDefinitionPropertyChanged));
        }

        RestoreColumnOrder(grid);

        // The select-checkbox and view-button columns are always frozen in addition to however
        // many data columns the user asks to freeze — you always want them visible.
        RowsDataGrid.FrozenColumnCount = FixedColumnCount + grid.FrozenColumnCount;

        // Live filter over the column-chooser popup's name list, driven by GridViewModel.ColumnSearchText.
        ICollectionView columnsView = CollectionViewSource.GetDefaultView(grid.Columns);
        columnsView.Filter = o => o is GridColumnInfo info
            && (string.IsNullOrEmpty(grid.ColumnSearchText) || info.Name.Contains(grid.ColumnSearchText, StringComparison.OrdinalIgnoreCase));

        void OnGridPropertyChanged(object? _, PropertyChangedEventArgs args)
        {
            switch (args.PropertyName)
            {
                case nameof(GridViewModel.FrozenColumnCount):
                    RowsDataGrid.FrozenColumnCount = FixedColumnCount + grid.FrozenColumnCount;
                    break;
                case nameof(GridViewModel.ColumnSearchText):
                    columnsView.Refresh();
                    break;
                // Picking "Fit to window" deliberately doesn't set a row count — the window measures
                // one. Nothing was asking for that measurement, though, so the choice sat there doing
                // nothing until the next resize and the grid kept whatever page size it had before.
                case nameof(GridViewModel.SelectedPageSize):
                    Dispatcher.BeginInvoke(ApplyDynamicPageSize, DispatcherPriority.Loaded);
                    break;
            }
        }

        grid.PropertyChanged += OnGridPropertyChanged;
        grid.MatchFocused += OnMatchFocused;
        _wiredGrid = grid;
        _wiredGridHandler = OnGridPropertyChanged;
    }

    /// <summary>Scrolls a row found by match navigation (F3 / ‹ ›) into view.</summary>
    private void OnMatchFocused(RowViewModel row) => RowsDataGrid.ScrollIntoView(row);

    /// <summary>
    /// Unhooks everything <see cref="RebuildColumns"/> wired up for the previously-shown grid, and
    /// saves what the user had done to its columns first — the DataGrid's own column objects are
    /// thrown away on every switch, so widths and ordering would otherwise reset each time you came
    /// back to a tab.
    /// </summary>
    private void DetachColumnWiring()
    {
        if (_wiredGrid is not null)
        {
            foreach (DataGridColumn column in RowsDataGrid.Columns)
            {
                if (column.Header is Grid { Children: [TextBlock, ..] } && column is DataGridTextColumn { SortMemberPath: { Length: > 0 } name })
                {
                    _wiredGrid.ColumnLayouts[name] = new ColumnLayout(column.ActualWidth, column.DisplayIndex);
                }
            }
        }

        if (_wiredGrid is not null && _wiredGridHandler is not null)
        {
            _wiredGrid.PropertyChanged -= _wiredGridHandler;
            _wiredGrid.MatchFocused -= OnMatchFocused;
        }
        _wiredGrid = null;
        _wiredGridHandler = null;

        foreach ((GridColumnInfo definition, PropertyChangedEventHandler handler) in _wiredColumnDefinitions)
        {
            definition.PropertyChanged -= handler;
        }
        _wiredColumnDefinitions.Clear();
    }

    /// <summary>
    /// A cell style for one data column that paints itself when the search's highlight mode marks
    /// that cell. The column index has to be baked into the binding path (<c>CellMatch[3]</c>),
    /// which is why this is built per column in code rather than being one shared XAML style: a
    /// DataGridCell's DataContext is the row, and nothing in it says which column the cell is in.
    /// </summary>
    private Style BuildHighlightableCellStyle(int columnIndex)
    {
        var style = new Style(typeof(DataGridCell), (Style)FindResource(typeof(DataGridCell)));
        var trigger = new DataTrigger
        {
            Binding = new Binding($"CellMatch[{columnIndex}]"),
            Value = true,
        };
        // DynamicResource (not the brush itself) so the highlight re-colours with the theme.
        trigger.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("SearchHighlightBrush")));
        trigger.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("SearchHighlightTextBrush")));
        style.Triggers.Add(trigger);
        return style;
    }

    /// <summary>
    /// Re-applies a remembered left-to-right column order. Done after every column exists (setting
    /// DisplayIndex shuffles the others, so it can't be done while still adding them), and in
    /// ascending order of the remembered index so each assignment lands where it was.
    /// </summary>
    private void RestoreColumnOrder(GridViewModel grid)
    {
        if (grid.ColumnLayouts.Count == 0) return;

        foreach ((string columnName, ColumnLayout layout) in grid.ColumnLayouts.OrderBy(entry => entry.Value.DisplayIndex))
        {
            if (layout.DisplayIndex < 0 || layout.DisplayIndex >= RowsDataGrid.Columns.Count) continue;

            DataGridColumn? column = RowsDataGrid.Columns
                .FirstOrDefault(c => c is DataGridTextColumn { SortMemberPath: { } path } && path == columnName);
            if (column is not null) column.DisplayIndex = layout.DisplayIndex;
        }
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
        _activeFilterOptions = [];
        _activeFilterOptionsView = null;
        ColumnFilterValuesList.ItemsSource = null;
        ColumnFilterLoadingText.Visibility = Visibility.Visible;
        ColumnFilterTruncatedText.Visibility = Visibility.Collapsed;

        ColumnPatternFilter? existingPattern = grid.Rows.GetColumnPatternFilter(columnName);
        ColumnPatternBox.Text = existingPattern?.Pattern ?? string.Empty;
        ColumnPatternRegexBox.IsChecked = existingPattern?.UseRegex ?? false;

        ShowColumnValuesView();

        ColumnFilterPopup.PlacementTarget = placementTarget;
        ColumnFilterPopup.IsOpen = true;

        DistinctValueResult distinctValues = await grid.GetDistinctValuesForColumnAsync(columnName);

        // The popup (or the column it was opened for) may have moved on while the lookup was
        // running — the user could have closed it, or clicked a different column's header instead.
        // Only apply a stale result if it's still the one this popup is actually showing.
        if (!ColumnFilterPopup.IsOpen || _activeFilterColumn != columnName) return;

        ColumnFilterLoadingText.Visibility = Visibility.Collapsed;
        HashSet<string>? currentSelection = grid.Rows.GetColumnValueFilter(columnName);
        _activeFilterOptions = [.. distinctValues.Values.Select(v => new ColumnFilterValueOption(v, currentSelection is null || currentSelection.Contains(v)))];

        // A column with more distinct values than the lookup collects (a price, an ID) would
        // otherwise present a partial list as if it were the whole set.
        ColumnFilterTruncatedText.Visibility = distinctValues.Truncated ? Visibility.Visible : Visibility.Collapsed;

        ICollectionView view = CollectionViewSource.GetDefaultView(_activeFilterOptions);
        _activeFilterOptionsView = view;
        ColumnFilterValuesList.ItemsSource = view;

    }

    // ============================== Column menu: values vs stats ==============================

    private void OnColumnValuesModeClick(object sender, RoutedEventArgs e) => ShowColumnValuesView();

    private async void OnColumnStatsModeClick(object sender, RoutedEventArgs e) => await ShowColumnStatsViewAsync();

    private void ShowColumnValuesView()
    {
        ColumnValuesPanel.Visibility = Visibility.Visible;
        ColumnStatsPanel.Visibility = Visibility.Collapsed;
        UpdateColumnModeButtons(statsSelected: false);
    }

    /// <summary>
    /// Switches the column menu to its summary view and computes it. Like the value list, the panel
    /// appears immediately with a loading line and fills in when the (background) pass finishes,
    /// rather than freezing the popup until it does.
    /// </summary>
    private async Task ShowColumnStatsViewAsync()
    {
        if (_activeFilterColumn is not { } columnName || _viewModel.Grid is not { } grid) return;

        ColumnValuesPanel.Visibility = Visibility.Collapsed;
        ColumnStatsPanel.Visibility = Visibility.Visible;
        UpdateColumnModeButtons(statsSelected: true);

        ColumnStatsList.ItemsSource = null;
        ColumnStatsLoadingText.Visibility = Visibility.Visible;

        ColumnStatistics stats = await grid.GetColumnStatisticsAsync(columnName);

        // The popup may have closed or moved to another column while this was running.
        if (!ColumnFilterPopup.IsOpen || _activeFilterColumn != columnName) return;

        ColumnStatsLoadingText.Visibility = Visibility.Collapsed;
        ColumnStatsList.ItemsSource = DescribeColumn(stats);
    }

    private void UpdateColumnModeButtons(bool statsSelected)
    {
        ColumnValuesModeButton.FontWeight = statsSelected ? FontWeights.Normal : FontWeights.SemiBold;
        ColumnStatsModeButton.FontWeight = statsSelected ? FontWeights.SemiBold : FontWeights.Normal;
    }

    /// <summary>Turns a column summary into the label/value lines the popup lists. Numeric lines are omitted entirely for a column that holds no numbers, rather than shown as blanks.</summary>
    private static List<ColumnStatLine> DescribeColumn(ColumnStatistics stats)
    {
        var lines = new List<ColumnStatLine>
        {
            new("Rows in view", $"{stats.RowCount:N0}"),
            new("With a value", $"{stats.NonBlankCount:N0}"),
            new("Blank", stats.BlankCount == 0 ? "0" : $"{stats.BlankCount:N0}  ({stats.BlankFraction:P0})"),
            new("Distinct values", stats.DistinctTruncated ? $"{stats.DistinctCount:N0}+" : $"{stats.DistinctCount:N0}"),
        };

        if (stats.Min is not null) lines.Add(new ColumnStatLine("Lowest", stats.Min));
        if (stats.Max is not null) lines.Add(new ColumnStatLine("Highest", stats.Max));

        if (stats.NumericCount > 0)
        {
            if (!stats.IsFullyNumeric)
            {
                // Say so, rather than presenting a sum that silently ignores the rest of the column.
                lines.Add(new ColumnStatLine("Numeric values", $"{stats.NumericCount:N0} of {stats.NonBlankCount:N0}"));
            }
            lines.Add(new ColumnStatLine("Sum", ColumnStatistics.FormatNumber(stats.Sum!.Value)));
            lines.Add(new ColumnStatLine("Mean", ColumnStatistics.FormatNumber(stats.Mean!.Value)));
        }

        return lines;
    }

    private async void OnColumnSortAscendingClick(object sender, RoutedEventArgs e) => await SortActiveColumnAsync(SortDirection.Ascending);

    private async void OnColumnSortDescendingClick(object sender, RoutedEventArgs e) => await SortActiveColumnAsync(SortDirection.Descending);

    private void OnColumnClearSortClick(object sender, RoutedEventArgs e)
    {
        ColumnFilterPopup.IsOpen = false;
        _viewModel.Grid?.ClearSort();
    }

    private async Task SortActiveColumnAsync(SortDirection direction)
    {
        if (_activeFilterColumn is not { } columnName || _viewModel.Grid is not { } grid) return;

        ColumnFilterPopup.IsOpen = false;
        await grid.SortByColumnAsync(columnName, direction);
    }

    private async void OnColumnPatternBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await ApplyColumnPatternFilterAsync();
    }

    private async void OnColumnPatternApplyClick(object sender, RoutedEventArgs e) => await ApplyColumnPatternFilterAsync();

    /// <summary>Applies (or clears, with an empty box) this column's own text/regex filter.</summary>
    private async Task ApplyColumnPatternFilterAsync()
    {
        if (_activeFilterColumn is not { } columnName || _viewModel.Grid is not { } grid) return;

        string pattern = ColumnPatternBox.Text;
        bool useRegex = ColumnPatternRegexBox.IsChecked == true;

        // An invalid regex matches nothing rather than throwing, so say so instead of letting the
        // grid silently empty itself.
        if (useRegex && pattern.Length > 0 && !RowFilter.TryCompileRegex(pattern, out _, out string? error))
        {
            _viewModel.ReportStatus($"Not a valid regular expression: {error}");
            return;
        }

        ColumnFilterPopup.IsOpen = false;
        await grid.SetColumnPatternFilterAsync(columnName, pattern, useRegex);
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

/// <summary>One parser warning, as the file-info panel lists it.</summary>
public sealed record FileDiagnosticLine(string Headline, string Message);

/// <summary>One label/value line of the column menu's Stats view.</summary>
public sealed record ColumnStatLine(string Label, string Value);

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
