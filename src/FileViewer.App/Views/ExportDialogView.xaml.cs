using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using FileViewer.App.ViewModels;
using FileViewer.Core.Export;
using FileViewer.Core.Session;
using Microsoft.Win32;

namespace FileViewer.App.Views;

/// <summary>
/// Format picker, scope picker and live 2-row preview, reusing <see cref="FileViewerSession.GeneratePreview(IRowExporter, IEnumerable{long})"/>
/// so the preview can never drift from what the real export (also via the session) produces. Also
/// suggests a Bloomberg-style output file name — "{baseName}.{formatToken}.{yyyyMMdd}" (see
/// <see cref="ExportFileNaming"/>) — built from the source file's name and a user-editable date, so
/// the default <see cref="SaveFileDialog"/> file name matches how these files are actually named in
/// the wild instead of a plain "*.csv" with no date.
///
/// Scope matters as much as format here. The dialog exports the rows the grid is actually showing —
/// its filters and sort applied — rather than the whole section, because a user who has just
/// narrowed 2 million rows to 40 means those 40. The other two scopes are the ticked rows only, and
/// (for a bulk file) every section at once, one file per section.
/// </summary>
public partial class ExportDialogView : Window, INotifyPropertyChanged
{
    private readonly GridViewModel _grid;
    private readonly FileTabViewModel? _tab;
    private ExportFormatKind _format = ExportFormatKind.Csv;
    private string _difFormatToken = "dif";
    private DateTime _selectedDate = DateTime.Today;
    private string _previewText = string.Empty;
    private ExportScope _scope = ExportScope.CurrentView;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <param name="grid">The grid being exported — its filters, sort and selection define what "export" means.</param>
    /// <param name="tab">The file the grid belongs to, when known; required for exporting every section of a bulk file.</param>
    public ExportDialogView(GridViewModel grid, FileTabViewModel? tab = null)
    {
        _grid = grid;
        _tab = tab;
        DataContext = this;
        InitializeComponent();
        RefreshPreview();
    }

    private FileViewerSession Session => _grid.Session;

    public bool IsDifSelected { get => _format == ExportFormatKind.Dif; set { if (value) SetFormat(ExportFormatKind.Dif); } }
    public bool IsCsvSelected { get => _format == ExportFormatKind.Csv; set { if (value) SetFormat(ExportFormatKind.Csv); } }
    public bool IsTsvSelected { get => _format == ExportFormatKind.Tsv; set { if (value) SetFormat(ExportFormatKind.Tsv); } }
    public bool IsJsonSelected { get => _format == ExportFormatKind.Json; set { if (value) SetFormat(ExportFormatKind.Json); } }

    public bool IsCurrentViewScope { get => _scope == ExportScope.CurrentView; set { if (value) SetScope(ExportScope.CurrentView); } }
    public bool IsSelectionScope { get => _scope == ExportScope.SelectedRows; set { if (value) SetScope(ExportScope.SelectedRows); } }
    public bool IsAllSectionsScope { get => _scope == ExportScope.AllSections; set { if (value) SetScope(ExportScope.AllSections); } }

    /// <summary>Label for the "what gets written" radio buttons, so the counts are visible before choosing.</summary>
    public string CurrentViewScopeLabel => $"Rows in view ({_grid.Rows.TotalRowCount:N0})";

    public string SelectionScopeLabel => $"Selected rows only ({_grid.SelectedCount:N0})";

    public string AllSectionsScopeLabel => _tab is null ? "All sections" : $"Every section ({_tab.Sections.Count}), one file each";

    /// <summary>Only a bulk file has more than one section, so the all-sections choice is only offered for one.</summary>
    public bool ShowAllSectionsScope => _tab is { HasMultipleSections: true };

    public bool IsSelectionScopeEnabled => _grid.SelectedCount > 0;

    /// <summary>Only DIF has a real-world ".dif" vs ".out" ambiguity (both are genuine Bloomberg conventions); the other formats have one obvious token (csv/tsv/json), so this only matters — and is only shown — while DIF is selected.</summary>
    public bool ShowDifFormatTokenChoice => IsDifSelected;

    public bool IsDifDotDifSelected { get => _difFormatToken == "dif"; set { if (value) SetDifFormatToken("dif"); } }
    public bool IsDifDotOutSelected { get => _difFormatToken == "out"; set { if (value) SetDifFormatToken("out"); } }

    /// <summary>Date embedded in the suggested output file name — defaults to today, but the user can pick any date (e.g. to match the business date the data represents).</summary>
    public DateTime? SelectedDate
    {
        get => _selectedDate;
        set
        {
            _selectedDate = value ?? DateTime.Today;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedDate)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SuggestedFileName)));
        }
    }

    /// <summary>"{baseName}.{formatToken}.{yyyyMMdd}" — what gets pre-filled into the save dialog; shown in the dialog itself too so the user sees exactly what will be saved before Export is even clicked.</summary>
    public string SuggestedFileName => ExportFileNaming.BuildFileName(
        Session.FileIndex.FilePath, CurrentFormatToken(), DateOnly.FromDateTime(_selectedDate),
        // One section of a bulk file exports under its own name, so exporting two of them doesn't
        // propose the same file name twice.
        Session.FileIndex.Header.IsMultiSection ? Session.FileIndex.Header.SectionName : null);

    public string PreviewText
    {
        get => _previewText;
        private set
        {
            _previewText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PreviewText)));
        }
    }

    private void SetFormat(ExportFormatKind format)
    {
        _format = format;
        foreach (string name in new[]
                 {
                     nameof(IsDifSelected), nameof(IsCsvSelected), nameof(IsTsvSelected), nameof(IsJsonSelected),
                     nameof(ShowDifFormatTokenChoice), nameof(SuggestedFileName),
                 })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
        RefreshPreview();
    }

    private void SetScope(ExportScope scope)
    {
        _scope = scope;
        foreach (string name in new[] { nameof(IsCurrentViewScope), nameof(IsSelectionScope), nameof(IsAllSectionsScope) })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
        RefreshPreview();
    }

    private void SetDifFormatToken(string token)
    {
        _difFormatToken = token;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDifDotDifSelected)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDifDotOutSelected)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SuggestedFileName)));
    }

    private string CurrentFormatToken() => _format switch
    {
        ExportFormatKind.Dif => _difFormatToken,
        ExportFormatKind.Csv => "csv",
        ExportFormatKind.Tsv => "tsv",
        ExportFormatKind.Json => "json",
        _ => throw new InvalidOperationException($"Unhandled export format '{_format}'."),
    };

    /// <summary>
    /// The rows the current scope covers, in grid order. "Selected rows" is intersected with the
    /// rows in view and kept in view order, so a selection made before a filter was applied exports
    /// what is both selected and visible, in the order it appears.
    /// </summary>
    private List<long> CurrentScopeRows()
    {
        IReadOnlyList<long> rowsInView = _grid.Rows.GetAllRowIndices();
        if (_scope != ExportScope.SelectedRows)
        {
            return [.. rowsInView];
        }

        return [.. rowsInView.Where(_grid.Selection.IsSelected)];
    }

    private void RefreshPreview()
    {
        using IRowExporter exporter = CreateExporter(_format, Session);
        PreviewText = Session.GeneratePreview(exporter, CurrentScopeRows());
    }

    private static IRowExporter CreateExporter(ExportFormatKind format, FileViewerSession session) => format switch
    {
        ExportFormatKind.Dif => new DifExporter(session.FileIndex),
        ExportFormatKind.Csv => new CsvExporter(),
        ExportFormatKind.Tsv => new TsvExporter(),
        ExportFormatKind.Json => new JsonExporter(),
        _ => throw new InvalidOperationException($"Unhandled export format '{format}'."),
    };

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            // AddExtension off: the suggested name already ends in ".{formatToken}.{yyyyMMdd}" (an
            // 8-digit "extension"), and Windows would otherwise try to tack the filter's default
            // extension on after that, e.g. "...20260727.dif".
            AddExtension = false,
            FileName = SuggestedFileName,
            InitialDirectory = Path.GetDirectoryName(Session.FileIndex.FilePath) ?? string.Empty,
            Title = _scope == ExportScope.AllSections ? "Choose where to write one file per section" : "Export",
            Filter = _format switch
            {
                ExportFormatKind.Dif => "DIF export (*.dif.*, *.out.*)|*.dif.*;*.out.*|All files (*.*)|*.*",
                ExportFormatKind.Csv => "CSV export (*.csv.*)|*.csv.*|All files (*.*)|*.*",
                ExportFormatKind.Tsv => "TSV export (*.tsv.*)|*.tsv.*|All files (*.*)|*.*",
                ExportFormatKind.Json => "JSON export (*.json.*)|*.json.*|All files (*.*)|*.*",
                _ => "All files (*.*)|*.*",
            },
        };
        if (dialog.ShowDialog() != true) return;

        // The streaming write itself must not block the UI thread, same as indexing — disable the
        // whole dialog (not just this button) so the format or scope can't change mid-export.
        IsEnabled = false;
        try
        {
            if (_scope == ExportScope.AllSections && _tab is not null)
            {
                await ExportEverySectionAsync(Path.GetDirectoryName(dialog.FileName) ?? string.Empty, _format, CurrentFormatToken());
            }
            else
            {
                string path = dialog.FileName;
                ExportFormatKind format = _format;
                List<long> rows = CurrentScopeRows();
                await Task.Run(() =>
                {
                    using FileStream stream = File.Create(path);
                    using IRowExporter exporter = CreateExporter(format, Session);
                    Session.Export(stream, exporter, rows);
                });
            }
        }
        finally
        {
            IsEnabled = true;
        }

        DialogResult = true;
        Close();
    }

    /// <summary>
    /// Writes one file per section of a bulk file, named for the section. A section already open
    /// exports the rows its own grid is showing (its filters, sort and edits); a section never
    /// opened is indexed here, on the fly, and exports in full — so "every section" doesn't
    /// quietly skip the ones that were never looked at.
    /// </summary>
    private async Task ExportEverySectionAsync(string directory, ExportFormatKind format, string formatToken)
    {
        foreach (FileSectionViewModel section in _tab!.Sections)
        {
            string fileName = ExportFileNaming.BuildFileName(
                _tab.FilePath, formatToken, DateOnly.FromDateTime(_selectedDate), section.Name);
            string path = Path.Combine(directory, fileName);

            if (section.Grid is { } sectionGrid)
            {
                List<long> rows = [.. sectionGrid.Rows.GetAllRowIndices()];
                FileViewerSession session = sectionGrid.Session;
                await Task.Run(() =>
                {
                    using FileStream stream = File.Create(path);
                    using IRowExporter exporter = CreateExporter(format, session);
                    session.Export(stream, exporter, rows);
                });
                continue;
            }

            using FileViewerSession opened = await FileViewerSession.OpenSectionAsync(_tab.FilePath, _tab.Layout, section.Index);
            await Task.Run(() =>
            {
                using FileStream stream = File.Create(path);
                using IRowExporter exporter = CreateExporter(format, opened);
                opened.Export(stream, exporter);
            });
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private enum ExportFormatKind { Dif, Csv, Tsv, Json }
}

/// <summary>Which rows an export covers.</summary>
public enum ExportScope
{
    /// <summary>Everything the grid is currently showing — its filters and sort applied.</summary>
    CurrentView,

    /// <summary>Only the rows ticked for bulk actions, in view order.</summary>
    SelectedRows,

    /// <summary>Every section of a bulk file, written as one file per section.</summary>
    AllSections,
}
