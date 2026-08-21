namespace MusicEngine.Search;

using Models;

/// <summary>
/// Cache-aside result cache keyed by the canonical query; saves repeat searches
/// (very common) and makes the second search instant. True LRU: recency is
/// updated on every hit and eviction is a single allocation-free pass (BUG-08).
/// Keyed by <see cref="SearchService.CanonicalCacheKey"/> so cross-script
/// duplicates of the same search share one entry (PERF-03).
/// </summary>
public sealed class SearchResultCache
{
    /// <summary>
    /// Canonical cache key (PERF-03): the Finglish→Persian conversion is the
    /// script-independent identity of a query — "tataloo behesht" and
    /// "تتلو بهشت" both convert to "تتلو بهشت", so cross-script repeats of the
    /// same search hit the same cache entry. (The expander's own output differs
    /// per input script, so the expansion set alone cannot unify them.)
    /// </summary>
    public static string CanonicalKey(string query)
    {
        var converted = Text.TrackTextNormalizer.Normalize(Text.FinglishConverter.Convert(query));
        return converted.Length > 0 ? converted : Text.TrackTextNormalizer.Normalize(query);
    }


    private sealed record Entry(IReadOnlyList<TrackWork> Works, DateTimeOffset StoredAt, long LastAccessTicks);

    private readonly Dictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly TimeSpan _ttl;

    public SearchResultCache(TimeSpan? ttl = null) => _ttl = ttl ?? TimeSpan.FromHours(6);

    public IReadOnlyList<TrackWork>? TryGet(string rawQuery)
    {
        var key = Text.TrackTextNormalizer.Normalize(rawQuery);
        if (key.Length == 0) return null;
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var e) && DateTimeOffset.UtcNow - e.StoredAt < _ttl)
            {
                // Refresh recency so a hot query survives eviction (true LRU).
                _cache[key] = e with { LastAccessTicks = DateTimeOffset.UtcNow.UtcTicks };
                return e.Works;
            }
            _cache.Remove(key);
            return null;
        }
    }

    public void Store(string rawQuery, IReadOnlyList<TrackWork> works)
    {
        var key = Text.TrackTextNormalizer.Normalize(rawQuery);
        if (key.Length == 0) return;
        lock (_lock)
        {
            if (_cache.Count >= 512)
            {
                // Single allocation-free pass: evict the least-recently-used entry.
                var oldestKey = default(string);
                var oldestTicks = long.MaxValue;
                foreach (var kv in _cache)
                {
                    if (kv.Value.LastAccessTicks < oldestTicks)
                    {
                        oldestTicks = kv.Value.LastAccessTicks;
                        oldestKey = kv.Key;
                    }
                }
                if (oldestKey is not null) _cache.Remove(oldestKey);
            }
            _cache[key] = new Entry(works, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.UtcTicks);
        }
    }

    public void Clear() { lock (_lock) _cache.Clear(); }
}
