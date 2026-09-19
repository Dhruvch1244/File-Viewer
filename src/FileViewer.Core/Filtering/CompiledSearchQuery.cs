using System.Text;
using System.Text.RegularExpressions;
using FileViewer.Core.Dif;

namespace FileViewer.Core.Filtering;

/// <summary>
/// A <see cref="SearchQuery"/> prepared once for a whole scan: column names resolved to indices,
/// regexes compiled, comparison types picked, and — where the query allows it — a raw-byte form of
/// each term so a row can be tested without ever being split into fields or decoded into strings.
///
/// Doing this per query rather than per row is most of the point. The previous design re-resolved a
/// column name and re-parsed a regex inside the row loop, which at a few million rows dominates the
/// actual matching.
/// </summary>
public sealed class CompiledSearchQuery
{
    private readonly CompiledCriterion[] _criteria;
    private readonly SearchCombineMode _combine;

    private CompiledSearchQuery(CompiledCriterion[] criteria, SearchCombineMode combine, bool supportsRawMatching)
    {
        _criteria = criteria;
        _combine = combine;
        SupportsRawMatching = supportsRawMatching;

        // An AND of terms can be *partially* answered on raw bytes even when some term needs the
        // decoded row: any raw-capable term that fails rejects the row on its own.
        HasRawPrefilter = combine == SearchCombineMode.All && criteria.Any(c => c.RawNeedle is not null);
    }

    public static readonly CompiledSearchQuery Empty = new([], SearchCombineMode.All, supportsRawMatching: false);

    public bool IsEmpty => _criteria.Length == 0;

    /// <summary>
    /// True when every term can be answered from a row's raw bytes alone — see
    /// <see cref="MatchesRaw"/>. Requires all terms to be plain (non-regex) substring tests over
    /// every column, with ASCII text that contains no delimiter (a term containing the delimiter
    /// could otherwise match across a field boundary, which "contains in some column" must not).
    /// </summary>
    public bool SupportsRawMatching { get; }

    /// <summary>
    /// True when <see cref="RawPrefilterRejects"/> can throw rows out before they are decoded. This
    /// is what keeps a selective text term cheap even when it is combined with column filters that
    /// do need the decoded row: the term rejects almost everything straight from the bytes, and only
    /// what survives is ever split into fields.
    /// </summary>
    public bool HasRawPrefilter { get; }

    /// <summary>
    /// True if the row can be rejected on its raw bytes alone. Only meaningful when
    /// <see cref="HasRawPrefilter"/> is set (the query is an AND), and only valid for a row with no
    /// overlay edits. A false result means "not rejected yet", not "matched" — the caller still has
    /// to evaluate the rest against the decoded row.
    /// </summary>
    public bool RawPrefilterRejects(ReadOnlySpan<byte> rowBytes)
    {
        foreach (CompiledCriterion criterion in _criteria)
        {
            if (criterion.RawNeedle is null) continue;

            bool contains = criterion.CaseSensitive
                ? rowBytes.IndexOf(criterion.RawNeedle) >= 0
                : AsciiByteSearch.ContainsIgnoreCase(rowBytes, criterion.RawNeedle);
            bool matched = criterion.Mode == SearchMatchMode.NotContains ? !contains : contains;
            if (!matched) return true;
        }
        return false;
    }

    public static CompiledSearchQuery Compile(SearchQuery query, DifFileHeader header)
    {
        if (query.IsEmpty) return Empty;

        var compiled = new List<CompiledCriterion>(query.Criteria.Count);
        bool supportsRaw = true;

        foreach (SearchCriterion criterion in query.Criteria)
        {
            if (criterion.IsEmpty) continue;

            int columnIndex = criterion.ColumnName is null ? -1 : header.ColumnIndexOf(criterion.ColumnName);
            // A term naming a column this section doesn't have can never match — keep it (rather
            // than dropping it) so an "All" query correctly yields nothing instead of silently
            // widening, and an "Any" query isn't skewed by it either.
            bool columnMissing = criterion.ColumnName is not null && columnIndex < 0;

            Regex? regex = null;
            if (criterion.Mode == SearchMatchMode.Regex)
            {
                RegexOptions options = RegexOptions.CultureInvariant
                    | (criterion.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
                try
                {
                    regex = new Regex(criterion.Text, options);
                }
                catch (ArgumentException)
                {
                    // An invalid pattern matches nothing rather than throwing mid-scan — the UI
                    // validates separately so it can flag the box while the user is still typing.
                    regex = null;
                }
            }

            byte[]? rawNeedle = null;
            bool rawUsable = criterion.ColumnName is null
                && criterion.Mode is SearchMatchMode.Contains or SearchMatchMode.NotContains
                && IsAscii(criterion.Text)
                && !criterion.Text.Contains(header.Delimiter)
                && !criterion.Text.Contains('\n')
                && !criterion.Text.Contains('\r');
            if (rawUsable)
            {
                rawNeedle = Encoding.ASCII.GetBytes(criterion.CaseSensitive ? criterion.Text : criterion.Text.ToUpperInvariant());
            }
            else
            {
                supportsRaw = false;
            }

            compiled.Add(new CompiledCriterion(
                criterion.Text,
                criterion.Mode,
                columnIndex,
                columnMissing,
                criterion.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase,
                criterion.CaseSensitive,
                regex,
                rawNeedle));
        }

        return compiled.Count == 0
            ? Empty
            : new CompiledSearchQuery([.. compiled], query.Combine, supportsRaw && compiled.Count > 0);
    }

    /// <summary>Evaluates the query against a row's resolved field values.</summary>
    public bool Matches(IReadOnlyList<string> fields)
    {
        if (_criteria.Length == 0) return true;

        foreach (CompiledCriterion criterion in _criteria)
        {
            bool matched = criterion.Matches(fields);
            if (_combine == SearchCombineMode.All)
            {
                if (!matched) return false;
            }
            else if (matched)
            {
                return true;
            }
        }
        return _combine == SearchCombineMode.All;
    }

    /// <summary>
    /// Evaluates the query straight against a row's raw file bytes — no field splitting, no string
    /// allocation. Only valid when <see cref="SupportsRawMatching"/> is true and the row has no
    /// overlay edits (its bytes are what the user sees).
    /// </summary>
    public bool MatchesRaw(ReadOnlySpan<byte> rowBytes)
    {
        if (_criteria.Length == 0) return true;

        foreach (CompiledCriterion criterion in _criteria)
        {
            bool contains = criterion.CaseSensitive
                ? rowBytes.IndexOf(criterion.RawNeedle!) >= 0
                : AsciiByteSearch.ContainsIgnoreCase(rowBytes, criterion.RawNeedle!);
            bool matched = criterion.Mode == SearchMatchMode.NotContains ? !contains : contains;

            if (_combine == SearchCombineMode.All)
            {
                if (!matched) return false;
            }
            else if (matched)
            {
                return true;
            }
        }
        return _combine == SearchCombineMode.All;
    }

    private static bool IsAscii(string text)
    {
        foreach (char c in text)
        {
            if (c > 127) return false;
        }
        return true;
    }

    private readonly struct CompiledCriterion(
        string text,
        SearchMatchMode mode,
        int columnIndex,
        bool columnMissing,
        StringComparison comparison,
        bool caseSensitive,
        Regex? regex,
        byte[]? rawNeedle)
    {
        public SearchMatchMode Mode { get; } = mode;
        public bool CaseSensitive { get; } = caseSensitive;
        public byte[]? RawNeedle { get; } = rawNeedle;

        public bool Matches(IReadOnlyList<string> fields)
        {
            if (columnMissing)
            {
                // Nothing to compare against: a positive test fails, a negative one trivially holds.
                return Mode is SearchMatchMode.NotContains or SearchMatchMode.NotEquals;
            }

            if (columnIndex >= 0)
            {
                return MatchesValue(columnIndex < fields.Count ? fields[columnIndex] : string.Empty);
            }

            // "Any column" — a negative term must hold for every column, a positive one for at least one.
            bool negated = Mode is SearchMatchMode.NotContains or SearchMatchMode.NotEquals;
            for (int i = 0; i < fields.Count; i++)
            {
                bool matched = MatchesValue(fields[i]);
                if (negated)
                {
                    if (!matched) return false;
                }
                else if (matched)
                {
                    return true;
                }
            }
            return negated;
        }

        private bool MatchesValue(string value) => Mode switch
        {
            SearchMatchMode.Contains => value.Contains(text, comparison),
            SearchMatchMode.NotContains => !value.Contains(text, comparison),
            SearchMatchMode.Equals => value.Equals(text, comparison),
            SearchMatchMode.NotEquals => !value.Equals(text, comparison),
            SearchMatchMode.StartsWith => value.StartsWith(text, comparison),
            SearchMatchMode.EndsWith => value.EndsWith(text, comparison),
            SearchMatchMode.Regex => regex is not null && regex.IsMatch(value),
            _ => value.Contains(text, comparison),
        };
    }
}
