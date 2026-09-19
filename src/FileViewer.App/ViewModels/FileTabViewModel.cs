using System.Collections.ObjectModel;
using System.IO;
using FileViewer.App.Common;
using FileViewer.Core.Dif;

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

    public FileTabViewModel(string filePath, DifFileLayout layout)
    {
        FilePath = filePath;
        Layout = layout;
        Title = Path.GetFileName(filePath);
        Sections = new ObservableCollection<FileSectionViewModel>(layout.Sections.Select(section => new FileSectionViewModel(section)));
    }

    public string FilePath { get; }

    /// <summary>File name only — the tab's label. The full path is the tooltip.</summary>
    public string Title { get; }

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
