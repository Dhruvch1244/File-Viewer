using FileViewer.Core.Caching;
using FileViewer.Core.Export;
using FileViewer.Core.Indexing;
using FileViewer.Core.Overlay;

namespace FileViewer.Core.Tests.Export;

public class PreviewGeneratorTests
{
    private static string FixturePath(string fileName) => Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

    [Fact]
    public async Task GeneratePreview_MatchesFirstTwoRowsOfAFullExport()
    {
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("minimal_valid.dif"));
        var overlay = new EditOverlay();
        var previewCache = new DecodedRowCache();
        var exportCache = new DecodedRowCache();

        string preview = PreviewGenerator.GeneratePreview(
            index, overlay, previewCache, ExportRunner.FileOrderWithAddedRows(index, overlay), new CsvExporter());

        using var fullExportStream = new MemoryStream();
        ExportRunner.Export(fullExportStream, index, overlay, exportCache,
            ExportRunner.FileOrderWithAddedRows(index, overlay), new CsvExporter());
        string fullExport = System.Text.Encoding.UTF8.GetString(fullExportStream.ToArray());

        string[] previewLines = preview.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        string[] fullExportLines = fullExport.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        // Header + first 2 data rows only.
        Assert.Equal(3, previewLines.Length);
        Assert.Equal(fullExportLines[0], previewLines[0]); // header
        Assert.Equal(fullExportLines[1], previewLines[1]); // row 0
        Assert.Equal(fullExportLines[2], previewLines[2]); // row 1
    }

    [Fact]
    public async Task GeneratePreview_CapsAtTwoRowsEvenWithMoreAvailable()
    {
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("FixedIncomeAsia.dif")); // 50 rows
        var overlay = new EditOverlay();
        var cache = new DecodedRowCache();

        string preview = PreviewGenerator.GeneratePreview(
            index, overlay, cache, ExportRunner.FileOrderWithAddedRows(index, overlay), new CsvExporter());

        string[] lines = preview.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1 + PreviewGenerator.PreviewRowCount, lines.Length);
    }

    [Fact]
    public async Task GeneratePreview_SkipsDeletedRowsJustLikeARealExport()
    {
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("minimal_valid.dif"));
        var overlay = new EditOverlay();
        var cache = new DecodedRowCache();
        overlay.DeleteRow(0); // delete the row that would otherwise be first in the preview

        string preview = PreviewGenerator.GeneratePreview(
            index, overlay, cache, ExportRunner.FileOrderWithAddedRows(index, overlay), new CsvExporter());

        string[] lines = preview.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.DoesNotContain(lines, l => l.StartsWith("SEC001", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("SEC002", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("SEC003", StringComparison.Ordinal));
    }
}
