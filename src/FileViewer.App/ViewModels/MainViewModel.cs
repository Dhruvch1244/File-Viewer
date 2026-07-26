using System.IO;
using System.Linq;
using FileViewer.App.Common;
using FileViewer.App.Logging;
using FileViewer.Core.Dif;
using FileViewer.Core.Indexing;
using FileViewer.Core.Session;

namespace FileViewer.App.ViewModels;

/// <summary>Top-level view model: owns the current <see cref="FileViewerSession"/> lifecycle, open/index progress, and the <see cref="GridViewModel"/> once a file is loaded.</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private FileViewerSession? _session;
    private GridViewModel? _grid;
    private string _statusMessage = "No file open.";
    private double _indexingProgressPercent;
    private bool _isIndexing;

    public GridViewModel? Grid
    {
        get => _grid;
        private set => SetField(ref _grid, value);
    }

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

    public async Task OpenFileAsync(string path)
    {
        IsIndexing = true;
        IndexingProgressPercent = 0;
        StatusMessage = $"Indexing {Path.GetFileName(path)}...";

        try
        {
            var progress = new Progress<IndexingProgress>(p =>
            {
                IndexingProgressPercent = p.TotalBytes > 0 ? 100.0 * p.BytesScanned / p.TotalBytes : 0;
                StatusMessage = $"Indexing... {p.RowsFound:N0} row(s) found";
            });

            FileViewerSession newSession = await FileViewerSession.OpenAsync(path, progress: progress);

            _session?.Dispose();
            _session = newSession;
            Grid = new GridViewModel(newSession);

            if (!newSession.FileIndex.Header.IsValid)
            {
                string reason = newSession.FileIndex.Diagnostics
                    .FirstOrDefault(d => d.Severity == DifDiagnosticSeverity.Error)?.Message ?? "Unrecognized file format.";
                StatusMessage = $"Could not open '{Path.GetFileName(path)}': {reason}";
                FileLogger.Instance.LogWarning($"Opened '{path}' but it is not a valid DIF file: {reason}");
            }
            else
            {
                int warningCount = newSession.FileIndex.Diagnostics.Count(d => d.Severity == DifDiagnosticSeverity.Warning);
                StatusMessage = warningCount == 0
                    ? $"Loaded {newSession.FileIndex.RowIndex.Count:N0} row(s) from {Path.GetFileName(path)}."
                    : $"Loaded {newSession.FileIndex.RowIndex.Count:N0} row(s) from {Path.GetFileName(path)} ({warningCount:N0} warning(s) — see diagnostics).";
                FileLogger.Instance.LogInfo($"Opened '{path}': {newSession.FileIndex.RowIndex.Count:N0} row(s), {warningCount:N0} diagnostic warning(s).");
            }
        }
        catch (Exception ex)
        {
            // Malformed/unreadable files must fail gracefully, never crash the process (PRS §8).
            StatusMessage = $"Failed to open '{Path.GetFileName(path)}': {ex.Message}";
            FileLogger.Instance.LogError($"Failed to open '{path}'.", ex);
        }
        finally
        {
            IsIndexing = false;
            IndexingProgressPercent = 0;
        }
    }

    public void Dispose() => _session?.Dispose();
}
