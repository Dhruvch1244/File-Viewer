using FileViewer.Core.Caching;
using FileViewer.Core.Filtering;
using FileViewer.Core.Indexing;
using FileViewer.Core.Native;
using FileViewer.Core.Overlay;
using FileViewer.Core.Scanning;

namespace FileViewer.Core.Sorting;

/// <summary>
/// Sorts a copy of a <see cref="SortKey"/> array by key bytes — never the base
/// <see cref="Native.RowIndexEntry"/> array, which stays in stable file order so overlay keys
/// (tied to base row index) are never affected by sorting (PRS §6.5).
/// </summary>
public static class RowSorter
{
    /// <summary>
    /// Returns a new, independently-disposable <see cref="SortKey"/> array sorted by key bytes.
    /// <paramref name="source"/> is left untouched. Ties are broken by ascending
    /// <see cref="SortKey.RowIndex"/> regardless of <paramref name="direction"/> — <see cref="Span{T}.Sort()"/>'s
    /// introsort is not itself stable, and breaking ties this way makes the result deterministic
    /// and stable with respect to the source's original row order (typically file order).
    /// </summary>
    public static UnmanagedArray<SortKey> SortByKey(UnmanagedArray<SortKey> source, SortDirection direction = SortDirection.Ascending)
    {
        nuint count = source.Count;
        var result = new UnmanagedArray<SortKey>(count == 0 ? 1 : count);
        foreach (ref readonly SortKey key in source.AsSpan())
        {
            result.Add(key);
        }

        int sign = direction == SortDirection.Ascending ? 1 : -1;
        result.AsSpan().Sort((a, b) =>
        {
            int cmp = a.CompareTo(b);
            return cmp != 0 ? sign * cmp : a.RowIndex.CompareTo(b.RowIndex);
        });

        return result;
    }

    /// <summary>
    /// Sort over an arbitrary column — the same read-every-candidate-row path as
    /// <see cref="Filtering.RowQueryEngine"/> (PRS §8), since there is no pre-extracted unmanaged sort
    /// key for anything but "_ID". Values that parse as numbers on both sides of a comparison sort
    /// numerically; otherwise ordinal string comparison. Stable: ties keep
    /// <paramref name="rowIndices"/>'s original relative order.
    ///
    /// Two passes, not one sort over live data: the column's values are extracted first (in
    /// parallel, through the read-ahead row reader that makes a file-order walk a sequential read),
    /// then a plain index sort runs over those extracted keys. That way a row is read once, rather
    /// than once per comparison it takes part in, and the comparisons themselves never touch the
    /// file, the overlay or the cache.
    ///
    /// Deleted rows keep their place in the result with an empty key rather than disappearing —
    /// dropping them is the grid's job (it excludes them when it builds the row order), not the
    /// sort's.
    /// </summary>
    public static List<long> SortByColumn(
        IReadOnlyList<long> rowIndices,
        string columnName,
        SortDirection direction,
        FileIndex fileIndex,
        EditOverlay overlay,
        DecodedRowCache? cache = null,
        CancellationToken cancellationToken = default)
    {
        int columnIndex = fileIndex.Header.ColumnIndexOf(columnName);
        if (columnIndex < 0)
        {
            return [.. rowIndices];
        }

        SortKeyEntry[] keys = ExtractKeys(rowIndices, columnIndex, fileIndex, overlay, cache, cancellationToken);

        var order = new int[rowIndices.Count];
        for (int i = 0; i < order.Length; i++) order[i] = i;

        int sign = direction == SortDirection.Ascending ? 1 : -1;
        Array.Sort(order, (left, right) =>
        {
            int comparison = SortKeyEntry.Compare(keys[left], keys[right]);
            // Array.Sort's introsort isn't stable on its own; breaking ties by original position
            // makes it so, and makes the result deterministic.
            return comparison != 0 ? sign * comparison : left.CompareTo(right);
        });

        var result = new List<long>(rowIndices.Count);
        foreach (int position in order)
        {
            result.Add(rowIndices[position]);
        }
        return result;
    }

    /// <summary>
    /// One row's sort key: the column's text, plus the number it parses as, worked out once.
    ///
    /// Parsing here rather than inside the comparison is the difference between 2 million parses and
    /// something like 40 million: a comparison sort of n items performs n·log n comparisons, and
    /// <see cref="NumericAwareStringComparer"/> parses <em>both</em> sides of every one of them. The
    /// ordering is identical — same rule, decided once per value instead of once per comparison.
    /// </summary>
    private readonly record struct SortKeyEntry(string Text, double Number, bool IsNumeric)
    {
        public static SortKeyEntry From(string text) =>
            double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double number)
                ? new SortKeyEntry(text, number, true)
                : new SortKeyEntry(text, 0, false);

        /// <summary>Numeric comparison when both sides are numbers, ordinal string comparison otherwise — the same rule <see cref="NumericAwareStringComparer"/> applies.</summary>
        public static int Compare(in SortKeyEntry left, in SortKeyEntry right) =>
            left.IsNumeric && right.IsNumeric
                ? left.Number.CompareTo(right.Number)
                : string.CompareOrdinal(left.Text, right.Text);
    }

    /// <summary>
    /// Reads each row once and pulls out the one column being sorted by. Partitioned across cores
    /// for anything big enough to be worth it; each worker gets its own reader (and therefore its
    /// own buffers), and writes into its own slice of the shared key array, so no synchronization
    /// is needed.
    /// </summary>
    private static SortKeyEntry[] ExtractKeys(
        IReadOnlyList<long> rowIndices,
        int columnIndex,
        FileIndex fileIndex,
        EditOverlay overlay,
        DecodedRowCache? cache,
        CancellationToken cancellationToken)
    {
        var keys = new SortKeyEntry[rowIndices.Count];
        OverlaySnapshot snapshot = overlay.CreateSnapshot();

        if (rowIndices.Count < RowQueryEngine.ParallelThresholdRows)
        {
            ExtractKeyRange(rowIndices, 0, rowIndices.Count, columnIndex, fileIndex, snapshot, cache, keys, cancellationToken);
            return keys;
        }

        int partitionCount = Math.Max(1, Math.Min(Environment.ProcessorCount, rowIndices.Count / RowQueryEngine.ParallelThresholdRows));
        int partitionSize = (rowIndices.Count + partitionCount - 1) / partitionCount;

        Parallel.For(0, partitionCount, new ParallelOptions { CancellationToken = cancellationToken }, partition =>
        {
            int start = partition * partitionSize;
            int end = Math.Min(rowIndices.Count, start + partitionSize);
            ExtractKeyRange(rowIndices, start, end, columnIndex, fileIndex, snapshot, cache: null, keys, cancellationToken);
        });

        return keys;
    }

    private static void ExtractKeyRange(
        IReadOnlyList<long> rowIndices,
        int start,
        int end,
        int columnIndex,
        FileIndex fileIndex,
        OverlaySnapshot snapshot,
        DecodedRowCache? cache,
        SortKeyEntry[] keys,
        CancellationToken cancellationToken)
    {
        using var reader = new ScanRowReader(fileIndex, snapshot, cache, ScanRowReader.IsAscending(rowIndices, start, end));
        for (int i = start; i < end; i++)
        {
            if ((i & 0x3FF) == 0) cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<string>? fields = reader.GetFields(rowIndices[i]);
            keys[i] = SortKeyEntry.From(
                fields is not null && columnIndex < fields.Count ? fields[columnIndex] : string.Empty);
        }
    }
}
