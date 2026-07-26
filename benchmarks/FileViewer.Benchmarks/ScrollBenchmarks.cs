using BenchmarkDotNet.Attributes;
using FileViewer.Core.Caching;
using FileViewer.Core.Indexing;
using FileViewer.Core.Overlay;

namespace FileViewer.Benchmarks;

/// <summary>
/// Frame-time proxy: resolving a screen's worth of rows via <see cref="RowResolver"/> repeatedly,
/// simulating a scroll pass through a 500 MB file. This is a proxy for 60fps scroll (PRS §8) — the
/// real acceptance check still needs a profiler run (dotnet-trace/PerfView) against the actual WPF
/// app, since this measures resolve throughput, not WPF layout/render cost.
/// </summary>
[MemoryDiagnoser]
public class ScrollBenchmarks
{
    private const int VisibleRowsPerScreen = 40;

    private FileIndex _index = null!;
    private EditOverlay _overlay = null!;
    private DecodedRowCache _cache = null!;
    private string _path = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _path = SyntheticDifFileGenerator.Generate(SyntheticDifFileGenerator.RowCount500Mb);
        _index = await FileIndexer.IndexAsync(_path);
        _overlay = new EditOverlay();
        _cache = new DecodedRowCache();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _index.Dispose();
        File.Delete(_path);
    }

    [Benchmark]
    public void ResolveOneScreenful()
    {
        long rowCount = (long)_index.RowIndex.Count;
        long start = Random.Shared.NextInt64(0, Math.Max(1, rowCount - VisibleRowsPerScreen));
        for (long i = start; i < start + VisibleRowsPerScreen && i < rowCount; i++)
        {
            RowResolver.Resolve(i, _index, _overlay, _cache);
        }
    }
}
