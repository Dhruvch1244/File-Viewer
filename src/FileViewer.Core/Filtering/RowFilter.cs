using FileViewer.Core.Caching;
using FileViewer.Core.Indexing;
using FileViewer.Core.Overlay;

namespace FileViewer.Core.Filtering;

/// <summary>
/// Arbitrary-column substring filter. Unlike sorting (which only ever permutes the pre-extracted
/// "_ID" <see cref="Native.SortKey"/> array), matching an arbitrary column means decoding each
/// candidate row through <see cref="RowResolver"/> — the expected slower path PRS §8 budgets
/// "low single-digit seconds" for at 2 GB scale, versus sort's near-instant array permutation.
/// </summary>
public static class RowFilter
{
    /// <summary>Returns the subset of <paramref name="rowIndices"/> (order preserved) whose resolved fields contain <paramref name="searchText"/> in any column, case-insensitively.</summary>
    public static List<long> Filter(
        IEnumerable<long> rowIndices,
        string searchText,
        FileIndex fileIndex,
        EditOverlay overlay,
        DecodedRowCache cache)
    {
        if (string.IsNullOrEmpty(searchText))
        {
            return [.. rowIndices];
        }

        var result = new List<long>();
        foreach (long rowIndex in rowIndices)
        {
            ResolvedRow? resolved = RowResolver.Resolve(rowIndex, fileIndex, overlay, cache);
            if (resolved is null) continue; // tombstone

            foreach (string field in resolved.FieldValues)
            {
                if (field.Contains(searchText, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(rowIndex);
                    break;
                }
            }
        }
        return result;
    }
}
