using System.Collections.ObjectModel;
using System.IO;
using FileViewer.App.Common;
using FileViewer.Core.Dif;
using FileViewer.Core.Session;

namespace FileViewer.App.ViewModels;

/// <summary>
/// One open file, as a tab. Owns that file's scanned <see cref="DifFileLayout"/> and one
/// <see cref="FileSectionViewModel"/> per data section — so a bulk file opens as a single tab with a
/// section bar inside it, rather than as a handful of unrelated tabs the user has to keep straight.
///
/// Tabs are independent: each has its own sessions, edits, sort, filters and column layout, and
/// closing one disposes exactly that file's state.
/// </summary>
public sealed class FileTabViewModel : ObservableObject, IDisposable
{
    private FileSectionViewModel? _activeSection;
    private bool _isActive;
    private string _title;

    /// <summary>
    /// Builds a tab holding rows pulled out of <paramref name="source"/>: the same file and columns,
    /// a fixed set of rows, and its own filters, search, sort and column layout. It shares the
    /// source's index and edit overlay — an edit here is an edit to the same file — so it lives only
    /// as long as the view it came from.
    /// </summary>
    public static FileTabViewModel CreateExtract(
        FileTabViewModel source,
        FileSectionViewModel sourceSection,
        FileViewerSession sharedSession,
        IReadOnlyList<long> rows,
        GridPreferences preferences)
    {
        var tab = new FileTabViewModel(source.FilePath, source.Layout, preferences, singleSection: sourceSection.Index)
        {
            ExtractedFrom = source,
            _title = $"{Path.GetFileName(source.FilePath)} · {rows.Count:N0} row(s)",
        };

        tab.Sections[0].AttachExtractedRows(sharedSession, rows);
        tab.ActiveSection = tab.Sections[0];
        source.Extracts.Add(tab);
        return tab;
    }

    public FileTabViewModel(string filePath, DifFileLayout layout, GridPreferences preferences, int? singleSection = null)
    {
        FilePath = filePath;
        Layout = layout;
        _title = Path.GetFileName(filePath);
        IEnumerable<DifSection> sections = singleSection is int only
            ? [layout.Sections[only]]
            : layout.Sections;
        Sections = new ObservableCollection<FileSectionViewModel>(sections.Select(section => new FileSectionViewModel(section, preferences)));
    }

    public string FilePath { get; }

    /// <summary>
    /// The tab's label: the file name, or "folder\\name" when another open tab has the same file
    /// name. Dated exports routinely share a name and differ only by folder, and two tabs reading
    /// "holdings.dif" tell you nothing. The full path is always the tooltip.
    /// </summary>
    public string Title
    {
        get => _title;
        internal set => SetField(ref _title, value);
    }

    public DifFileLayout Layout { get; }

    public ObservableCollection<FileSectionViewModel> Sections { get; }

    /// <summary>True for a bulk file: what makes the section bar appear at all.</summary>
    public bool HasMultipleSections => Sections.Count > 1;

    /// <summary>Tab subtitle for a bulk file — e.g. "3 sections (bulk)".</summary>
    public string SectionSummary => HasMultipleSections ? $"{Sections.Count} sections (bulk)" : string.Empty;

    public FileSectionViewModel? ActiveSection
    {
        get => _activeSection;
        internal set
        {
            FileSectionViewModel? previous = _activeSection;
            if (!SetField(ref _activeSection, value)) return;

            if (previous is not null) previous.IsActive = false;
            if (value is not null) value.IsActive = true;
            OnPropertyChanged(nameof(Grid));
        }
    }

    /// <summary>Whether this is the tab currently on screen — drives the tab strip's selected styling.</summary>
    public bool IsActive
    {
        get => _isActive;
        internal set => SetField(ref _isActive, value);
    }

    public GridViewModel? Grid => ActiveSection?.Grid;

    /// <summary>
    /// True if any section of this file holds edits that have not been exported anywhere. Always
    /// false for an extracted view: its edits belong to the file it was pulled out of and survive
    /// its closing, so there is nothing to warn about.
    /// </summary>
    public bool HasUnsavedEdits =>
        !IsExtract && Sections.Any(section => section.Grid?.Session.HasPendingEdits == true);

    /// <summary>The tab this one's rows were pulled out of, if any.</summary>
    public FileTabViewModel? ExtractedFrom { get; private init; }

    public bool IsExtract => ExtractedFrom is not null;

    /// <summary>Views pulled out of this one. They share this tab's session, so they close when it does.</summary>
    public List<FileTabViewModel> Extracts { get; } = [];

    /// <summary>The file name alone, and the folder holding it — the two halves a disambiguated title is built from.</summary>
    internal string FileName => Path.GetFileName(FilePath);

    internal string ParentFolderName => Path.GetFileName(Path.GetDirectoryName(FilePath) ?? string.Empty);

    /// <summary>Raised when the active section finishes loading, so the window can rebuild its columns for the new grid.</summary>
    internal void NotifyGridChanged() => OnPropertyChanged(nameof(Grid));

    public void Dispose()
    {
        foreach (FileSectionViewModel section in Sections)
        {
            section.Dispose();
        }
    }
}
