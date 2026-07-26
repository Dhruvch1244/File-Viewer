using System.ComponentModel;
using System.IO;
using System.Windows;
using FileViewer.Core.Export;
using FileViewer.Core.Session;
using Microsoft.Win32;

namespace FileViewer.App.Views;

/// <summary>Format picker + live 2-row preview, reusing <see cref="FileViewerSession.GeneratePreview"/> so the preview can never drift from what the real export (also via the session) produces.</summary>
public partial class ExportDialogView : Window, INotifyPropertyChanged
{
    private readonly FileViewerSession _session;
    private ExportFormatKind _format = ExportFormatKind.Csv;
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
        foreach (string name in new[] { nameof(IsDifSelected), nameof(IsCsvSelected), nameof(IsTsvSelected), nameof(IsJsonSelected) })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
        RefreshPreview();
    }

    private void RefreshPreview()
    {
        using IRowExporter exporter = CreateExporter(_format);
        PreviewText = _session.GeneratePreview(exporter);
    }

    private IRowExporter CreateExporter(ExportFormatKind format) => format switch
    {
        ExportFormatKind.Dif => new DifExporter(_session.FileIndex.Header.Delimiter, _session.FileIndex.Header.HeaderMetadata),
        ExportFormatKind.Csv => new CsvExporter(),
        ExportFormatKind.Tsv => new TsvExporter(),
        ExportFormatKind.Json => new JsonExporter(),
        _ => throw new InvalidOperationException($"Unhandled export format '{format}'."),
    };

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = _format switch
            {
                ExportFormatKind.Dif => "DIF files (*.dif)|*.dif",
                ExportFormatKind.Csv => "CSV files (*.csv)|*.csv",
                ExportFormatKind.Tsv => "TSV files (*.tsv)|*.tsv",
                ExportFormatKind.Json => "JSON files (*.json)|*.json",
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
