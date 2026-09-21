using System.ComponentModel;
using System.Windows;
using FileViewer.App.ViewModels;
using FileViewer.Core.Diffing;
using FileViewer.Core.Session;

namespace FileViewer.App.Views;

/// <summary>
/// Compares the section currently on screen against another currently-open section — possibly of a
/// different file, possibly another section of a bulk file — matched by a key column rather than by
/// row position (see <see cref="RowDiffEngine"/> for why). Results are shown as counts plus three
/// buttons that open the differing rows as ordinary, fully-interactive extracted views: this dialog
/// itself never tries to render millions of rows, the grid the app already has does that.
/// </summary>
public partial class CompareFilesDialogView : Window, INotifyPropertyChanged
{
    private readonly MainViewModel _mainViewModel;
    private readonly FileTabViewModel _currentTab;
    private readonly FileSectionViewModel _currentSection;
    private readonly FileViewerSession _currentSession;

    private CompareTarget? _selectedTarget;
    private string? _selectedKeyColumn;
    private bool _isComparing;
    private RowDiffResult? _result;
    private string _resultSummary = string.Empty;
    private string _warningText = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public CompareFilesDialogView(MainViewModel mainViewModel, FileTabViewModel currentTab, FileSectionViewModel currentSection)
    {
        _mainViewModel = mainViewModel;
        _currentTab = currentTab;
        _currentSection = currentSection;
        _currentSession = currentSection.Grid!.Session;

        foreach (FileTabViewModel tab in mainViewModel.Tabs)
        {
            if (tab.IsExtract) continue; // a fixed subset of another section — not a useful comparison side
            foreach (FileSectionViewModel section in tab.Sections)
            {
                if (ReferenceEquals(tab, currentTab) && ReferenceEquals(section, currentSection)) continue;
                if (section.Grid is null) continue; // never opened — nothing to compare against yet

                CompareTargets.Add(new CompareTarget(tab, section));
            }
        }
        SelectedTarget = CompareTargets.FirstOrDefault();

        foreach (string column in _currentSession.FileIndex.Header.ColumnNames)
        {
            KeyColumnOptions.Add(column);
        }
        SelectedKeyColumn = KeyColumnOptions.Contains("_ID") ? "_ID" : KeyColumnOptions.FirstOrDefault();

        DataContext = this;
        InitializeComponent();
    }

    public System.Collections.ObjectModel.ObservableCollection<CompareTarget> CompareTargets { get; } = new();
    public System.Collections.ObjectModel.ObservableCollection<string> KeyColumnOptions { get; } = new();

    public CompareTarget? SelectedTarget
    {
        get => _selectedTarget;
        set { _selectedTarget = value; OnPropertyChanged(nameof(SelectedTarget)); OnPropertyChanged(nameof(CanCompare)); }
    }

    public string? SelectedKeyColumn
    {
        get => _selectedKeyColumn;
        set { _selectedKeyColumn = value; OnPropertyChanged(nameof(SelectedKeyColumn)); OnPropertyChanged(nameof(CanCompare)); }
    }

    public bool CanCompare => !IsComparing && SelectedTarget is not null && !string.IsNullOrEmpty(SelectedKeyColumn) && CompareTargets.Count > 0;

    public bool HasNoTargets => CompareTargets.Count == 0;

    public bool IsComparing
    {
        get => _isComparing;
        private set { _isComparing = value; OnPropertyChanged(nameof(IsComparing)); OnPropertyChanged(nameof(CanCompare)); }
    }

    public bool HasResult => _result is not null;

    public string ResultSummary
    {
        get => _resultSummary;
        private set { _resultSummary = value; OnPropertyChanged(nameof(ResultSummary)); }
    }

    public string WarningText
    {
        get => _warningText;
        private set { _warningText = value; OnPropertyChanged(nameof(WarningText)); OnPropertyChanged(nameof(HasWarning)); }
    }

    public bool HasWarning => WarningText.Length > 0;

    public bool CanViewAdded => _result is { AddedCount: > 0 };
    public bool CanViewRemoved => _result is { RemovedCount: > 0 };
    public bool CanViewChanged => _result is { ChangedCount: > 0 };

    private async void OnCompareClick(object sender, RoutedEventArgs e)
    {
        if (SelectedTarget is not { } target || SelectedKeyColumn is not { } keyColumn) return;

        IsComparing = true;
        _result = null;
        OnPropertyChanged(nameof(HasResult));
        ResultSummary = string.Empty;
        WarningText = string.Empty;

        try
        {
            FileViewerSession rightSession = target.Section.Grid!.Session;
            RowDiffResult result = await Task.Run(() => RowDiffEngine.Compare(
                _currentSession.FileIndex, _currentSession.Overlay, _currentSession.Cache,
                rightSession.FileIndex, rightSession.Overlay, rightSession.Cache,
                keyColumn));

            _result = result;
            OnPropertyChanged(nameof(HasResult));
            OnPropertyChanged(nameof(CanViewAdded));
            OnPropertyChanged(nameof(CanViewRemoved));
            OnPropertyChanged(nameof(CanViewChanged));

            ResultSummary = result.IsIdentical
                ? $"Identical on every compared column — {result.UnchangedCount:N0} row(s) matched."
                : $"{result.AddedCount:N0} added, {result.RemovedCount:N0} removed, {result.ChangedCount:N0} changed, {result.UnchangedCount:N0} unchanged.";

            var warnings = new List<string>();
            if (result.KeyColumnMissingOnLeft || result.KeyColumnMissingOnRight)
            {
                warnings.Add($"'{keyColumn}' is missing from {(result.KeyColumnMissingOnLeft && result.KeyColumnMissingOnRight ? "both sides" : result.KeyColumnMissingOnLeft ? "this section" : "the other section")} — nothing was compared.");
            }
            if (result.DuplicateKeysOnLeft || result.DuplicateKeysOnRight)
            {
                warnings.Add($"'{keyColumn}' has duplicate values on {(result.DuplicateKeysOnLeft && result.DuplicateKeysOnRight ? "both sides" : result.DuplicateKeysOnLeft ? "this section" : "the other section")} — matching used the last row for each repeated value, so this comparison may not be reliable.");
            }
            WarningText = string.Join(" ", warnings);
        }
        finally
        {
            IsComparing = false;
        }
    }

    private async void OnViewAddedClick(object sender, RoutedEventArgs e) => await ViewEntriesAsync(RowDiffKind.Added, "added row(s)");

    private async void OnViewRemovedClick(object sender, RoutedEventArgs e) => await ViewEntriesAsync(RowDiffKind.Removed, "removed row(s)");

    private async void OnViewChangedClick(object sender, RoutedEventArgs e) => await ViewEntriesAsync(RowDiffKind.Changed, "changed row(s)");

    /// <summary>
    /// Opens the rows of one diff category as an extracted view — added/changed rows come from the
    /// comparison's own side (right for added, left for removed/changed: a removed row only exists
    /// on the left, and "changed" shows the left's current values, matching what editing/exporting
    /// from here would act on).
    /// </summary>
    private async Task ViewEntriesAsync(RowDiffKind kind, string label)
    {
        if (_result is not { } result || SelectedTarget is not { } target) return;

        bool useRight = kind == RowDiffKind.Added;
        long[] rows = [.. result.Entries
            .Where(entry => entry.Kind == kind)
            .Select(entry => useRight ? entry.RightRowIndex : entry.LeftRowIndex)
            .Where(rowIndex => rowIndex.HasValue)
            .Select(rowIndex => rowIndex!.Value)];

        if (rows.Length == 0) return;

        FileTabViewModel sourceTab = useRight ? target.Tab : _currentTab;
        FileSectionViewModel sourceSection = useRight ? target.Section : _currentSection;
        FileViewerSession session = useRight ? target.Section.Grid!.Session : _currentSession;
        string vsName = useRight ? _currentTab.FileName : target.Tab.FileName;

        await _mainViewModel.ShowExtractedRowsAsync(sourceTab, sourceSection, session, rows, $"{label} vs {vsName}");
        DialogResult = true;
        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>One candidate "other side" to compare against — an open, loaded section that isn't the one already on screen.</summary>
    public sealed record CompareTarget(FileTabViewModel Tab, FileSectionViewModel Section)
    {
        public string DisplayName => Tab.HasMultipleSections ? $"{Tab.Title} · {Section.Name}" : Tab.Title;
    }
}
