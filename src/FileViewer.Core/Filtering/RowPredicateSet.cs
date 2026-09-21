using System.Text;
using System.Text.RegularExpressions;
using FileViewer.Core.Dif;

namespace FileViewer.Core.Filtering;

/// <summary>
/// Every filter currently in force — the main search query, the Excel-style per-column value
/// filters, and the per-column pattern filters — compiled together into one test a row is put
/// through exactly once.
///
/// This is the single biggest thing standing between the grid and a responsive 2 GB file. The
/// previous design applied each filter as its own pass: with a search term and two column filters
/// active, every surviving row was decoded three times, and the intermediate row-index lists were
/// rebuilt in between. One pass over the rows, one decode each, all predicates evaluated together —
/// and column names/regexes resolved here, once, instead of inside the row loop.
/// </summary>
public sealed class RowPredicateSet
{
    private readonly CompiledSearchQuery _search;
    private readonly ValuePredicate[] _valueFilters;
    private readonly PatternPredicate[] _patternFilters;

    private RowPredicateSet(CompiledSearchQuery search, ValuePredicate[] valueFilters, PatternPredicate[] patternFilters)
    {
        _search = search;
        _valueFilters = valueFilters;
        _patternFilters = patternFilters;
    }

    public static readonly RowPredicateSet Empty = new(CompiledSearchQuery.Empty, [], []);

    public bool IsEmpty => _search.IsEmpty && _valueFilters.Length == 0 && _patternFilters.Length == 0;

    /// <summary>True when the whole set can be answered from a row's raw bytes — see <see cref="CompiledSearchQuery.SupportsRawMatching"/>. Only the search query has a raw form; any column filter forces the decoded path.</summary>
    public bool SupportsRawMatching => _valueFilters.Length == 0 && _patternFilters.Length == 0 && _search.SupportsRawMatching;

    /// <summary>
    /// True when every active predicate is a column-scoped value/pattern filter and there is no
    /// free-text search term — the search scans every column, so it needs the full row decoded
    /// regardless, but a value/pattern filter only ever looks at its own column. When this holds,
    /// <see cref="MatchesColumnsOnly"/> can answer the row from just the columns those filters
    /// reference, instead of materializing every column as a string the way <see cref="Matches"/>'s
    /// caller (a full <c>DifRowParser.ParseRow</c>) does.
    /// </summary>
    public bool SupportsColumnScopedMatching => _search.IsEmpty;

    public static RowPredicateSet Compile(
        SearchQuery search,
        IReadOnlyDictionary<string, HashSet<string>>? columnValueFilters,
        IReadOnlyDictionary<string, ColumnPatternFilter>? columnPatternFilters,
        DifFileHeader header)
    {
        CompiledSearchQuery compiledSearch = CompiledSearchQuery.Compile(search, header);

        var values = new List<ValuePredicate>();
        if (columnValueFilters is not null)
        {
            foreach ((string columnName, HashSet<string> allowed) in columnValueFilters)
            {
                int columnIndex = header.ColumnIndexOf(columnName);
                if (columnIndex < 0) continue; // a column this section doesn't have filters nothing
                values.Add(new ValuePredicate(columnIndex, allowed));
            }
        }

        var patterns = new List<PatternPredicate>();
        if (columnPatternFilters is not null)
        {
            foreach ((string columnName, ColumnPatternFilter filter) in columnPatternFilters)
            {
                if (string.IsNullOrEmpty(filter.Pattern)) continue;
                int columnIndex = header.ColumnIndexOf(columnName);
                if (columnIndex < 0) continue;

                Regex? regex = null;
                bool invalidRegex = false;
                if (filter.UseRegex)
                {
                    invalidRegex = !RowFilter.TryCompileRegex(filter.Pattern, out regex, out _);
                }
                patterns.Add(new PatternPredicate(columnIndex, filter.Pattern, regex, invalidRegex));
            }
        }

        return new RowPredicateSet(compiledSearch, [.. values], [.. patterns]);
    }

    public bool Matches(IReadOnlyList<string> fields)
    {
        foreach (ValuePredicate predicate in _valueFilters)
        {
            if (!predicate.Matches(fields)) return false;
        }
        foreach (PatternPredicate predicate in _patternFilters)
        {
            if (!predicate.Matches(fields)) return false;
        }
        return _search.Matches(fields);
    }

    /// <summary>Only valid when <see cref="SupportsRawMatching"/> is true and the row carries no overlay edits.</summary>
    public bool MatchesRaw(ReadOnlySpan<byte> rowBytes) => _search.MatchesRaw(rowBytes);

    /// <summary>
    /// Matches using only the columns the active value/pattern filters reference, decoding just
    /// those fields instead of every column in the row (see <see cref="SupportsColumnScopedMatching"/>).
    /// Only valid when <see cref="SupportsColumnScopedMatching"/> is true and the row carries no
    /// overlay edits (an edited row's raw file bytes no longer reflect its current value).
    /// </summary>
    public bool MatchesColumnsOnly(ReadOnlySpan<byte> rowBytes, byte delimiter, Encoding encoding)
    {
        if (_valueFilters.Length == 0 && _patternFilters.Length == 0) return true;

        ReadOnlySpan<byte> trimmed = DifLineScanner.TrimTrailingCr(rowBytes);
        int fieldCount = DifRowParser.CountFields(trimmed, delimiter);
        Span<Range> ranges = fieldCount <= 64 ? stackalloc Range[fieldCount] : new Range[fieldCount];
        DifRowParser.SplitFieldsInto(trimmed, delimiter, ranges);

        foreach (ValuePredicate predicate in _valueFilters)
        {
            if (!predicate.MatchesRaw(trimmed, ranges, encoding)) return false;
        }
        foreach (PatternPredicate predicate in _patternFilters)
        {
            if (!predicate.MatchesRaw(trimmed, ranges, encoding)) return false;
        }
        return true;
    }

    /// <summary>True when some of the search can reject rows before they are decoded — see <see cref="CompiledSearchQuery.HasRawPrefilter"/>.</summary>
    public bool HasRawPrefilter => _search.HasRawPrefilter;

    /// <summary>Rejects a row on its raw bytes where the query allows it. A false result still has to be confirmed against the decoded row.</summary>
    public bool RawPrefilterRejects(ReadOnlySpan<byte> rowBytes) => _search.RawPrefilterRejects(rowBytes);

    private readonly struct ValuePredicate(int columnIndex, IReadOnlySet<string> allowedValues)
    {
        public bool Matches(IReadOnlyList<string> fields) =>
            allowedValues.Contains(columnIndex < fields.Count ? fields[columnIndex] : string.Empty);

        public bool MatchesRaw(ReadOnlySpan<byte> trimmedLine, ReadOnlySpan<Range> ranges, Encoding encoding) =>
            allowedValues.Contains(FieldValue(trimmedLine, ranges, columnIndex, encoding));
    }

    private readonly struct PatternPredicate(int columnIndex, string pattern, Regex? regex, bool invalidRegex)
    {
        public bool Matches(IReadOnlyList<string> fields)
        {
            if (invalidRegex) return false; // a pattern that doesn't compile matches nothing
            string value = columnIndex < fields.Count ? fields[columnIndex] : string.Empty;
            return regex is not null ? regex.IsMatch(value) : value.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        }

        public bool MatchesRaw(ReadOnlySpan<byte> trimmedLine, ReadOnlySpan<Range> ranges, Encoding encoding)
        {
            if (invalidRegex) return false;
            string value = FieldValue(trimmedLine, ranges, columnIndex, encoding);
            return regex is not null ? regex.IsMatch(value) : value.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string FieldValue(ReadOnlySpan<byte> trimmedLine, ReadOnlySpan<Range> ranges, int columnIndex, Encoding encoding) =>
        columnIndex < ranges.Length ? encoding.GetString(trimmedLine[ranges[columnIndex]]) : string.Empty;
}

/// <summary>A single column's text/regex filter — the ag-Grid-style "floating filter row" entry.</summary>
public readonly record struct ColumnPatternFilter(string Pattern, bool UseRegex);
