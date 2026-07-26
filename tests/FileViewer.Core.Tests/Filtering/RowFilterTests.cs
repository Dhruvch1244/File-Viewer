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

    [Fact]
    public async Task GetDistinctValues_ReturnsSortedUniqueValuesAcrossRows()
    {
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("minimal_valid.dif"));
        var overlay = new EditOverlay();
        var cache = new DecodedRowCache();

        // _ERR is "0" for all three rows in this fixture — should collapse to a single value.
        List<string> errValues = RowFilter.GetDistinctValues([0, 1, 2], "_ERR", index, overlay, cache);
        Assert.Equal(["0"], errValues);

        List<string> priceValues = RowFilter.GetDistinctValues([0, 1, 2], "PRICE", index, overlay, cache);
        Assert.Equal(["100.50", "101.25", "99.75"], priceValues); // ordinal sort, not numeric
    }

    [Fact]
    public async Task GetDistinctValues_ReflectsEditedValues()
    {
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("minimal_valid.dif"));
        var overlay = new EditOverlay();
        var cache = new DecodedRowCache();
        overlay.EditCell(0, "PRICE", "EDITED_VALUE");

        List<string> values = RowFilter.GetDistinctValues([0, 1, 2], "PRICE", index, overlay, cache);

        Assert.Contains("EDITED_VALUE", values);
        Assert.DoesNotContain("100.50", values); // row 0's original value no longer present
    }

    [Fact]
    public async Task GetDistinctValues_UnknownColumn_ReturnsEmpty()
    {
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("minimal_valid.dif"));
        var overlay = new EditOverlay();
        var cache = new DecodedRowCache();

        List<string> values = RowFilter.GetDistinctValues([0, 1, 2], "NO_SUCH_COLUMN", index, overlay, cache);

        Assert.Empty(values);
    }

    [Fact]
    public async Task FilterByColumnValues_KeepsOnlyMatchingRows()
    {
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("minimal_valid.dif"));
        var overlay = new EditOverlay();
        var cache = new DecodedRowCache();

        List<long> result = RowFilter.FilterByColumnValues(
            [0, 1, 2], "PRICE", new HashSet<string> { "100.50", "99.75" }, index, overlay, cache);

        Assert.Equal([0L, 2L], result);
    }

    [Fact]
    public async Task FilterByColumnValues_SkipsDeletedRows()
    {
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("minimal_valid.dif"));
        var overlay = new EditOverlay();
        var cache = new DecodedRowCache();
        overlay.DeleteRow(0);

        List<long> result = RowFilter.FilterByColumnValues(
            [0, 1, 2], "PRICE", new HashSet<string> { "100.50", "99.75" }, index, overlay, cache);

        Assert.Equal([2L], result); // row 0 matched the value but is deleted
    }
}
