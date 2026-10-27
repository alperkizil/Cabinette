namespace Cabinette.Windows.Interop;

/// <summary>
/// Small thread-safe LRU cache bounded by entry count and, optionally, by a total weight (e.g. bytes).
/// Values may be null (used to remember "no thumbnail").
/// </summary>
internal sealed class LruCache<TKey, TValue>
    where TKey : notnull
{
    private readonly int _capacity;
    private readonly long _maxWeight;
    private readonly Func<TValue, long>? _weigher;
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _map;
    private readonly LinkedList<Entry> _order = new();
    private readonly Lock _lock = new();
    private long _weight;

    public LruCache(int capacity, IEqualityComparer<TKey>? comparer = null, Func<TValue, long>? weigher = null, long maxWeight = long.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _weigher = weigher;
        _maxWeight = maxWeight;
        _map = new Dictionary<TKey, LinkedListNode<Entry>>(comparer);
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _map.Count;
            }
        }
    }

    public long Weight
    {
        get
        {
            lock (_lock)
            {
                return _weight;
            }
        }
    }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }

        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        var weight = _weigher is null ? 0 : Math.Max(0, _weigher(value));
        lock (_lock)
        {
            if (_map.Remove(key, out var existing))
            {
                _order.Remove(existing);
                _weight -= existing.Value.Weight;
            }

            _map[key] = _order.AddFirst(new Entry(key, value, weight));
            _weight += weight;

            // Evict least recently used entries, but always keep the one just added.
            while ((_map.Count > _capacity || _weight > _maxWeight) && _order.Count > 1 && _order.Last is { } last)
            {
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
                _weight -= last.Value.Weight;
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _order.Clear();
            _weight = 0;
        }
    }

    private readonly record struct Entry(TKey Key, TValue Value, long Weight);
}
