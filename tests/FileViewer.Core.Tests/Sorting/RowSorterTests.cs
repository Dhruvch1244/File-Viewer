using System.Text;
using FileViewer.Core.Caching;
using FileViewer.Core.Filtering;
using FileViewer.Core.Indexing;
using FileViewer.Core.Native;
using FileViewer.Core.Overlay;
using FileViewer.Core.Sorting;

namespace FileViewer.Core.Tests.Sorting;

public class RowSorterTests
{
    private static string FixturePath(string fileName) => Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

    private static SortKey MakeKey(string text, long rowIndex)
    {
        var key = new SortKey { RowIndex = rowIndex };
        key.SetKey(Encoding.ASCII.GetBytes(text));
        return key;
    }

    private static UnmanagedArray<SortKey> MakeArray(params (string Text, long RowIndex)[] entries)
    {
        var array = new UnmanagedArray<SortKey>((nuint)entries.Length);
        foreach ((string text, long rowIndex) in entries)
        {
            array.Add(MakeKey(text, rowIndex));
        }
        return array;
    }

    private static string[] KeysAsStrings(UnmanagedArray<SortKey> array)
    {
        var result = new string[array.Count];
        for (int i = 0; i < (int)array.Count; i++)
        {
            result[i] = Encoding.ASCII.GetString(array[i].GetKeySpan());
        }
        return result;
    }

    private static long[] RowIndices(UnmanagedArray<SortKey> array)
    {
        var result = new long[array.Count];
        for (int i = 0; i < (int)array.Count; i++)
        {
            result[i] = array[i].RowIndex;
        }
        return result;
    }

    [Fact]
    public void SortByKey_Ascending_OrdersLexicographicallyAndPermutesRowIndex()
    {
        using UnmanagedArray<SortKey> source = MakeArray(("SEC1002", 2), ("SEC1000", 0), ("SEC1001", 1));

        using UnmanagedArray<SortKey> sorted = RowSorter.SortByKey(source, SortDirection.Ascending);

        Assert.Equal(["SEC1000", "SEC1001", "SEC1002"], KeysAsStrings(sorted));
        Assert.Equal([0L, 1L, 2L], RowIndices(sorted));
    }

    [Fact]
    public void SortByKey_Descending_ReversesOrder()
    {
        using UnmanagedArray<SortKey> source = MakeArray(("SEC1002", 2), ("SEC1000", 0), ("SEC1001", 1));

        using UnmanagedArray<SortKey> sorted = RowSorter.SortByKey(source, SortDirection.Descending);

        Assert.Equal(["SEC1002", "SEC1001", "SEC1000"], KeysAsStrings(sorted));
        Assert.Equal([2L, 1L, 0L], RowIndices(sorted));
    }

    [Fact]
    public void SortByKey_DoesNotMutateSourceArray()
    {
        using UnmanagedArray<SortKey> source = MakeArray(("SEC1002", 2), ("SEC1000", 0), ("SEC1001", 1));

        using UnmanagedArray<SortKey> sorted = RowSorter.SortByKey(source, SortDirection.Ascending);

        Assert.Equal(["SEC1002", "SEC1000", "SEC1001"], KeysAsStrings(source));
    }

    [Fact]
    public void SortByKey_DuplicateKeys_AreStableByAscendingRowIndexRegardlessOfDirection()
    {
        // Three rows share the key "DUP"; their original (file) order is RowIndex 5, 1, 3.
        using UnmanagedArray<SortKey> source = MakeArray(("AAA", 0), ("DUP", 5), ("DUP", 1), ("DUP", 3), ("ZZZ", 9));

        using UnmanagedArray<SortKey> ascending = RowSorter.SortByKey(source, SortDirection.Ascending);
        using UnmanagedArray<SortKey> descending = RowSorter.SortByKey(source, SortDirection.Descending);

        // Regardless of primary sort direction, the tied "DUP" rows come out in ascending RowIndex order.
        Assert.Equal([0L, 1L, 3L, 5L, 9L], RowIndices(ascending));
        Assert.Equal([9L, 1L, 3L, 5L, 0L], RowIndices(descending));
    }

    [Fact]
    public void SortByKey_EmptySource_ProducesEmptyResult()
    {
        using var source = new UnmanagedArray<SortKey>(1);

        using UnmanagedArray<SortKey> sorted = RowSorter.SortByKey(source);

        Assert.Equal((nuint)0, sorted.Count);
    }

    [Fact]
    public async Task SortByColumn_NumericColumn_SortsByValueNotLexicographically()
    {
        // PRICE values are 100.50 / 101.25 / 99.75 for rows 0/1/2 — lexicographic order would put
        // "100.50" before "99.75"; numeric-aware sort must not.
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("minimal_valid.dif"));
        var overlay = new EditOverlay();
        var cache = new DecodedRowCache();

        List<long> ascending = RowSorter.SortByColumn([0, 1, 2], "PRICE", SortDirection.Ascending, index, overlay, cache);
        List<long> descending = RowSorter.SortByColumn([0, 1, 2], "PRICE", SortDirection.Descending, index, overlay, cache);

        Assert.Equal([2L, 0L, 1L], ascending);
        Assert.Equal([1L, 0L, 2L], descending);
    }

    [Fact]
    public async Task SortByColumn_ReflectsEditedValuesNotOriginalFileContent()
    {
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("minimal_valid.dif"));
        var overlay = new EditOverlay();
        var cache = new DecodedRowCache();
        overlay.EditCell(1, "PRICE", "1.00"); // row 1 was 101.25, now the smallest

        List<long> ascending = RowSorter.SortByColumn([0, 1, 2], "PRICE", SortDirection.Ascending, index, overlay, cache);

        Assert.Equal([1L, 2L, 0L], ascending);
    }

    [Fact]
    public async Task SortByColumn_UnknownColumnName_ReturnsInputUnchanged()
    {
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("minimal_valid.dif"));
        var overlay = new EditOverlay();
        var cache = new DecodedRowCache();

        List<long> result = RowSorter.SortByColumn([2, 0, 1], "NO_SUCH_COLUMN", SortDirection.Ascending, index, overlay, cache);

        Assert.Equal([2L, 0L, 1L], result);
    }

    [Fact]
    public async Task SortByColumn_TiedValues_KeepTheirOriginalRelativeOrder()
    {
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("minimal_valid.dif"));
        var overlay = new EditOverlay();
        var cache = new DecodedRowCache();

        // Every row has _ERR = "0", so the sort is all ties: the input order has to survive, in both
        // directions (the direction flips the comparison, never the tiebreak).
        Assert.Equal([2L, 0L, 1L], RowSorter.SortByColumn([2, 0, 1], "_ERR", SortDirection.Ascending, index, overlay, cache));
        Assert.Equal([2L, 0L, 1L], RowSorter.SortByColumn([2, 0, 1], "_ERR", SortDirection.Descending, index, overlay, cache));
    }

    [Fact]
    public async Task SortByColumn_LargeInput_ProducesAFullyOrderedResultOverTheSameRows()
    {
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("FixedIncomeAsia.dif"));
        var overlay = new EditOverlay();
        var cache = new DecodedRowCache();

        var rows = new List<long>();
        for (long i = 0; i < (long)index.RowIndex.Count; i++) rows.Add(i);

        // Repeat the rows until the parallel key-extraction path is the one that runs.
        var repeated = new List<long>();
        while (repeated.Count < RowQueryEngine.ParallelThresholdRows * 2) repeated.AddRange(rows);

        List<long> sorted = RowSorter.SortByColumn(repeated, "PRICE", SortDirection.Ascending, index, overlay, cache);

        Assert.Equal(repeated.Count, sorted.Count);
        Assert.Equal(repeated.OrderBy(r => r), sorted.OrderBy(r => r)); // same rows, just reordered

        int priceColumn = index.Header.ColumnIndexOf("PRICE");
        string Price(long rowIndex) => RowResolver.Resolve(rowIndex, index, overlay, cache)!.FieldValues[priceColumn];

        for (int i = 1; i < sorted.Count; i++)
        {
            Assert.True(
                NumericAwareStringComparer.Instance.Compare(Price(sorted[i - 1]), Price(sorted[i])) <= 0,
                $"Row at position {i} sorts before the row before it.");
        }
    }
}
