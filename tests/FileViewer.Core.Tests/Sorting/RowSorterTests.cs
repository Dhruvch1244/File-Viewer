using System.Text;
using FileViewer.Core.Native;
using FileViewer.Core.Sorting;

namespace FileViewer.Core.Tests.Sorting;

public class RowSorterTests
{
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
}
