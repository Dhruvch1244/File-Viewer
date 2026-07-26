using System.Runtime.InteropServices;

namespace FileViewer.Core.Native;

/// <summary>
/// A growable array of unmanaged structs backed by <see cref="NativeMemory"/> rather than the GC
/// heap. Used for the row index and sort-key arrays (<see cref="RowIndexEntry"/>,
/// <see cref="SortKey"/>) so a multi-million-row index never triggers Gen2/LOH collections.
/// Not thread-safe for concurrent <see cref="Add"/> calls — callers must serialize appends (the
/// indexing pipeline merges parallel-scan results into this array from a single thread).
/// </summary>
public sealed unsafe class UnmanagedArray<T> : IDisposable where T : unmanaged
{
    private const nuint DefaultInitialCapacity = 1024;

    private T* _pointer;
    private nuint _capacity;
    private nuint _count;
    private bool _disposed;

    public UnmanagedArray(nuint initialCapacity = DefaultInitialCapacity)
    {
        _capacity = initialCapacity == 0 ? 1 : initialCapacity;
        _pointer = (T*)NativeMemory.Alloc(_capacity, (nuint)sizeof(T));
        _count = 0;
    }

    public nuint Count => _count;
    public nuint Capacity => _capacity;

    public ref T this[nuint index]
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (index >= _count) throw new ArgumentOutOfRangeException(nameof(index));
            return ref _pointer[index];
        }
    }

    public ref T this[long index] => ref this[checked((nuint)index)];

    public void Add(in T item)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureCapacity(_count + 1);
        _pointer[_count] = item;
        _count++;
    }

    public void EnsureCapacity(nuint required)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (required <= _capacity) return;

        nuint newCapacity = _capacity;
        while (newCapacity < required)
        {
            newCapacity *= 2;
        }
        Grow(newCapacity);
    }

    private void Grow(nuint newCapacity)
    {
        nuint newByteSize = checked(newCapacity * (nuint)sizeof(T));
        _pointer = (T*)NativeMemory.Realloc(_pointer, newByteSize);
        _capacity = newCapacity;
    }

    public void Clear() => _count = 0;

    /// <summary>
    /// A span over all live elements. Throws if <see cref="Count"/> exceeds <see cref="int.MaxValue"/>
    /// (not expected at the row counts a 2 GB DIF file implies); use <see cref="AsSpan(long, int)"/>
    /// for partial access at larger scales.
    /// </summary>
    public Span<T> AsSpan()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_count > int.MaxValue)
        {
            throw new InvalidOperationException(
                $"Count ({_count}) exceeds Span capacity (int.MaxValue). Use AsSpan(start, length) for partial access.");
        }
        return new Span<T>(_pointer, (int)_count);
    }

    public Span<T> AsSpan(long start, int length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new Span<T>(_pointer + start, length);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (_pointer != null)
        {
            NativeMemory.Free(_pointer);
            _pointer = null;
        }
        _disposed = true;
    }

    ~UnmanagedArray() => Dispose(false);
}
