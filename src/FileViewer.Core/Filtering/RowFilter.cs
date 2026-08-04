using System.Text.RegularExpressions;
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

    /// <summary>
    /// Returns every distinct value <paramref name="columnName"/> takes across <paramref name="rowIndices"/>
    /// — an Excel-style "pick from what's actually there" filter menu, so a user never has to
    /// already know a column's possible values to filter by them. Sorted ordinally; case-sensitive
    /// (two differently-cased strings are genuinely different values here, matching what a user
    /// would see displayed).
    /// </summary>
    public static List<string> GetDistinctValues(
        IEnumerable<long> rowIndices,
        string columnName,
        FileIndex fileIndex,
        EditOverlay overlay,
        DecodedRowCache cache)
    {
        int columnIndex = fileIndex.Header.ColumnIndexOf(columnName);
        if (columnIndex < 0)
        {
            return [];
        }

        var distinct = new SortedSet<string>(StringComparer.Ordinal);
        foreach (long rowIndex in rowIndices)
        {
            ResolvedRow? resolved = RowResolver.Resolve(rowIndex, fileIndex, overlay, cache);
            if (resolved is null) continue;

            distinct.Add(columnIndex < resolved.FieldValues.Count ? resolved.FieldValues[columnIndex] : string.Empty);
        }
        return [.. distinct];
    }

    /// <summary>Returns the subset of <paramref name="rowIndices"/> (order preserved) whose value for <paramref name="columnName"/> is one of <paramref name="allowedValues"/>.</summary>
    public static List<long> FilterByColumnValues(
        IEnumerable<long> rowIndices,
        string columnName,
        IReadOnlySet<string> allowedValues,
        FileIndex fileIndex,
        EditOverlay overlay,
        DecodedRowCache cache)
    {
        int columnIndex = fileIndex.Header.ColumnIndexOf(columnName);
        if (columnIndex < 0)
        {
            return [.. rowIndices];
        }

        var result = new List<long>();
        foreach (long rowIndex in rowIndices)
        {
            ResolvedRow? resolved = RowResolver.Resolve(rowIndex, fileIndex, overlay, cache);
            if (resolved is null) continue;

            string value = columnIndex < resolved.FieldValues.Count ? resolved.FieldValues[columnIndex] : string.Empty;
            if (allowedValues.Contains(value))
            {
                result.Add(rowIndex);
            }
        }
        return result;
    }

    /// <summary>
    /// Attempts to compile <paramref name="pattern"/> as a case-insensitive regex. Callers driving a
    /// live filter-as-you-type box (an ag-Grid-style per-column filter row) should call this
    /// themselves to surface an invalid pattern to the user (e.g. a red input border) — the pattern
    /// a user is still typing is expected to be invalid regex some of the time, so that's a normal
    /// state to show, not an exception to propagate.
    /// </summary>
    public static bool TryCompileRegex(string pattern, out Regex? regex, out string? error)
    {
        try
        {
            regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            error = null;
            return true;
        }
        catch (ArgumentException ex)
        {
            regex = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Returns the subset of <paramref name="rowIndices"/> (order preserved) whose value for
    /// <paramref name="columnName"/> matches <paramref name="pattern"/> — a per-column filter box
    /// (ag-Grid's "floating filter row" is the model), as opposed to <see cref="Filter"/>'s
    /// substring search across every column at once. An invalid regex matches nothing rather than
    /// throwing — use <see cref="TryCompileRegex"/> first if the caller wants to distinguish "no
    /// matches" from "not a valid pattern yet" (e.g. while the user is still typing it).
    /// </summary>
    public static List<long> FilterByColumnPattern(
        IEnumerable<long> rowIndices,
        string columnName,
        string pattern,
        bool useRegex,
        FileIndex fileIndex,
        EditOverlay overlay,
        DecodedRowCache cache)
    {
        int columnIndex = fileIndex.Header.ColumnIndexOf(columnName);
        if (columnIndex < 0 || string.IsNullOrEmpty(pattern))
        {
            return [.. rowIndices];
        }

        Regex? regex = null;
        if (useRegex && !TryCompileRegex(pattern, out regex, out _))
        {
            return [];
        }

        var result = new List<long>();
        foreach (long rowIndex in rowIndices)
        {
            ResolvedRow? resolved = RowResolver.Resolve(rowIndex, fileIndex, overlay, cache);
            if (resolved is null) continue;

            string value = columnIndex < resolved.FieldValues.Count ? resolved.FieldValues[columnIndex] : string.Empty;
            bool isMatch = regex is not null
                ? regex.IsMatch(value)
                : value.Contains(pattern, StringComparison.OrdinalIgnoreCase);
            if (isMatch)
            {
                result.Add(rowIndex);
            }
        }
        return result;
    }
}
