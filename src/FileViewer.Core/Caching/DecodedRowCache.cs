namespace FileViewer.Core.Caching;

/// <summary>
/// Fixed-capacity LRU cache of decoded rows, keyed by base (logical) row index so entries survive
/// re-sorts — sorting only permutes the <see cref="Native.SortKey"/> array, never base row indices.
/// Bounded so scrolling through a 2 GB file never grows managed allocation without limit (PRS
/// §5/§9); ordinary managed collections are fine here since the cache size never scales with file
/// size. O(1) get/set/evict via a dictionary + doubly-linked list.
///
/// Every public member is protected by a single lock: rows are resolved (and therefore cached)
/// from whichever thread is rendering/exporting/sorting/filtering at the time, including a
/// background thread while the UI thread keeps rendering the page currently on screen.
/// </summary>
public sealed class DecodedRowCache
{
    public const int DefaultCapacity = 4096;

    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Dictionary<long, LinkedListNode<CacheEntry>> _map;
    private readonly LinkedList<CacheEntry> _lruList = new();

    public DecodedRowCache(int capacity = DefaultCapacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        _capacity = capacity;
        _map = new Dictionary<long, LinkedListNode<CacheEntry>>(capacity);
    }

    public int Count { get { lock (_gate) return _map.Count; } }

    public bool TryGet(long rowIndex, out DecodedRow row)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(rowIndex, out LinkedListNode<CacheEntry>? node))
            {
                _lruList.Remove(node);
                _lruList.AddFirst(node);
                row = node.Value.Row;
                return true;
            }
            row = null!;
            return false;
        }
    }

    public void Set(long rowIndex, DecodedRow row)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(rowIndex, out LinkedListNode<CacheEntry>? existing))
            {
                _lruList.Remove(existing);
            }
            else if (_map.Count >= _capacity)
            {
                LinkedListNode<CacheEntry>? leastRecentlyUsed = _lruList.Last;
                if (leastRecentlyUsed is not null)
                {
                    _lruList.RemoveLast();
                    _map.Remove(leastRecentlyUsed.Value.RowIndex);
                }
            }

            var node = new LinkedListNode<CacheEntry>(new CacheEntry(rowIndex, row));
            _lruList.AddFirst(node);
            _map[rowIndex] = node;
        }
    }

    /// <summary>Evicts a single row — called whenever a <see cref="Overlay.CellEdit"/> or <see cref="Overlay.RowOp"/> touches it, so the cache never serves stale pre-edit data.</summary>
    public void Invalidate(long rowIndex)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(rowIndex, out LinkedListNode<CacheEntry>? node))
            {
                _lruList.Remove(node);
                _map.Remove(rowIndex);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _lruList.Clear();
        }
    }

    private readonly record struct CacheEntry(long RowIndex, DecodedRow Row);
}
