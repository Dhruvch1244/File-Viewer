using System.Globalization;
using FileViewer.Core.Caching;
using FileViewer.Core.Filtering;
using FileViewer.Core.Indexing;
using FileViewer.Core.Overlay;
using FileViewer.Core.Scanning;
using FileViewer.Core.Sorting;

namespace FileViewer.Core.Statistics;

/// <summary>
/// Computes a column's <see cref="ColumnStatistics"/> in one pass over the rows, through the same
/// read-ahead row reader the filter engine uses — so summarizing a column costs about what filtering
/// on it does, not a multiple of it.
/// </summary>
public static class ColumnStatisticsCalculator
{
    /// <summary>
    /// Cap on distinct values counted. Past this the exact count stops being interesting (and the
    /// set would grow with the file), so the result is reported as truncated instead.
    /// </summary>
    public const int DistinctCountCap = 100_000;

    public static ColumnStatistics Compute(
        IReadOnlyList<long> rowIndices,
        string columnName,
        FileIndex fileIndex,
        EditOverlay overlay,
        DecodedRowCache? cache = null,
        CancellationToken cancellationToken = default)
    {
        int columnIndex = fileIndex.Header.ColumnIndexOf(columnName);
        if (columnIndex < 0)
        {
            return ColumnStatistics.Empty(columnName);
        }

        OverlaySnapshot snapshot = overlay.CreateSnapshot();
        bool overlayIsEmpty = snapshot.IsEmpty;

        // Same rule as the filter scan: a large pass bypasses the shared cache rather than flushing
        // it with rows nothing will render.
        DecodedRowCache? scanCache = rowIndices.Count <= RowQueryEngine.ParallelThresholdRows ? cache : null;

        var distinct = new HashSet<string>(StringComparer.Ordinal);
        bool distinctTruncated = false;
        int rowCount = 0;
        int blankCount = 0;
        int numericCount = 0;
        double numericMin = 0;
        double numericMax = 0;
        double sum = 0;
        string? min = null;
        string? max = null;

        using var reader = new ScanRowReader(
            fileIndex, snapshot, scanCache, ScanRowReader.IsAscending(rowIndices, 0, rowIndices.Count));

        for (int i = 0; i < rowIndices.Count; i++)
        {
            if ((i & 0x3FF) == 0) cancellationToken.ThrowIfCancellationRequested();

            long rowIndex = rowIndices[i];
            if (!overlayIsEmpty && snapshot.GetRowState(rowIndex) == RowState.Deleted) continue;

            IReadOnlyList<string>? fields = reader.GetFields(rowIndex);
            if (fields is null) continue;

            string value = columnIndex < fields.Count ? fields[columnIndex] : string.Empty;
            rowCount++;

            if (value.Length == 0)
            {
                blankCount++;
                continue;
            }

            if (!distinctTruncated && distinct.Add(value) && distinct.Count >= DistinctCountCap)
            {
                distinctTruncated = true;
            }

            if (min is null || NumericAwareStringComparer.Instance.Compare(value, min) < 0) min = value;
            if (max is null || NumericAwareStringComparer.Instance.Compare(value, max) > 0) max = value;

            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double numeric))
            {
                if (numericCount == 0)
                {
                    numericMin = numeric;
                    numericMax = numeric;
                }
                else
                {
                    if (numeric < numericMin) numericMin = numeric;
                    if (numeric > numericMax) numericMax = numeric;
                }
                numericCount++;
                sum += numeric;
            }
        }

        return new ColumnStatistics(
            columnName,
            rowCount,
            blankCount,
            distinct.Count,
            distinctTruncated,
            min,
            max,
            numericCount,
            numericCount > 0 ? numericMin : null,
            numericCount > 0 ? numericMax : null,
            numericCount > 0 ? sum : null);
    }
}
