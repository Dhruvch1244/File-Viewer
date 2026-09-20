using FileViewer.Core.Caching;
using FileViewer.Core.Dif;
using FileViewer.Core.Indexing;
using FileViewer.Core.Overlay;
using FileViewer.Core.Scanning;

namespace FileViewer.Core.Filtering;

/// <summary>
/// Runs a <see cref="RowPredicateSet"/> over a set of rows — one decode per row, in parallel,
/// cancellable, preserving the input order.
///
/// Three things make this materially cheaper than the per-filter passes it replaces:
/// <list type="number">
/// <item><b>One pass.</b> Every active filter is evaluated against a row while it is decoded, instead
/// of each filter re-walking (and re-decoding) the survivors of the previous one.</item>
/// <item><b>Parallel.</b> Row reads are independent (<see cref="RandomAccess"/> takes an explicit
/// offset), so the scan is partitioned across cores and the per-partition results concatenated in
/// partition order — which is exactly the input order, so nothing needs re-sorting afterwards.</item>
/// <item><b>Allocation-light.</b> Each worker rents one row buffer and re-uses it, and the overlay is
/// read from a lock-free snapshot rather than taking its lock twice per row. Where the query allows
/// it (a plain substring search over all columns), rows are matched straight from their bytes and
/// never split into fields or decoded into strings at all.</item>
/// </list>
///
/// The shared <see cref="DecodedRowCache"/> is deliberately bypassed for large scans: filling a
/// 4096-entry LRU with rows a scan will never look at again would evict exactly the rows the visible
/// page needs, and the lock it takes would serialize the workers.
/// </summary>
public static class RowQueryEngine
{
    /// <summary>Row counts below this run on the calling thread — partitioning costs more than it saves.</summary>
    public const int ParallelThresholdRows = 8192;

    /// <summary>Filters <paramref name="rowIndices"/> (order preserved), dropping deleted rows and anything the predicates reject.</summary>
    public static List<long> Filter(
        IReadOnlyList<long> rowIndices,
        RowPredicateSet predicates,
        FileIndex fileIndex,
        EditOverlay overlay,
        DecodedRowCache? cache = null,
        CancellationToken cancellationToken = default)
    {
        OverlaySnapshot snapshot = overlay.CreateSnapshot();

        // Nothing to filter and nothing deleted: the result is the input, no rows read at all.
        if (predicates.IsEmpty && snapshot.IsEmpty)
        {
            return [.. rowIndices];
        }

        if (rowIndices.Count < ParallelThresholdRows)
        {
            var result = new List<long>();
            FilterRange(rowIndices, 0, rowIndices.Count, predicates, fileIndex, snapshot, cache, result, cancellationToken);
            return result;
        }

        int partitionCount = PartitionCount(rowIndices.Count);
        var partitions = new List<long>[partitionCount];
        int partitionSize = (rowIndices.Count + partitionCount - 1) / partitionCount;

        Parallel.For(0, partitionCount, new ParallelOptions { CancellationToken = cancellationToken }, partition =>
        {
            int start = partition * partitionSize;
            int end = Math.Min(rowIndices.Count, start + partitionSize);
            var local = new List<long>(Math.Max(16, (end - start) / 4));
            FilterRange(rowIndices, start, end, predicates, fileIndex, snapshot, cache: null, local, cancellationToken);
            partitions[partition] = local;
        });

        int total = 0;
        foreach (List<long> partition in partitions) total += partition.Count;

        var merged = new List<long>(total);
        foreach (List<long> partition in partitions) merged.AddRange(partition);
        return merged;
    }

    /// <summary>
    /// Every distinct value <paramref name="columnName"/> takes across <paramref name="rowIndices"/>,
    /// for the Excel-style column filter popup. Capped at <paramref name="maxValues"/>: a popup
    /// can't usefully show more, and without a cap a high-cardinality column (a price, an ID) would
    /// build a set as large as the file itself.
    /// </summary>
    public static DistinctValueResult GetDistinctValues(
        IReadOnlyList<long> rowIndices,
        string columnName,
        FileIndex fileIndex,
        EditOverlay overlay,
        DecodedRowCache? cache = null,
        int maxValues = DistinctValueResult.DefaultMaxValues,
        CancellationToken cancellationToken = default)
    {
        int columnIndex = fileIndex.Header.ColumnIndexOf(columnName);
        if (columnIndex < 0)
        {
            return new DistinctValueResult([], false);
        }

        OverlaySnapshot snapshot = overlay.CreateSnapshot();
        var distinct = new SortedSet<string>(StringComparer.Ordinal);
        bool truncated = false;

        // Same rule as the filter scan: a large lookup bypasses the shared cache rather than
        // flushing it with rows nothing will render.
        DecodedRowCache? scanCache = rowIndices.Count <= ParallelThresholdRows ? cache : null;
        bool overlayIsEmpty = snapshot.IsEmpty;
        using var reader = new ScanRowReader(fileIndex, snapshot, scanCache, ScanRowReader.IsAscending(rowIndices, 0, rowIndices.Count));
        foreach (long rowIndex in rowIndices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!overlayIsEmpty && snapshot.GetRowState(rowIndex) == RowState.Deleted) continue; // tombstone

            IReadOnlyList<string>? fields = reader.GetFields(rowIndex);
            if (fields is null) continue;

            distinct.Add(columnIndex < fields.Count ? fields[columnIndex] : string.Empty);
            if (distinct.Count >= maxValues)
            {
                truncated = true;
                break;
            }
        }

        return new DistinctValueResult([.. distinct], truncated);
    }

    private static void FilterRange(
        IReadOnlyList<long> rowIndices,
        int start,
        int end,
        RowPredicateSet predicates,
        FileIndex fileIndex,
        OverlaySnapshot snapshot,
        DecodedRowCache? cache,
        List<long> destination,
        CancellationToken cancellationToken)
    {
        bool overlayIsEmpty = snapshot.IsEmpty;
        bool matchOnRawAlone = predicates.SupportsRawMatching;
        bool prefilterOnRaw = !matchOnRawAlone && predicates.HasRawPrefilter;

        using var reader = new ScanRowReader(fileIndex, snapshot, cache, ScanRowReader.IsAscending(rowIndices, start, end));
        for (int i = start; i < end; i++)
        {
            if ((i & 0x3FF) == 0) cancellationToken.ThrowIfCancellationRequested();

            long rowIndex = rowIndices[i];

            if (!overlayIsEmpty && snapshot.GetRowState(rowIndex) == RowState.Deleted) continue;
            if (predicates.IsEmpty)
            {
                destination.Add(rowIndex);
                continue;
            }

            if ((matchOnRawAlone || prefilterOnRaw) && reader.TryReadRawRow(rowIndex, out ReadOnlySpan<byte> raw))
            {
                if (matchOnRawAlone)
                {
                    if (predicates.MatchesRaw(raw)) destination.Add(rowIndex);
                    continue;
                }

                // Cheap rejection first: most rows die here, on bytes, and are never decoded.
                if (predicates.RawPrefilterRejects(raw)) continue;

                // Survivors are decoded from the bytes already in hand rather than re-read.
                if (predicates.Matches(reader.ParseFields(raw))) destination.Add(rowIndex);
                continue;
            }

            IReadOnlyList<string>? fields = reader.GetFields(rowIndex);
            if (fields is null) continue;
            if (predicates.Matches(fields)) destination.Add(rowIndex);
        }
    }

    private static int PartitionCount(int rowCount)
    {
        int byCores = Environment.ProcessorCount;
        int byWork = Math.Max(1, rowCount / ParallelThresholdRows);
        return Math.Max(1, Math.Min(byCores, byWork));
    }

}

/// <summary>The distinct values of a column, and whether the lookup stopped early at its cap.</summary>
/// <param name="Values">Distinct values, ordinally sorted.</param>
/// <param name="Truncated">True if the column has more distinct values than were collected.</param>
public readonly record struct DistinctValueResult(IReadOnlyList<string> Values, bool Truncated)
{
    /// <summary>Default cap on collected distinct values — far more than a filter popup can usefully list, small enough that a unique-per-row column can't blow up memory.</summary>
    public const int DefaultMaxValues = 10_000;
}
