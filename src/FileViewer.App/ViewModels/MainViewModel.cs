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
    private CancellationTokenSource? _indexingCts;
    private readonly Action<AppTheme> _themeChangedHandler;
    private FileTabViewModel? _activeTab;
    private string _statusMessage = "No file open.";
    private double _indexingProgressPercent;
    private bool _isIndexing;
    private bool _isDarkTheme = ThemeManager.Current == AppTheme.Dark;

    public MainViewModel()
    {
        ToggleThemeCommand = RelayCommand.Create(() => ThemeManager.Toggle());
        CancelIndexingCommand = RelayCommand.Create(CancelIndexing, () => IsIndexing);
        CloseTabCommand = RelayCommand.Create<FileTabViewModel>(CloseTab);
        OpenRecentFileCommand = new AsyncRelayCommand<string>(OpenFileAsync);

        // Restore the remembered theme before anything is shown, so the app doesn't flash the
        // default one on every launch.
        if (Enum.TryParse(_settings.Theme, out AppTheme savedTheme))
        {
            ThemeManager.Apply(savedTheme);
        }
        IsDarkTheme = ThemeManager.Current == AppTheme.Dark;

        // Held in a field so it can come off again: ThemeManager's event is static, and with more
        // than one window open a handler that is never removed keeps a closed window's view model
        // alive for the life of the process.
        _themeChangedHandler = theme =>
        {
            IsDarkTheme = theme == AppTheme.Dark;
            _settings.Theme = theme.ToString();
            _settings.Save();
        };
        ThemeManager.ThemeChanged += _themeChangedHandler;

        Preferences.PageSize = PageSizeOption.FromSetting(_settings.RowsPerPage);
        Preferences.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(GridPreferences.PageSize)) return;
            _settings.RowsPerPage = Preferences.PageSize.ToSetting();
            _settings.Save();
        };

        RefreshRecentFiles();
    }

    /// <summary>Grid settings shared by every open tab and section — currently how many rows a page shows. See <see cref="GridPreferences"/>.</summary>
    public GridPreferences Preferences { get; } = new();

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

    /// <summary>
    /// Stops the index or structure scan in progress. Opening a multi-gigabyte file by mistake used
    /// to mean waiting it out — the work was always cancellable, nothing ever asked it to stop.
    /// </summary>
    public ICommand CancelIndexingCommand { get; }
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

    /// <summary>Puts a one-off message in the status bar — used for things the window does directly, like copying to the clipboard.</summary>
    public void ReportStatus(string message) => StatusMessage = message;

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
        CancellationToken cancellationToken = BeginCancellableWork();

        try
        {
            DifFileLayout layout = await FileIndexer.ScanLayoutAsync(path, cancellationToken);

            if (!layout.IsValid)
            {
                string reason = layout.Diagnostics
                    .FirstOrDefault(d => d.Severity == DifDiagnosticSeverity.Error)?.Message ?? "Unrecognized file format.";
                StatusMessage = $"Could not open '{Path.GetFileName(path)}': {reason}";
                FileLogger.Instance.LogWarning($"Opened '{path}' but it is not a valid DIF file: {reason}");
                return;
            }

            var tab = new FileTabViewModel(path, layout, Preferences);
            Tabs.Add(tab);
            OnPropertyChanged(nameof(HasFileOpen));

            RetitleTabs();

            _settings.RememberRecentFile(path);
            _settings.Save();
            RefreshRecentFiles();

            await ActivateTabAsync(tab);
            await ShowSectionAsync(tab, tab.Sections[0]);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"Cancelled opening '{Path.GetFileName(path)}'.";
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
            EndCancellableWork();
        }
    }

    /// <summary>
    /// Splits a bulk file's tab into one tab per section, so every section is on screen at once
    /// rather than a click away behind the section bar.
    ///
    /// The original tab is closed as part of the split, deliberately: leaving it open would mean the
    /// same section existed in two tabs, each with its own edit overlay, and an edit made in one
    /// would be invisible in the other while both claimed to be the same rows of the same file.
    /// Sections still index lazily — only the tab that ends up in front is read now.
    /// </summary>
    public async Task OpenAllSectionsAsTabsAsync(FileTabViewModel tab)
    {
        if (!tab.HasMultipleSections) return;

        string fileName = tab.FileName;
        string filePath = tab.FilePath;
        DifFileLayout layout = tab.Layout;
        int insertAt = Tabs.IndexOf(tab);

        // Edits live in a section's session, so move the sessions across rather than letting the
        // close drop them: a split keeps whatever has been edited instead of offering to throw it
        // away, and a section already read is not read again. Sections never opened carry nothing
        // and stay lazy.
        var carried = new Dictionary<int, FileViewerSession>();
        foreach (FileSectionViewModel loaded in tab.Sections)
        {
            if (loaded.DetachSession() is { } session) carried[loaded.Index] = session;
        }

        // With the sessions moved out, there are no unexported edits left here to warn about, so
        // this closes without a prompt. CloseTab still owns that question for every other caller.
        CloseTab(tab);
        if (Tabs.Contains(tab))
        {
            // Declined (it can still happen via an extract of this tab). Put the sessions back so
            // the tab is exactly as it was rather than silently emptied.
            foreach ((int index, FileViewerSession session) in carried)
            {
                tab.Sections.First(s => s.Index == index).Attach(session);
            }
            return;
        }

        var created = new List<FileTabViewModel>(layout.Sections.Count);
        for (int i = 0; i < layout.Sections.Count; i++)
        {
            var sectionTab = new FileTabViewModel(filePath, layout, Preferences, singleSection: i);
            if (carried.Remove(i, out FileViewerSession? session))
            {
                sectionTab.Sections[0].Attach(session);
                sectionTab.ActiveSection = sectionTab.Sections[0];
            }
            Tabs.Insert(Math.Clamp(insertAt, 0, Tabs.Count) + i, sectionTab);
            created.Add(sectionTab);
        }

        RetitleTabs();
        OnPropertyChanged(nameof(HasFileOpen));

        await ActivateTabAsync(created[0]);
        await ShowSectionAsync(created[0], created[0].Sections[0]);
        StatusMessage = $"Opened {created.Count} sections of {fileName} as separate tabs.";
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
        CancellationToken cancellationToken = BeginCancellableWork();

        try
        {
            var progress = new Progress<IndexingProgress>(p =>
            {
                IndexingProgressPercent = p.TotalBytes > 0 ? 100.0 * p.BytesScanned / p.TotalBytes : 0;
                StatusMessage = $"Indexing... {p.RowsFound:N0} row(s) found";
            });

            FileViewerSession session = await FileViewerSession.OpenSectionAsync(
                tab.FilePath, tab.Layout, section.Index, progress: progress, cancellationToken: cancellationToken);

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
        catch (OperationCanceledException)
        {
            StatusMessage = $"Cancelled indexing '{section.Name}'.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to open section '{section.Name}' of '{tab.Title}': {ex.Message}";
            FileLogger.Instance.LogError($"Failed to open section '{section.Name}' of '{tab.FilePath}'.", ex);
        }
        finally
        {
            EndCancellableWork();
        }
    }

    private CancellationToken BeginCancellableWork()
    {
        _indexingCts?.Cancel();
        _indexingCts?.Dispose();
        _indexingCts = new CancellationTokenSource();
        CommandManager.InvalidateRequerySuggested();
        return _indexingCts.Token;
    }

    private void EndCancellableWork()
    {
        IsIndexing = false;
        IndexingProgressPercent = 0;
        _indexingCts?.Dispose();
        _indexingCts = null;
        CommandManager.InvalidateRequerySuggested();
    }

    private void CancelIndexing()
    {
        _indexingCts?.Cancel();
        StatusMessage = "Cancelling…";
    }

    /// <summary>
    /// Gives every tab the shortest label that still identifies it: the file name on its own, or
    /// "folder\\name" when another open tab has the same file name.
    /// </summary>
    private void RetitleTabs()
    {
        foreach (FileTabViewModel tab in Tabs)
        {
            if (tab.IsExtract) continue; // its title already says what it is

            // Only a *different* file sharing the name needs the folder to tell it apart. Several
            // tabs on the same path are sections of one bulk file, and the section name below
            // already distinguishes them — prefixing all of them with the folder says nothing.
            bool nameIsAmbiguous = Tabs.Any(other =>
                !ReferenceEquals(other, tab)
                && string.Equals(other.FileName, tab.FileName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(other.FilePath, tab.FilePath, StringComparison.OrdinalIgnoreCase));

            string baseName = nameIsAmbiguous && tab.ParentFolderName.Length > 0
                ? Path.Combine(tab.ParentFolderName, tab.FileName)
                : tab.FileName;

            tab.Title = tab.PinnedSectionName is { Length: > 0 } section ? $"{baseName} · {section}" : baseName;
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

    /// <summary>
    /// Pulls the selected rows out into their own view: same file, same columns, same edits, just
    /// those rows — with its own filters, search, sort and column layout. The point is to be able to
    /// narrow to a set of records and then keep working on them without the rest of the file getting
    /// in the way, including narrowing again inside it.
    ///
    /// It shares the source view's index and overlay rather than re-indexing the file, which is what
    /// makes it instant; the cost is that it closes when the file it came from does.
    /// </summary>
    public async Task ExtractSelectionAsync()
    {
        if (ActiveTab is not { } tab || tab.ActiveSection is not { Grid: { } grid } section) return;

        long[] rows = [.. grid.Selection.Resolve(grid.Rows.GetAllRowIndices())];
        if (rows.Length == 0)
        {
            StatusMessage = "Nothing selected — tick the rows you want first.";
            return;
        }

        FileTabViewModel extract = FileTabViewModel.CreateExtract(tab, section, grid.Session, rows, Preferences);
        Tabs.Add(extract);
        OnPropertyChanged(nameof(HasFileOpen));

        await ActivateTabAsync(extract);
        StatusMessage = $"Pulled {rows.Length:N0} row(s) into their own view.";
    }

    /// <summary>Closes whichever tab is on screen — Ctrl+W.</summary>
    public void CloseActiveTab() => CloseTab(ActiveTab);

    /// <summary>
    /// Asked before a tab holding unexported edits is closed; returning false cancels the close.
    /// Supplied by the window (this is where a dialog belongs), left null in tests, where closing
    /// simply proceeds.
    /// </summary>
    public Func<FileTabViewModel, bool>? ConfirmDiscardingEdits { get; set; }

    /// <summary>True if any open file holds edits that exist only in memory — what the window checks before letting itself close.</summary>
    public bool HasUnsavedEdits => Tabs.Any(tab => tab.HasUnsavedEdits);

    /// <summary>Closes a tab, disposing every section it had indexed, and falls back to the neighbouring tab.</summary>
    public void CloseTab(FileTabViewModel? tab)
    {
        if (tab is null) return;

        // Edits live in the overlay until they are exported, so closing throws them away. Say so
        // rather than doing it silently.
        if (tab.HasUnsavedEdits && ConfirmDiscardingEdits?.Invoke(tab) == false) return;

        // Views pulled out of this one read through its session, so they cannot outlive it.
        foreach (FileTabViewModel extract in tab.Extracts.ToList())
        {
            CloseTab(extract);
        }
        tab.ExtractedFrom?.Extracts.Remove(tab);

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
        ThemeManager.ThemeChanged -= _themeChangedHandler;
        _indexingCts?.Cancel();
        _indexingCts?.Dispose();
        _indexingCts = null;

        // Extracted views share the session of the tab they came from, so they have to go first —
        // disposing a source while one still reads through it would leave it pointing at a closed file.
        foreach (FileTabViewModel tab in Tabs.OrderByDescending(tab => tab.IsExtract))
        {
            tab.Dispose();
        }
        Tabs.Clear();
    }
}
