using BenchmarkDotNet.Attributes;
using FileViewer.Core.Caching;
using FileViewer.Core.Filtering;
using FileViewer.Core.Indexing;
using FileViewer.Core.Overlay;
using FileViewer.Core.Sorting;

namespace FileViewer.Benchmarks;

/// <summary>
/// Sort (permutes the pre-extracted "_ID" SortKey array — expected near-instant even at scale) vs.
/// filter (decodes every candidate row through the cache — the deliberately slower path PRS §8
/// budgets "low single-digit seconds" for). Filter uses the smaller 10 MB fixture: at true 500 MB+
/// scale, a decode-every-row filter is by design slow, and BenchmarkDotNet's repeated-iteration
/// model would make that prohibitively slow to run routinely.
/// </summary>
[MemoryDiagnoser]
public class SortFilterBenchmarks
{
    private FileIndex _sortIndex = null!;
    private FileIndex _filterIndex = null!;
    private EditOverlay _overlay = null!;
    private DecodedRowCache _cache = null!;
    private string _sortPath = null!;
    private string _filterPath = null!;
    private long[] _filterCandidateRows = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _sortPath = SyntheticDifFileGenerator.Generate(SyntheticDifFileGenerator.RowCount500Mb);
        _sortIndex = await FileIndexer.IndexAsync(_sortPath);

        _filterPath = SyntheticDifFileGenerator.Generate(SyntheticDifFileGenerator.RowCount10Mb);
        _filterIndex = await FileIndexer.IndexAsync(_filterPath);
        _filterCandidateRows = [.. Enumerable.Range(0, checked((int)_filterIndex.RowIndex.Count)).Select(i => (long)i)];

        _overlay = new EditOverlay();
        _cache = new DecodedRowCache();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _sortIndex.Dispose();
        _filterIndex.Dispose();
        File.Delete(_sortPath);
        File.Delete(_filterPath);
    }

    [Benchmark]
    public void SortByIdAscending_500Mb()
    {
        using var sorted = RowSorter.SortByKey(_sortIndex.SortKeys, SortDirection.Ascending);
    }

    [Benchmark]
    public void FilterSubstring_10Mb()
    {
        RowFilter.Filter(_filterCandidateRows, "SEC000100000", _filterIndex, _overlay, _cache);
    }

    /// <summary>Two search terms at once — the multi-term main search, still one pass over the rows.</summary>
    [Benchmark]
    public void FilterTwoTerms_10Mb()
    {
        var query = new SearchQuery([new SearchCriterion("SEC0001"), new SearchCriterion("USD")]);
        RowFilter.Filter(_filterCandidateRows, query, _filterIndex, _overlay, _cache);
    }

    /// <summary>
    /// The same kind of test scoped to one column. Scoping rules out the raw-bytes fast path, so
    /// this is the cost of fully decoding every candidate row — the contrast with
    /// <see cref="FilterSubstring_10Mb"/> is what that fast path is worth.
    /// </summary>
    [Benchmark]
    public void FilterColumnScoped_10Mb()
    {
        var query = new SearchQuery([new SearchCriterion("USD", SearchMatchMode.Equals, "CURRENCY")]);
        RowFilter.Filter(_filterCandidateRows, query, _filterIndex, _overlay, _cache);
    }

    /// <summary>A search term plus two column filters, compiled together and evaluated in one pass over the rows.</summary>
    [Benchmark]
    public void SearchPlusTwoColumnFilters_OnePass_10Mb()
    {
        RowPredicateSet predicates = RowPredicateSet.Compile(
            new SearchQuery([new SearchCriterion("SEC0001")]),
            new Dictionary<string, HashSet<string>> { ["CURRENCY"] = ["USD"] },
            new Dictionary<string, ColumnPatternFilter> { ["PRICE"] = new("^10", UseRegex: true) },
            _filterIndex.Header);

        RowQueryEngine.Filter(_filterCandidateRows, predicates, _filterIndex, _overlay, _cache);
    }

    /// <summary>
    /// The same three filters applied one after another, each re-walking (and re-decoding) the
    /// previous one's survivors — how the grid used to combine filters, kept here as the baseline
    /// <see cref="SearchPlusTwoColumnFilters_OnePass_10Mb"/> is measured against.
    /// </summary>
    [Benchmark]
    public void SearchPlusTwoColumnFilters_SeparatePasses_10Mb()
    {
        List<long> rows = RowFilter.Filter(_filterCandidateRows, "SEC0001", _filterIndex, _overlay, _cache);
        rows = RowFilter.FilterByColumnValues(rows, "CURRENCY", new HashSet<string> { "USD" }, _filterIndex, _overlay, _cache);
        RowFilter.FilterByColumnPattern(rows, "PRICE", "^10", useRegex: true, _filterIndex, _overlay, _cache);
    }

    /// <summary>Distinct values of a high-cardinality column — the Excel-style filter popup's lookup, capped so it can't build a set the size of the file.</summary>
    [Benchmark]
    public void DistinctValues_HighCardinalityColumn_10Mb()
    {
        RowQueryEngine.GetDistinctValues(_filterCandidateRows, "_ID", _filterIndex, _overlay, _cache);
    }
}
