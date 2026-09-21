using System.Runtime.InteropServices;
using FileViewer.Core.Native;

namespace FileViewer.Core.Tests.Native;

public class UnmanagedArrayTests
{
    [StructLayout(LayoutKind.Sequential)]
    private struct TestItem
    {
        public int Value;
    }

    [Fact]
    public void Add_IncreasesCountAndStoresValue()
    {
        using var array = new UnmanagedArray<TestItem>(initialCapacity: 4);

        array.Add(new TestItem { Value = 42 });

        Assert.Equal((nuint)1, array.Count);
        Assert.Equal(42, array[0].Value);
    }

    [Fact]
    public void Add_BeyondInitialCapacity_GrowsAndPreservesExistingData()
    {
        using var array = new UnmanagedArray<TestItem>(initialCapacity: 2);
        const int itemCount = 10_000;

        for (int i = 0; i < itemCount; i++)
        {
            array.Add(new TestItem { Value = i });
        }

        Assert.Equal((nuint)itemCount, array.Count);
        Assert.True(array.Capacity >= (nuint)itemCount);
        for (int i = 0; i < itemCount; i++)
        {
            Assert.Equal(i, array[i].Value);
        }
    }

    [Fact]
    public void Indexer_OutOfRange_Throws()
    {
        using var array = new UnmanagedArray<TestItem>(initialCapacity: 4);
        array.Add(new TestItem { Value = 1 });

        Assert.Throws<ArgumentOutOfRangeException>(() => array[1]);
    }

    [Fact]
    public void RefIndexer_AllowsInPlaceMutation()
    {
        using var array = new UnmanagedArray<TestItem>(initialCapacity: 4);
        array.Add(new TestItem { Value = 1 });

        ref TestItem item = ref array[0L];
        item.Value = 99;

        Assert.Equal(99, array[0].Value);
    }

    [Fact]
    public void AsSpan_ReflectsCurrentCountAndValues()
    {
        using var array = new UnmanagedArray<TestItem>(initialCapacity: 4);
        array.Add(new TestItem { Value = 1 });
        array.Add(new TestItem { Value = 2 });
        array.Add(new TestItem { Value = 3 });

        Span<TestItem> span = array.AsSpan();

        Assert.Equal(3, span.Length);
        Assert.Equal(1, span[0].Value);
        Assert.Equal(2, span[1].Value);
        Assert.Equal(3, span[2].Value);
    }

    [Fact]
    public void Dispose_ThenAccess_ThrowsObjectDisposedException()
    {
        var array = new UnmanagedArray<TestItem>(initialCapacity: 4);
        array.Add(new TestItem { Value = 1 });

        array.Dispose();

        Assert.Throws<ObjectDisposedException>(() => array[0]);
        Assert.Throws<ObjectDisposedException>(() => array.Add(new TestItem { Value = 2 }));
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var array = new UnmanagedArray<TestItem>(initialCapacity: 4);

        array.Dispose();
        var exception = Record.Exception(array.Dispose);

        Assert.Null(exception);
    }

    [Fact]
    public void Clear_ResetsCountButKeepsCapacity()
    {
        using var array = new UnmanagedArray<TestItem>(initialCapacity: 4);
        array.Add(new TestItem { Value = 1 });
        array.Add(new TestItem { Value = 2 });
        nuint capacityBeforeClear = array.Capacity;

        array.Clear();

        Assert.Equal((nuint)0, array.Count);
        Assert.Equal(capacityBeforeClear, array.Capacity);
    }

    [Fact]
    public void GetWritableSpan_WritesLandAtTheGivenOffset()
    {
        using var array = new UnmanagedArray<TestItem>(initialCapacity: 8);

        Span<TestItem> first = array.GetWritableSpan(start: 0, length: 3);
        for (int i = 0; i < 3; i++) first[i] = new TestItem { Value = i };

        Span<TestItem> second = array.GetWritableSpan(start: 3, length: 2);
        for (int i = 0; i < 2; i++) second[i] = new TestItem { Value = 100 + i };

        array.SetCount(5);

        Assert.Equal((nuint)5, array.Count);
        Assert.Equal([0, 1, 2, 100, 101], array.AsSpan().ToArray().Select(t => t.Value));
    }

    [Fact]
    public void SetCount_BeyondCapacity_Throws()
    {
        using var array = new UnmanagedArray<TestItem>(initialCapacity: 4);

        Assert.Throws<ArgumentOutOfRangeException>(() => array.SetCount(5));
    }

    [Fact]
    public void GetWritableSpan_ConcurrentDisjointWriters_ProduceNoCorruption()
    {
        // Mirrors how FileIndexer's chunk merge fills a pre-sized merged array: many workers, each
        // writing only its own known, non-overlapping slice, then one SetCount call at the end.
        const int workerCount = 8;
        const int itemsPerWorker = 50_000;
        using var array = new UnmanagedArray<TestItem>(initialCapacity: (nuint)(workerCount * itemsPerWorker));

        Parallel.For(0, workerCount, worker =>
        {
            Span<TestItem> slice = array.GetWritableSpan((nuint)(worker * itemsPerWorker), itemsPerWorker);
            for (int i = 0; i < itemsPerWorker; i++)
            {
                slice[i] = new TestItem { Value = worker * itemsPerWorker + i };
            }
        });
        array.SetCount((nuint)(workerCount * itemsPerWorker));

        Assert.Equal((nuint)(workerCount * itemsPerWorker), array.Count);
        for (int i = 0; i < workerCount * itemsPerWorker; i++)
        {
            Assert.Equal(i, array[i].Value);
        }
    }
}
