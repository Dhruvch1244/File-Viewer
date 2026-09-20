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
        _session?.Dispose();
        _session = session;
        Grid = new GridViewModel(session, preferences);
    }

    public void Dispose()
    {
        Grid?.Rows.Dispose();
        _session?.Dispose();
        _session = null;
        Grid = null;
    }
}
