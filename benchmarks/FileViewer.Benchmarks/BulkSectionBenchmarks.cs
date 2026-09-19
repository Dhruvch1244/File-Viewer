using System.Text;
using BenchmarkDotNet.Attributes;
using FileViewer.Core.Dif;
using FileViewer.Core.Indexing;

namespace FileViewer.Benchmarks;

/// <summary>
/// The cost of opening a multi-section ("bulk") file, split into the two things that actually
/// happen: scanning the file's structure once (<see cref="FileIndexer.ScanLayoutAsync"/> — one
/// forward pass, jumping between sections with a vectorized marker search rather than parsing rows),
/// and indexing one section's rows (<see cref="FileIndexer.IndexSectionAsync"/>).
///
/// The split is the point of the design: a bulk file with N sections pays the scan once and then
/// only for the sections someone actually opens, instead of indexing all N up front to show one.
/// </summary>
[MemoryDiagnoser]
public class BulkSectionBenchmarks
{
    private const int SectionCount = 4;

    private string _bulkPath = null!;
    private DifFileLayout _layout = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _bulkPath = GenerateBulkFile(SectionCount, SyntheticDifFileGenerator.RowCount10Mb / SectionCount);
        _layout = await FileIndexer.ScanLayoutAsync(_bulkPath);
    }

    [GlobalCleanup]
    public void Cleanup() => File.Delete(_bulkPath);

    /// <summary>Walks the whole file once to find every section — what opening a bulk file costs before any row is indexed.</summary>
    [Benchmark]
    public async Task ScanSections()
    {
        DifFileLayout layout = await FileIndexer.ScanLayoutAsync(_bulkPath);
        _ = layout.Sections.Count;
    }

    /// <summary>Indexes a single section's rows against an already-scanned layout — what switching to another section costs.</summary>
    [Benchmark]
    public async Task IndexOneSection()
    {
        using FileIndex index = await FileIndexer.IndexSectionAsync(_bulkPath, _layout, SectionCount - 1);
        _ = index.RowIndex.Count;
    }

    private static string GenerateBulkFile(int sectionCount, int rowsPerSection)
    {
        string path = Path.Combine(Path.GetTempPath(), $"fileviewer-bench-bulk-{sectionCount}x{rowsPerSection}-{Guid.NewGuid():N}.dif");
        using var writer = new StreamWriter(path, append: false, Encoding.ASCII) { NewLine = "\n" };

        writer.WriteLine("IMAHDR");
        writer.WriteLine("START-OF-FILE");
        writer.WriteLine("FIRMNAME=benchmark");
        writer.WriteLine("DELIMITER=|");

        for (int section = 0; section < sectionCount; section++)
        {
            writer.WriteLine($"DATA=BULK_FIELD_{section}");
            writer.WriteLine("START-OF-FIELDS");
            writer.WriteLine("PRICE");
            writer.WriteLine("VOLUME");
            writer.WriteLine("CURRENCY");
            writer.WriteLine("END-OF-FIELDS");
            writer.WriteLine("START-OF-DATA");
            for (int i = 0; i < rowsPerSection; i++)
            {
                writer.WriteLine($"SEC{i:D9} HK Equity|0|3|{100 + i % 500}.{i % 100:D2}|{1000 + i}|USD");
            }
            writer.WriteLine("END-OF-DATA");
            writer.WriteLine($"DATARECORDS={rowsPerSection}");
        }

        writer.WriteLine("END-OF-FILE");
        writer.WriteLine("IMATRL");
        writer.WriteLine($"DATARECORDS={sectionCount * rowsPerSection}");

        return path;
    }
}
