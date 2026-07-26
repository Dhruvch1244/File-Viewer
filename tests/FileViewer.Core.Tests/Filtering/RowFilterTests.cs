using FileViewer.Core.Caching;
using FileViewer.Core.Filtering;
using FileViewer.Core.Indexing;
using FileViewer.Core.Overlay;

namespace FileViewer.Core.Tests.Filtering;

public class RowFilterTests
{
    private static string FixturePath(string fileName) => Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

    [Fact]
    public async Task Filter_EmptySearchText_ReturnsAllRowsUnchanged()
    {
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("minimal_valid.dif"));
        var overlay = new EditOverlay();
        var cache = new DecodedRowCache();

        List<long> result = RowFilter.Filter([0, 1, 2], "", index, overlay, cache);

        Assert.Equal([0L, 1L, 2L], result);
    }

    [Fact]
    public async Task Filter_MatchesAnyColumnCaseInsensitively()
    {
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("minimal_valid.dif"));
        var overlay = new EditOverlay();
        var cache = new DecodedRowCache();

        List<long> result = RowFilter.Filter([0, 1, 2], "sec002", index, overlay, cache);

        Assert.Equal([1L], result);
    }

    [Fact]
    public async Task Filter_MatchesEditedValueNotOriginalFileContent()
    {
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("minimal_valid.dif"));
        var overlay = new EditOverlay();
        var cache = new DecodedRowCache();
        overlay.EditCell(0, "PRICE", "UNIQUE_MARKER");

        List<long> result = RowFilter.Filter([0, 1, 2], "UNIQUE_MARKER", index, overlay, cache);

        Assert.Equal([0L], result);
    }

    [Fact]
    public async Task Filter_SkipsDeletedRows()
    {
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("minimal_valid.dif"));
        var overlay = new EditOverlay();
        var cache = new DecodedRowCache();
        overlay.DeleteRow(1);

        List<long> result = RowFilter.Filter([0, 1, 2], "SEC", index, overlay, cache);

        Assert.Equal([0L, 2L], result);
    }

    [Fact]
    public async Task Filter_NoMatches_ReturnsEmptyList()
    {
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("minimal_valid.dif"));
        var overlay = new EditOverlay();
        var cache = new DecodedRowCache();

        List<long> result = RowFilter.Filter([0, 1, 2], "NO_SUCH_VALUE", index, overlay, cache);

        Assert.Empty(result);
    }
}
