using FileViewer.Core.Native;

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
}
