using FileViewer.Core.Caching;
using FileViewer.Core.Indexing;
using FileViewer.Core.Overlay;

namespace FileViewer.Core.Export;

/// <summary>
/// Drives a streaming export: iterates a caller-supplied row order through the same
/// <see cref="RowResolver"/> used by rendering, writing each resolved row as it goes with no
/// full-file buffering. Tombstones (deleted rows) are skipped. The order is a parameter rather
/// than something this class computes, so it works unchanged whether the caller passes plain file
/// order or a sorted/filtered view.
/// </summary>
public static class ExportRunner
{
    /// <summary>Rows written between <paramref name="progress"/> reports — often enough to animate a progress bar, rare enough not to flood the UI thread.</summary>
    private const int ProgressReportInterval = 5_000;

    public static void Export(
        Stream stream,
        FileIndex fileIndex,
        EditOverlay overlay,
        DecodedRowCache cache,
        IEnumerable<long> orderedRowIndices,
        IRowExporter exporter,
        int? maxRows = null,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        exporter.Begin(stream, fileIndex.Header.ColumnNames);

        long written = 0;
        foreach (long rowIndex in orderedRowIndices)
        {
            if (maxRows is int max && written >= max) break;

            ResolvedRow? resolved = RowResolver.Resolve(rowIndex, fileIndex, overlay, cache);
            if (resolved is null) continue; // tombstone

            exporter.WriteRow(resolved);
            written++;

            if (written % ProgressReportInterval == 0)
            {
                // Checked on the same interval as the progress report: a cancelled export stops
                // promptly without paying for a token check per row.
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(written);
            }
        }

        exporter.End();
        progress?.Report(written);
    }

    /// <summary>Base file row order (0..RowCount-1) followed by any currently-live Added/Duplicated rows, in creation order.</summary>
    public static IEnumerable<long> FileOrderWithAddedRows(FileIndex fileIndex, EditOverlay overlay)
    {
        for (long i = 0; i < (long)fileIndex.RowIndex.Count; i++)
        {
            yield return i;
        }

        foreach (long rowIndex in overlay.GetLiveAddedOrDuplicatedRowIndices())
        {
            yield return rowIndex;
        }
    }
}
