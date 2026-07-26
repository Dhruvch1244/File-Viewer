using BenchmarkDotNet.Attributes;
using FileViewer.Core.Indexing;

namespace FileViewer.Benchmarks;

/// <summary>Index time across the 10 MB/500 MB/2 GB synthetic files (PRS §11). The 2 GB run is intentionally the slowest of this suite — run it deliberately, not as part of routine iteration.</summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 3)]
public class IndexingBenchmarks
{
    private string _path10Mb = null!;
    private string _path500Mb = null!;
    private string _path2Gb = null!;

    [GlobalSetup]
    public void Setup()
    {
        _path10Mb = SyntheticDifFileGenerator.Generate(SyntheticDifFileGenerator.RowCount10Mb);
        _path500Mb = SyntheticDifFileGenerator.Generate(SyntheticDifFileGenerator.RowCount500Mb);
        _path2Gb = SyntheticDifFileGenerator.Generate(SyntheticDifFileGenerator.RowCount2Gb);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        File.Delete(_path10Mb);
        File.Delete(_path500Mb);
        File.Delete(_path2Gb);
    }

    [Benchmark]
    public async Task Index_10Mb()
    {
        using FileIndex index = await FileIndexer.IndexAsync(_path10Mb);
    }

    [Benchmark]
    public async Task Index_500Mb()
    {
        using FileIndex index = await FileIndexer.IndexAsync(_path500Mb);
    }

    [Benchmark]
    public async Task Index_2Gb()
    {
        using FileIndex index = await FileIndexer.IndexAsync(_path2Gb);
    }
}
