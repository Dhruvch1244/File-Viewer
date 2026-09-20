using System.Text;
using FileViewer.Core.Dif;
using FileViewer.Core.Export;
using FileViewer.Core.Indexing;
using FileViewer.Core.Overlay;
using FileViewer.Core.Session;

namespace FileViewer.Core.Tests.Indexing;

public class BulkSectionIndexingTests
{
    private static string FixturePath(string fileName) => Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

    [Fact]
    public async Task IndexAsync_BulkFile_IndexesTheFirstSectionOnly()
    {
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("bulk_multi_section.dif"));

        Assert.True(index.Header.IsMultiSection);
        Assert.Equal("DVD_HIST", index.Header.SectionName);
        Assert.Equal(2, (long)index.RowIndex.Count);
    }

    [Fact]
    public async Task IndexSectionAsync_ReusesTheScannedLayoutAndIndexesThatSectionsRows()
    {
        DifFileLayout layout = await FileIndexer.ScanLayoutAsync(FixturePath("bulk_multi_section.dif"));
        using FileIndex index = await FileIndexer.IndexSectionAsync(FixturePath("bulk_multi_section.dif"), layout, 1);

        Assert.Equal("CALL_SCHEDULE", index.Header.SectionName);
        Assert.Equal(3, (long)index.RowIndex.Count);
        Assert.Equal(["_ID", "_ERR", "_SIZE", "CALL_DATE", "CALL_PRICE"], index.Header.ColumnNames);
    }

    [Fact]
    public async Task OpenSectionAsync_ResolvesRowsOfTheRequestedSection()
    {
        DifFileLayout layout = await FileIndexer.ScanLayoutAsync(FixturePath("bulk_multi_section.dif"));
        using FileViewerSession session = await FileViewerSession.OpenSectionAsync(FixturePath("bulk_multi_section.dif"), layout, 1);

        Assert.Equal(2, session.Sections.Count);
        Assert.Equal(1, session.SectionIndex);

        ResolvedRow? first = session.Resolve(0);
        Assert.NotNull(first);
        Assert.Equal("SEC3 Corp", first!.FieldValues[0]);
        Assert.Equal("20270601", first.FieldValues[3]);
    }

    [Fact]
    public async Task ScanLayoutAsync_OrdinaryFile_ReportsASingleSection()
    {
        DifFileLayout layout = await FileIndexer.ScanLayoutAsync(FixturePath("FixedIncomeAsia.dif"));

        Assert.True(layout.IsValid);
        Assert.False(layout.IsMultiSection);
        Assert.Single(layout.Sections);
    }

    [Fact]
    public async Task DifExport_OfOneBulkSection_ProducesAValidSingleSectionFile()
    {
        DifFileLayout layout = await FileIndexer.ScanLayoutAsync(FixturePath("bulk_multi_section.dif"));
        using FileViewerSession session = await FileViewerSession.OpenSectionAsync(FixturePath("bulk_multi_section.dif"), layout, 1);

        using var buffer = new MemoryStream();
        session.Export(buffer, new DifExporter(session.FileIndex));
        byte[] exported = buffer.ToArray();

        DifFileHeader reparsed = DifHeaderParser.Parse(exported);
        Assert.True(reparsed.IsValid);
        Assert.Equal(["_ID", "_ERR", "_SIZE", "CALL_DATE", "CALL_PRICE"], reparsed.ColumnNames);

        string text = Encoding.UTF8.GetString(exported);
        Assert.Contains("DATA=CALL_SCHEDULE", text);
        Assert.Contains("SEC3 Corp", text);
        // The other section's field list and rows are left behind entirely.
        Assert.DoesNotContain("DVD_HIST", text);
        Assert.DoesNotContain("SEC1 Equity", text);
        Assert.EndsWith("DATARECORDS=5\n", text);
    }

    [Fact]
    public async Task DifExport_OfAnOrdinaryFile_IsStillAByteForByteRoundTrip()
    {
        using FileViewerSession session = await FileViewerSession.OpenAsync(FixturePath("FixedIncomeAsia.dif"));

        using var buffer = new MemoryStream();
        session.Export(buffer, new DifExporter(session.FileIndex));

        Assert.Equal(await File.ReadAllBytesAsync(FixturePath("FixedIncomeAsia.dif")), buffer.ToArray());
    }

    [Fact]
    public async Task Export_WithAnExplicitRowOrder_WritesOnlyThoseRowsInThatOrder()
    {
        // What "export what the grid is showing" relies on: the caller hands over the filtered,
        // sorted row order and gets exactly that back.
        using FileViewerSession session = await FileViewerSession.OpenAsync(FixturePath("minimal_valid.dif"));

        using var buffer = new MemoryStream();
        session.Export(buffer, new CsvExporter(), [2L, 0L]);

        string[] lines = Encoding.UTF8.GetString(buffer.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length); // header + the two requested rows
        Assert.Contains("SEC003", lines[1]);
        Assert.Contains("SEC001", lines[2]);
        Assert.DoesNotContain(lines, line => line.Contains("SEC002"));
    }

    [Fact]
    public async Task GeneratePreview_WithAnExplicitRowOrder_PreviewsTheSameRowsTheExportWouldWrite()
    {
        using FileViewerSession session = await FileViewerSession.OpenAsync(FixturePath("minimal_valid.dif"));

        string preview = session.GeneratePreview(new CsvExporter(), [2L, 0L]);

        Assert.Contains("SEC003", preview);
        Assert.DoesNotContain("SEC002", preview);
    }
}
