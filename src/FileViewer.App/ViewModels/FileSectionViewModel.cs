using FileViewer.App.Common;
using FileViewer.Core.Dif;
using FileViewer.Core.Session;

namespace FileViewer.App.ViewModels;

/// <summary>
/// One data section of an open file — the whole file for an ordinary export, one <c>DATA=</c> block
/// for a bulk one. Each section has its own <see cref="FileViewerSession"/>, and therefore its own
/// row index, column list, edit overlay, sort and filters: they are genuinely different tables that
/// happen to share a file.
///
/// A section is indexed lazily, the first time it is opened. A bulk file with ten sections costs one
/// structural scan plus the rows of whichever sections are actually looked at, rather than indexing
/// all ten up front to show one.
/// </summary>
public sealed class FileSectionViewModel(DifSection section, GridPreferences preferences) : ObservableObject, IDisposable
{
    private FileViewerSession? _session;
    private GridViewModel? _grid;
    private bool _isActive;
    private bool _ownsSession = true;

    public int Index => section.Index;

    public string Name => section.Name;

    /// <summary>What the section chip shows: the section's name plus its declared record count, when the file states one.</summary>
    public string DisplayLabel => section.DeclaredDataRecords is int records
        ? $"{Name}  ({records:N0})"
        : Name;

    /// <summary>Number of columns this section declares — sections of a bulk file rarely have the same shape.</summary>
    public int ColumnCount => section.ColumnNames.Count;

    public bool IsLoaded => _grid is not null;

    public bool IsActive
    {
        get => _isActive;
        internal set => SetField(ref _isActive, value);
    }

    public GridViewModel? Grid
    {
        get => _grid;
        private set
        {
            if (SetField(ref _grid, value)) OnPropertyChanged(nameof(IsLoaded));
        }
    }

    /// <summary>Takes ownership of the session indexed for this section and builds its grid.</summary>
    internal void Attach(FileViewerSession session)
    {
        if (_ownsSession) _session?.Dispose();
        _session = session;
        _ownsSession = true;
        Grid = new GridViewModel(session, preferences);
    }

    /// <summary>
    /// Hands this section's indexed session to another tab and gives up ownership, so disposing
    /// this one leaves the session alone. Used when a bulk file's tab is split into one tab per
    /// section: the session is what carries the edit overlay, so moving it rather than dropping it
    /// and re-indexing is what keeps edits across the split — and saves re-reading a section that
    /// has already been read. Returns null if this section was never opened, or does not own its
    /// session (an extracted view, which borrows one).
    /// </summary>
    internal FileViewerSession? DetachSession()
    {
        if (_session is null || !_ownsSession) return null;

        FileViewerSession session = _session;
        _ownsSession = false;
        Grid?.Rows.Dispose();
        Grid = null;
        _session = null;
        return session;
    }

    /// <summary>
    /// Builds a section over rows pulled out of another view: the same file, the same columns, the
    /// same edits — a fixed subset of the rows. The session belongs to the view it came from and is
    /// deliberately not disposed here; the extracted view is closed when that one is.
    /// </summary>
    internal void AttachExtractedRows(FileViewerSession sharedSession, IReadOnlyList<long> rows)
    {
        if (_ownsSession) _session?.Dispose();
        _session = sharedSession;
        _ownsSession = false;
        Grid = new GridViewModel(sharedSession, preferences, rows);
    }

    public void Dispose()
    {
        Grid?.Rows.Dispose();
        if (_ownsSession) _session?.Dispose();
        _session = null;
        Grid = null;
    }
}
