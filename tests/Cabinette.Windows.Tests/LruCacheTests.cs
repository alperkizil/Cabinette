using Cabinette.Windows.Interop;

namespace Cabinette.Windows.Tests;

public sealed class LruCacheTests
{
    [Fact]
    public void EvictsLeastRecentlyUsedByCount()
    {
        var cache = new LruCache<string, string?>(2);
        cache.Set("a", "A");
        cache.Set("b", null);
        Assert.True(cache.TryGet("a", out _)); // "a" is now the most recent.
        cache.Set("c", "C");

        Assert.True(cache.TryGet("a", out var a));
        Assert.Equal("A", a);
        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("c", out _));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void EvictsByWeightButKeepsNewestEntry()
    {
        var cache = new LruCache<int, int>(100, weigher: v => v, maxWeight: 10);
        cache.Set(1, 4);
        cache.Set(2, 4);
        cache.Set(3, 4); // 12 > 10 → evicts key 1.
        Assert.False(cache.TryGet(1, out _));
        Assert.Equal(8, cache.Weight);

        cache.Set(4, 50); // Heavier than the budget on its own: everything else goes, it stays.
        Assert.True(cache.TryGet(4, out _));
        Assert.Equal(1, cache.Count);

        cache.Set(4, 1); // Replacing updates the weight.
        Assert.Equal(1, cache.Weight);
    }
}
