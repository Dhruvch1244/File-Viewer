using System.Text.RegularExpressions;
using FileViewer.Core.Caching;
using FileViewer.Core.Indexing;
using FileViewer.Core.Overlay;

namespace FileViewer.Core.Filtering;

/// <summary>
/// Convenience entry points for the individual filter kinds, each expressed as a one-predicate
/// <see cref="RowPredicateSet"/> run through <see cref="RowQueryEngine"/>. Real callers with more
/// than one filter active should compile them together and make a single call instead — see
/// <see cref="RowPredicateSet"/> for why one pass beats a pass per filter.
///
/// Matching an arbitrary column means decoding candidate rows (unlike sorting by "_ID", which only
/// permutes a pre-extracted key array) — the slower path PRS §8 budgets "low single-digit seconds"
/// for at 2 GB scale.
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
        List<long> rows = [.. rowIndices];
        if (string.IsNullOrEmpty(searchText))
        {
            return rows;
        }

        return RowQueryEngine.Filter(
            rows,
            RowPredicateSet.Compile(SearchQuery.ForText(searchText), null, null, fileIndex.Header),
            fileIndex, overlay, cache);
    }

    /// <summary>Returns the subset of <paramref name="rowIndices"/> (order preserved) matching <paramref name="query"/> — the multi-term main search.</summary>
    public static List<long> Filter(
        IEnumerable<long> rowIndices,
        SearchQuery query,
        FileIndex fileIndex,
        EditOverlay overlay,
        DecodedRowCache? cache = null,
        CancellationToken cancellationToken = default)
    {
        List<long> rows = [.. rowIndices];
        if (query.IsEmpty)
        {
            return rows;
        }

        return RowQueryEngine.Filter(
            rows,
            RowPredicateSet.Compile(query, null, null, fileIndex.Header),
            fileIndex, overlay, cache, cancellationToken);
    }

    /// <summary>
    /// Every distinct value <paramref name="columnName"/> takes across <paramref name="rowIndices"/>
    /// — an Excel-style "pick from what's actually there" filter menu, so a user never has to
    /// already know a column's possible values to filter by them. Sorted ordinally; case-sensitive
    /// (two differently-cased strings are genuinely different values here, matching what a user
    /// would see displayed). Capped — see <see cref="RowQueryEngine.GetDistinctValues"/>.
    /// </summary>
    public static List<string> GetDistinctValues(
        IEnumerable<long> rowIndices,
        string columnName,
        FileIndex fileIndex,
        EditOverlay overlay,
        DecodedRowCache cache) =>
        [.. RowQueryEngine.GetDistinctValues([.. rowIndices], columnName, fileIndex, overlay, cache).Values];

    /// <summary>Returns the subset of <paramref name="rowIndices"/> (order preserved) whose value for <paramref name="columnName"/> is one of <paramref name="allowedValues"/>.</summary>
    public static List<long> FilterByColumnValues(
        IEnumerable<long> rowIndices,
        string columnName,
        IReadOnlySet<string> allowedValues,
        FileIndex fileIndex,
        EditOverlay overlay,
        DecodedRowCache cache)
    {
        List<long> rows = [.. rowIndices];
        if (fileIndex.Header.ColumnIndexOf(columnName) < 0)
        {
            return rows;
        }

        var filters = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal) { [columnName] = [.. allowedValues] };
        return RowQueryEngine.Filter(
            rows,
            RowPredicateSet.Compile(SearchQuery.Empty, filters, null, fileIndex.Header),
            fileIndex, overlay, cache);
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
    /// (ag-Grid's "floating filter row" is the model), as opposed to <see cref="Filter(IEnumerable{long}, string, FileIndex, EditOverlay, DecodedRowCache)"/>'s
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
        List<long> rows = [.. rowIndices];
        if (fileIndex.Header.ColumnIndexOf(columnName) < 0 || string.IsNullOrEmpty(pattern))
        {
            return rows;
        }

        var filters = new Dictionary<string, ColumnPatternFilter>(StringComparer.Ordinal)
        {
            [columnName] = new ColumnPatternFilter(pattern, useRegex),
        };
        return RowQueryEngine.Filter(
            rows,
            RowPredicateSet.Compile(SearchQuery.Empty, null, filters, fileIndex.Header),
            fileIndex, overlay, cache);
    }
}
