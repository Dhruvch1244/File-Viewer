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
}
