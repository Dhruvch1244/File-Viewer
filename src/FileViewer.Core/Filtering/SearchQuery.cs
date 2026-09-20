namespace FileViewer.Core.Filtering;

/// <summary>How one search term is compared against a value.</summary>
public enum SearchMatchMode
{
    Contains,
    NotContains,
    Equals,
    NotEquals,
    StartsWith,
    EndsWith,
    Regex,
}

/// <summary>Whether a row has to satisfy every term of a <see cref="SearchQuery"/> or just one of them.</summary>
public enum SearchCombineMode
{
    All,
    Any,
}

/// <summary>
/// One term of the main search: the text to look for, how to compare it, and whether it applies to
/// a single column or to every column at once (<see cref="ColumnName"/> null = all columns, which
/// is what the plain "search everything" box does).
/// </summary>
/// <param name="Text">The text (or regex pattern) to match.</param>
/// <param name="Mode">How <paramref name="Text"/> is compared against a value.</param>
/// <param name="ColumnName">The column this term applies to, or null for "any column".</param>
/// <param name="CaseSensitive">False (the default) matches the way a user reading the grid would expect.</param>
public sealed record SearchCriterion(
    string Text,
    SearchMatchMode Mode = SearchMatchMode.Contains,
    string? ColumnName = null,
    bool CaseSensitive = false)
{
    public bool IsEmpty => string.IsNullOrEmpty(Text);

    /// <summary>Short human-readable form for the "filtered by" chips — e.g. <c>PRICE contains "95"</c>.</summary>
    public string Describe()
    {
        string scope = ColumnName ?? "Any column";
        string verb = Mode switch
        {
            SearchMatchMode.Contains => "contains",
            SearchMatchMode.NotContains => "does not contain",
            SearchMatchMode.Equals => "is",
            SearchMatchMode.NotEquals => "is not",
            SearchMatchMode.StartsWith => "starts with",
            SearchMatchMode.EndsWith => "ends with",
            SearchMatchMode.Regex => "matches",
            _ => "contains",
        };
        string suffix = CaseSensitive ? " (case-sensitive)" : string.Empty;
        return Mode == SearchMatchMode.Regex
            ? $"{scope} {verb} /{Text}/{suffix}"
            : $"{scope} {verb} \"{Text}\"{suffix}";
    }
}

/// <summary>
/// The main search box's full state: any number of <see cref="SearchCriterion"/> terms combined
/// with AND (<see cref="SearchCombineMode.All"/>) or OR (<see cref="SearchCombineMode.Any"/>).
///
/// A single plain substring search — what the box used to be limited to — is just the one-term
/// case, so nothing about the simple path changes shape.
/// </summary>
public sealed record SearchQuery(IReadOnlyList<SearchCriterion> Criteria, SearchCombineMode Combine = SearchCombineMode.All)
{
    public static readonly SearchQuery Empty = new([]);

    public bool IsEmpty => Criteria.Count == 0 || Criteria.All(c => c.IsEmpty);

    /// <summary>The plain "search every column for this substring" query.</summary>
    public static SearchQuery ForText(string? text) =>
        string.IsNullOrEmpty(text) ? Empty : new SearchQuery([new SearchCriterion(text)]);

    public SearchQuery Add(SearchCriterion criterion) => this with { Criteria = [.. Criteria, criterion] };

    public SearchQuery RemoveAt(int index) =>
        index < 0 || index >= Criteria.Count
            ? this
            : this with { Criteria = [.. Criteria.Where((_, i) => i != index)] };
}
