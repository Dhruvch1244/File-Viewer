using FileViewer.Core.Caching;
using FileViewer.Core.Indexing;
using FileViewer.Core.Native;
using FileViewer.Core.Overlay;

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
    /// Decode-based sort over an arbitrary column — the same acknowledged slower path as
    /// <see cref="Filtering.RowFilter"/> (PRS §8), since there is no pre-extracted unmanaged sort
    /// key for anything but "_ID". Values that parse as numbers on both sides of a comparison sort
    /// numerically; otherwise falls back to ordinal string comparison. Uses a stable sort (LINQ's
    /// OrderBy), so ties keep <paramref name="rowIndices"/>'s original relative order.
    /// </summary>
    public static List<long> SortByColumn(
        IReadOnlyList<long> rowIndices,
        string columnName,
        SortDirection direction,
        FileIndex fileIndex,
        EditOverlay overlay,
        DecodedRowCache cache)
    {
        int columnIndex = fileIndex.Header.ColumnIndexOf(columnName);
        if (columnIndex < 0)
        {
            return [.. rowIndices];
        }

        var keyed = new List<(long RowIndex, string Value)>(rowIndices.Count);
        foreach (long rowIndex in rowIndices)
        {
            ResolvedRow? resolved = RowResolver.Resolve(rowIndex, fileIndex, overlay, cache);
            string value = resolved is not null && columnIndex < resolved.FieldValues.Count
                ? resolved.FieldValues[columnIndex]
                : string.Empty;
            keyed.Add((rowIndex, value));
        }

        IOrderedEnumerable<(long RowIndex, string Value)> ordered = direction == SortDirection.Ascending
            ? keyed.OrderBy(k => k.Value, NumericAwareStringComparer.Instance)
            : keyed.OrderByDescending(k => k.Value, NumericAwareStringComparer.Instance);

        return [.. ordered.Select(k => k.RowIndex)];
    }
}
