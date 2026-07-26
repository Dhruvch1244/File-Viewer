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
}
