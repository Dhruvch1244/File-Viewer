using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using FileViewer.App.Common;
using FileViewer.App.Logging;
using FileViewer.App.Settings;
using FileViewer.App.Theme;
using FileViewer.Core.Dif;
using FileViewer.Core.Indexing;
using FileViewer.Core.Session;

namespace FileViewer.App.ViewModels;

/// <summary>
/// Top-level view model: owns the open tabs (one per file), which tab and section is on screen, and
/// the open/index progress reporting. <see cref="Grid"/> forwards to whatever grid is currently
/// showing, so the window binds to it exactly as it did when only one file could be open.
///
/// Opening a file is two steps, and only the first is paid for every section: the file's structure
/// is scanned once (cheap for an ordinary file — two bounded windows; one sequential pass for a bulk
/// file, see <see cref="DifSectionScanner"/>), then the section being shown has its rows indexed.
/// Other sections are indexed only if the user opens them.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppSettings _settings = AppSettings.Load();
    private FileTabViewModel? _activeTab;
    private string _statusMessage = "No file open.";
    private double _indexingProgressPercent;
    private bool _isIndexing;
    private bool _isDarkTheme = ThemeManager.Current == AppTheme.Dark;

    public MainViewModel()
    {
        ToggleThemeCommand = RelayCommand.Create(() => ThemeManager.Toggle());
        CloseTabCommand = RelayCommand.Create<FileTabViewModel>(CloseTab);
        OpenRecentFileCommand = new AsyncRelayCommand<string>(OpenFileAsync);

        // Restore the remembered theme before anything is shown, so the app doesn't flash the
        // default one on every launch.
        if (Enum.TryParse(_settings.Theme, out AppTheme savedTheme))
        {
            ThemeManager.Apply(savedTheme);
        }
        IsDarkTheme = ThemeManager.Current == AppTheme.Dark;

        ThemeManager.ThemeChanged += theme =>
        {
            IsDarkTheme = theme == AppTheme.Dark;
            _settings.Theme = theme.ToString();
            _settings.Save();
        };

        RefreshRecentFiles();
    }

    /// <summary>Every open file, in the order they were opened — the tab strip's source.</summary>
    public ObservableCollection<FileTabViewModel> Tabs { get; } = new();

    public FileTabViewModel? ActiveTab
    {
        get => _activeTab;
        private set
        {
            FileTabViewModel? previous = _activeTab;
            if (!SetField(ref _activeTab, value)) return;

            if (previous is not null)
            {
                previous.IsActive = false;
                previous.PropertyChanged -= OnActiveTabPropertyChanged;
            }
            if (value is not null)
            {
                value.IsActive = true;
                value.PropertyChanged += OnActiveTabPropertyChanged;
            }

            OnPropertyChanged(nameof(HasFileOpen));
            OnPropertyChanged(nameof(Grid));
        }
    }

    /// <summary>The grid currently on screen: the active tab's active section's. Null until a file is open.</summary>
    public GridViewModel? Grid => ActiveTab?.Grid;

    /// <summary>Drives the welcome/tutorial screen vs. the data grid — welcome shows until a file is successfully opened.</summary>
    public bool HasFileOpen => Tabs.Count > 0;

    /// <summary>Mirrors <see cref="ThemeManager.Current"/> so the toolbar toggle button's icon/tooltip can bind to it directly.</summary>
    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        private set => SetField(ref _isDarkTheme, value);
    }

    public ICommand ToggleThemeCommand { get; }
    public ICommand CloseTabCommand { get; }

    /// <summary>Opens one of <see cref="RecentFiles"/> — bound with the path as its parameter.</summary>
    public ICommand OpenRecentFileCommand { get; }

    /// <summary>Recently opened files that still exist on disk, newest first.</summary>
    public ObservableCollection<string> RecentFiles { get; } = new();

    public bool HasRecentFiles => RecentFiles.Count > 0;

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public double IndexingProgressPercent
    {
        get => _indexingProgressPercent;
        private set => SetField(ref _indexingProgressPercent, value);
    }

    public bool IsIndexing
    {
        get => _isIndexing;
        private set => SetField(ref _isIndexing, value);
    }

    /// <summary>
    /// Opens <paramref name="path"/> in a new tab (or re-activates the tab it is already open in),
    /// and shows its first section. A file whose structure declares several sections — a bulk export
    /// — is detected here rather than being asked for: see <see cref="DifBulkDetection"/>.
    /// </summary>
    public async Task OpenFileAsync(string path)
    {
        FileTabViewModel? alreadyOpen = Tabs.FirstOrDefault(
            tab => string.Equals(tab.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (alreadyOpen is not null)
        {
            await ActivateTabAsync(alreadyOpen);
            StatusMessage = $"{Path.GetFileName(path)} is already open.";
            return;
        }

        IsIndexing = true;
        IndexingProgressPercent = 0;
        StatusMessage = $"Opening {Path.GetFileName(path)}...";

        try
        {
            DifFileLayout layout = await FileIndexer.ScanLayoutAsync(path);

            if (!layout.IsValid)
            {
                string reason = layout.Diagnostics
                    .FirstOrDefault(d => d.Severity == DifDiagnosticSeverity.Error)?.Message ?? "Unrecognized file format.";
                StatusMessage = $"Could not open '{Path.GetFileName(path)}': {reason}";
                FileLogger.Instance.LogWarning($"Opened '{path}' but it is not a valid DIF file: {reason}");
                return;
            }

            var tab = new FileTabViewModel(path, layout);
            Tabs.Add(tab);
            OnPropertyChanged(nameof(HasFileOpen));

            RetitleTabs();

            _settings.RememberRecentFile(path);
            _settings.Save();
            RefreshRecentFiles();

            await ActivateTabAsync(tab);
            await ShowSectionAsync(tab, tab.Sections[0]);
        }
        catch (Exception ex)
        {
            // Malformed/unreadable files must fail gracefully, never crash the process (PRS §8).
            StatusMessage = $"Failed to open '{Path.GetFileName(path)}': {ex.Message}";
            FileLogger.Instance.LogError($"Failed to open '{path}'.", ex);

            // A path that can no longer be opened (moved, deleted, permissions) shouldn't keep
            // offering itself from the recent list.
            if (!File.Exists(path))
            {
                _settings.ForgetRecentFile(path);
                _settings.Save();
                RefreshRecentFiles();
            }
        }
        finally
        {
            IsIndexing = false;
            IndexingProgressPercent = 0;
        }
    }

    /// <summary>Brings a tab to the front, indexing its active section first if it has never been shown.</summary>
    public async Task ActivateTabAsync(FileTabViewModel tab)
    {
        ActiveTab = tab;

        // A tab whose first section failed to index has no active section yet — fall back to its
        // first, so clicking the tab retries rather than showing nothing.
        FileSectionViewModel? section = tab.ActiveSection ?? tab.Sections.FirstOrDefault();
        if (section is not null && !section.IsLoaded)
        {
            await ShowSectionAsync(tab, section);
        }
    }

    /// <summary>
    /// Switches the tab to one of its sections, indexing that section's rows the first time it is
    /// opened. Sections already visited keep everything they had — their edits, sort, filters and
    /// column layout — since each owns its own session.
    /// </summary>
    public async Task ShowSectionAsync(FileTabViewModel tab, FileSectionViewModel section)
    {
        if (section.IsLoaded)
        {
            tab.ActiveSection = section;
            if (ReferenceEquals(tab, ActiveTab)) OnPropertyChanged(nameof(Grid));
            StatusMessage = DescribeLoadedSection(tab, section);
            return;
        }

        IsIndexing = true;
        IndexingProgressPercent = 0;
        StatusMessage = tab.HasMultipleSections
            ? $"Indexing section '{section.Name}' of {tab.Title}..."
            : $"Indexing {tab.Title}...";

        try
        {
            var progress = new Progress<IndexingProgress>(p =>
            {
                IndexingProgressPercent = p.TotalBytes > 0 ? 100.0 * p.BytesScanned / p.TotalBytes : 0;
                StatusMessage = $"Indexing... {p.RowsFound:N0} row(s) found";
            });

            FileViewerSession session = await FileViewerSession.OpenSectionAsync(
                tab.FilePath, tab.Layout, section.Index, progress: progress);

            // The tab can be closed while its section is still being indexed; handing the finished
            // session to a disposed tab would leak the file handle and show a grid for a file that
            // is no longer open.
            if (!Tabs.Contains(tab))
            {
                session.Dispose();
                return;
            }

            section.Attach(session);
            tab.ActiveSection = section;
            tab.NotifyGridChanged();
            if (ReferenceEquals(tab, ActiveTab)) OnPropertyChanged(nameof(Grid));

            StatusMessage = DescribeLoadedSection(tab, section);
            FileLogger.Instance.LogInfo(
                $"Opened '{tab.FilePath}' section '{section.Name}': {session.FileIndex.RowIndex.Count:N0} row(s).");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to open section '{section.Name}' of '{tab.Title}': {ex.Message}";
            FileLogger.Instance.LogError($"Failed to open section '{section.Name}' of '{tab.FilePath}'.", ex);
        }
        finally
        {
            IsIndexing = false;
            IndexingProgressPercent = 0;
        }
    }

    /// <summary>
    /// Gives every tab the shortest label that still identifies it: the file name on its own, or
    /// "folder\\name" when another open tab has the same file name.
    /// </summary>
    private void RetitleTabs()
    {
        foreach (FileTabViewModel tab in Tabs)
        {
            bool nameIsAmbiguous = Tabs.Any(other =>
                !ReferenceEquals(other, tab) && string.Equals(other.FileName, tab.FileName, StringComparison.OrdinalIgnoreCase));

            tab.Title = nameIsAmbiguous && tab.ParentFolderName.Length > 0
                ? Path.Combine(tab.ParentFolderName, tab.FileName)
                : tab.FileName;
        }
    }

    /// <summary>Re-reads the recent list, dropping anything that no longer exists on disk.</summary>
    private void RefreshRecentFiles()
    {
        RecentFiles.Clear();
        foreach (string path in _settings.ExistingRecentFiles())
        {
            RecentFiles.Add(path);
        }
        OnPropertyChanged(nameof(HasRecentFiles));
    }

    /// <summary>Moves to the next (or previous) open tab, wrapping around — Ctrl+Tab / Ctrl+Shift+Tab.</summary>
    public async Task CycleTabAsync(int offset)
    {
        if (Tabs.Count < 2 || ActiveTab is null) return;

        int index = Tabs.IndexOf(ActiveTab);
        int next = ((index + offset) % Tabs.Count + Tabs.Count) % Tabs.Count;
        await ActivateTabAsync(Tabs[next]);
    }

    /// <summary>Closes whichever tab is on screen — Ctrl+W.</summary>
    public void CloseActiveTab() => CloseTab(ActiveTab);

    /// <summary>Closes a tab, disposing every section it had indexed, and falls back to the neighbouring tab.</summary>
    public void CloseTab(FileTabViewModel? tab)
    {
        if (tab is null) return;

        int closedIndex = Tabs.IndexOf(tab);
        Tabs.Remove(tab);

        if (ReferenceEquals(tab, ActiveTab))
        {
            ActiveTab = Tabs.Count == 0 ? null : Tabs[Math.Clamp(closedIndex, 0, Tabs.Count - 1)];
        }

        tab.Dispose();
        RetitleTabs();
        OnPropertyChanged(nameof(HasFileOpen));
        OnPropertyChanged(nameof(Grid));

        if (Tabs.Count == 0) StatusMessage = "No file open.";
    }

    private string DescribeLoadedSection(FileTabViewModel tab, FileSectionViewModel section)
    {
        if (section.Grid is not { } grid) return StatusMessage;

        long rowCount = (long)grid.Session.FileIndex.RowIndex.Count;
        int warningCount = grid.Session.FileIndex.Diagnostics.Count(d => d.Severity == DifDiagnosticSeverity.Warning);
        string where = tab.HasMultipleSections ? $"{tab.Title} · {section.Name}" : tab.Title;
        string warnings = warningCount == 0 ? string.Empty : $" ({warningCount:N0} warning(s) — see diagnostics)";
        return $"Loaded {rowCount:N0} row(s) from {where}.{warnings}";
    }

    private void OnActiveTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileTabViewModel.Grid))
        {
            OnPropertyChanged(nameof(Grid));
        }
    }

    public void Dispose()
    {
        foreach (FileTabViewModel tab in Tabs)
        {
            tab.Dispose();
        }
        Tabs.Clear();
    }
}
