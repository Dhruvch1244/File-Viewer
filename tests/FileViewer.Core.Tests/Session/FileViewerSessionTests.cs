using FileViewer.Core.Export;
using FileViewer.Core.Session;
using FileViewer.Core.Sorting;

namespace FileViewer.Core.Tests.Session;

public class FileViewerSessionTests
{
    private static string FixturePath(string fileName) => Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

    [Fact]
    public async Task OpenAsync_IndexesFileAndAllowsResolvingRows()
    {
        using FileViewerSession session = await FileViewerSession.OpenAsync(FixturePath("minimal_valid.dif"));

        Assert.True(session.FileIndex.Header.IsValid);
        Assert.Equal("SEC001 HK Equity", session.Resolve(0)!.FieldValues[0]);
    }

    [Fact]
    public async Task ApplySort_ChangesExportOrder_ClearSort_RestoresFileOrder()
    {
        using FileViewerSession session = await FileViewerSession.OpenAsync(FixturePath("minimal_valid.dif"));

        session.ApplySort(SortDirection.Descending);
        Assert.Equal([2L, 1L, 0L], session.GetExportRowOrder());

        session.ClearSort();
        Assert.Equal([0L, 1L, 2L], session.GetExportRowOrder());
    }

    [Fact]
    public async Task GetExportRowOrder_IncludesLiveAddedRows()
    {
        // GetExportRowOrder enumerates candidate row indices only — a deleted base row is still
        // included here (it's Resolve/Export's job to skip tombstones), consistent with
        // ExportRunner.FileOrderWithAddedRows. This test covers the added-row appending part;
        // deleted-row exclusion from actual output is covered by ExportRoundTripTests.
        using FileViewerSession session = await FileViewerSession.OpenAsync(FixturePath("minimal_valid.dif"));

        session.Overlay.DeleteRow(1);
        long addedIndex = session.Overlay.AddRow(session.FileIndex.Header.ColumnNames);

        Assert.Equal([0L, 1L, 2L, addedIndex], session.GetExportRowOrder());
    }

    [Fact]
    public async Task Export_ExcludesDeletedRowsFromActualOutput()
    {
        using FileViewerSession session = await FileViewerSession.OpenAsync(FixturePath("minimal_valid.dif"));
        session.Overlay.DeleteRow(1);

        using var stream = new MemoryStream();
        session.Export(stream, new CsvExporter());
        string csv = System.Text.Encoding.UTF8.GetString(stream.ToArray());

        Assert.DoesNotContain("SEC002", csv);
        Assert.Contains("SEC001", csv);
        Assert.Contains("SEC003", csv);
    }

    [Fact]
    public async Task Export_ProducesCsvReflectingCurrentOverlayState()
    {
        using FileViewerSession session = await FileViewerSession.OpenAsync(FixturePath("minimal_valid.dif"));
        session.Overlay.EditCell(0, "PRICE", "1.23");

        using var stream = new MemoryStream();
        session.Export(stream, new CsvExporter());
        string csv = System.Text.Encoding.UTF8.GetString(stream.ToArray());

        Assert.Contains("1.23", csv);
    }

    [Fact]
    public async Task GeneratePreview_ReturnsFirstTwoRows()
    {
        using FileViewerSession session = await FileViewerSession.OpenAsync(FixturePath("minimal_valid.dif"));

        string preview = session.GeneratePreview(new CsvExporter());
        string[] lines = preview.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length); // header + 2 rows
    }

    [Fact]
    public async Task Dispose_CalledTwice_DoesNotThrow()
    {
        FileViewerSession session = await FileViewerSession.OpenAsync(FixturePath("minimal_valid.dif"));

        session.Dispose();
        var exception = Record.Exception(session.Dispose);

        Assert.Null(exception);
    }
}
