using FileViewer.Core.Caching;

namespace FileViewer.Core.Tests.Caching;

public class DecodedRowCacheTests
{
    private static DecodedRow MakeRow(long rowIndex) =>
        new(rowIndex, [$"field{rowIndex}"], RowRenderState.Normal);

    [Fact]
    public void TryGet_OnEmptyCache_ReturnsFalse()
    {
        var cache = new DecodedRowCache(capacity: 4);

        Assert.False(cache.TryGet(0, out _));
    }

    [Fact]
    public void Set_ThenTryGet_ReturnsTheSameRow()
    {
        var cache = new DecodedRowCache(capacity: 4);
        DecodedRow row = MakeRow(42);

        cache.Set(42, row);

        Assert.True(cache.TryGet(42, out DecodedRow? retrieved));
        Assert.Same(row, retrieved);
    }

    [Fact]
    public void Set_BeyondCapacity_EvictsLeastRecentlyUsed()
    {
        var cache = new DecodedRowCache(capacity: 2);

        cache.Set(1, MakeRow(1));
        cache.Set(2, MakeRow(2));
        cache.Set(3, MakeRow(3)); // evicts row 1 (least recently used)

        Assert.False(cache.TryGet(1, out _));
        Assert.True(cache.TryGet(2, out _));
        Assert.True(cache.TryGet(3, out _));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void TryGet_PromotesEntryAndPreventsItsEviction()
    {
        var cache = new DecodedRowCache(capacity: 2);
        cache.Set(1, MakeRow(1));
        cache.Set(2, MakeRow(2));

        cache.TryGet(1, out _); // row 1 is now most-recently-used; row 2 becomes LRU

        cache.Set(3, MakeRow(3)); // should evict row 2, not row 1

        Assert.True(cache.TryGet(1, out _));
        Assert.False(cache.TryGet(2, out _));
        Assert.True(cache.TryGet(3, out _));
    }

    [Fact]
    public void Set_ExistingKey_UpdatesValueWithoutGrowingBeyondCapacity()
    {
        var cache = new DecodedRowCache(capacity: 2);
        cache.Set(1, MakeRow(1));
        cache.Set(2, MakeRow(2));

        DecodedRow updated = MakeRow(1) with { FieldValues = ["updated"] };
        cache.Set(1, updated);

        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGet(1, out DecodedRow? retrieved));
        Assert.Same(updated, retrieved);
    }

    [Fact]
    public void Invalidate_RemovesEntry()
    {
        var cache = new DecodedRowCache(capacity: 4);
        cache.Set(1, MakeRow(1));

        cache.Invalidate(1);

        Assert.False(cache.TryGet(1, out _));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Invalidate_UnknownKey_IsNoOp()
    {
        var cache = new DecodedRowCache(capacity: 4);

        var exception = Record.Exception(() => cache.Invalidate(999));

        Assert.Null(exception);
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        var cache = new DecodedRowCache(capacity: 4);
        cache.Set(1, MakeRow(1));
        cache.Set(2, MakeRow(2));

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet(1, out _));
    }

    [Fact]
    public void Constructor_NonPositiveCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecodedRowCache(capacity: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DecodedRowCache(capacity: -1));
    }
}
