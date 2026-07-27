using System.ComponentModel;
using System.IO;
using System.Windows;
using FileViewer.Core.Export;
using FileViewer.Core.Session;
using Microsoft.Win32;

namespace FileViewer.App.Views;

/// <summary>
/// Format picker + live 2-row preview, reusing <see cref="FileViewerSession.GeneratePreview"/> so
/// the preview can never drift from what the real export (also via the session) produces. Also
/// suggests a Bloomberg-style output file name — "{baseName}.{formatToken}.{yyyyMMdd}" (see
/// <see cref="ExportFileNaming"/>) — built from the source file's name and a user-editable date, so
/// the default <see cref="SaveFileDialog"/> file name matches how these files are actually named in
/// the wild instead of a plain "*.csv" with no date.
/// </summary>
public partial class ExportDialogView : Window, INotifyPropertyChanged
{
    private readonly FileViewerSession _session;
    private ExportFormatKind _format = ExportFormatKind.Csv;
    private string _difFormatToken = "dif";
    private DateTime _selectedDate = DateTime.Today;
    private string _previewText = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ExportDialogView(FileViewerSession session)
    {
        _session = session;
        DataContext = this;
        InitializeComponent();
        RefreshPreview();
    }

    public bool IsDifSelected { get => _format == ExportFormatKind.Dif; set { if (value) SetFormat(ExportFormatKind.Dif); } }
    public bool IsCsvSelected { get => _format == ExportFormatKind.Csv; set { if (value) SetFormat(ExportFormatKind.Csv); } }
    public bool IsTsvSelected { get => _format == ExportFormatKind.Tsv; set { if (value) SetFormat(ExportFormatKind.Tsv); } }
    public bool IsJsonSelected { get => _format == ExportFormatKind.Json; set { if (value) SetFormat(ExportFormatKind.Json); } }

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
        _session.FileIndex.FilePath, CurrentFormatToken(), DateOnly.FromDateTime(_selectedDate));

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

    private void RefreshPreview()
    {
        using IRowExporter exporter = CreateExporter(_format);
        PreviewText = _session.GeneratePreview(exporter);
    }

    private IRowExporter CreateExporter(ExportFormatKind format) => format switch
    {
        ExportFormatKind.Dif => DifExporter.ForHeader(_session.FileIndex.Header),
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
            InitialDirectory = Path.GetDirectoryName(_session.FileIndex.FilePath) ?? string.Empty,
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
        // whole dialog (not just this button) so the format can't change mid-export.
        string path = dialog.FileName;
        ExportFormatKind format = _format;
        IsEnabled = false;
        try
        {
            await Task.Run(() =>
            {
                using FileStream stream = File.Create(path);
                using IRowExporter exporter = CreateExporter(format);
                _session.Export(stream, exporter);
            });
        }
        finally
        {
            IsEnabled = true;
        }

        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private enum ExportFormatKind { Dif, Csv, Tsv, Json }
}
