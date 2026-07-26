using BenchmarkDotNet.Attributes;
using FileViewer.Core.Caching;
using FileViewer.Core.Export;
using FileViewer.Core.Indexing;
using FileViewer.Core.Overlay;

namespace FileViewer.Benchmarks;

/// <summary>Streaming export throughput for the two format families (plain-line DIF/CSV/TSV are structurally similar; JSON is included separately since <see cref="Utf8JsonWriter"/> has different cost characteristics).</summary>
[MemoryDiagnoser]
public class ExportBenchmarks
{
    private FileIndex _index = null!;
    private EditOverlay _overlay = null!;
    private string _path = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _path = SyntheticDifFileGenerator.Generate(SyntheticDifFileGenerator.RowCount10Mb);
        _index = await FileIndexer.IndexAsync(_path);
        _overlay = new EditOverlay();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _index.Dispose();
        File.Delete(_path);
    }

    [Benchmark]
    public void ExportCsv()
    {
        var cache = new DecodedRowCache();
        using var stream = new MemoryStream();
        using var exporter = new CsvExporter();
        ExportRunner.Export(stream, _index, _overlay, cache, ExportRunner.FileOrderWithAddedRows(_index, _overlay), exporter);
    }

    [Benchmark]
    public void ExportJson()
    {
        var cache = new DecodedRowCache();
        using var stream = new MemoryStream();
        using var exporter = new JsonExporter();
        ExportRunner.Export(stream, _index, _overlay, cache, ExportRunner.FileOrderWithAddedRows(_index, _overlay), exporter);
    }
}
