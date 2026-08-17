namespace MusicEngine.Search;

using Microsoft.Extensions.Logging;
using Models;

/// <summary>
/// Provider health tracker: a provider that fails 3× in a row is quiesced for
/// 10 minutes and skipped in fan-outs, so a bot-checked scraper doesn't stall
/// every search. Recovers automatically.
/// </summary>
public sealed class ProviderHealthMonitor
{
    private readonly ILogger<ProviderHealthMonitor> _logger;
    private readonly int _failureThreshold;
    private readonly TimeSpan _quiesceFor;
    private readonly Dictionary<ProviderId, (int FailCount, DateTimeOffset QuiescedUntil)> _state = new();
    private readonly object _lock = new();

    public ProviderHealthMonitor(ILogger<ProviderHealthMonitor>? logger = null,
        int failureThreshold = 3, TimeSpan? quiesceFor = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ProviderHealthMonitor>.Instance;
        _failureThreshold = failureThreshold;
        _quiesceFor = quiesceFor ?? TimeSpan.FromMinutes(10);
    }

    public bool IsQuiesced(ProviderId id)
    {
        lock (_lock)
        {
            if (!_state.TryGetValue(id, out var s)) return false;
            if (s.QuiescedUntil > DateTimeOffset.UtcNow) return true;
            _state.Remove(id);
            return false;
        }
    }

    public void RecordSuccess(ProviderId id)
    {
        lock (_lock) _state.Remove(id);
    }

    public void RecordFailure(ProviderId id, string? message = null)
    {
        lock (_lock)
        {
            if (!_state.TryGetValue(id, out var s))
            {
                _state[id] = (1, default);
                return;
            }
            if (s.QuiescedUntil > DateTimeOffset.UtcNow) return;
            if (s.FailCount + 1 >= _failureThreshold)
            {
                _state[id] = (0, DateTimeOffset.UtcNow + _quiesceFor);
                _logger.LogWarning("Provider {Provider} quiesced for {Min}min after {N} failures{Msg}",
                    id, _quiesceFor.TotalMinutes, _failureThreshold,
                    message is null ? "" : $" ({message})");
            }
            else
            {
                _state[id] = (s.FailCount + 1, default);
            }
        }
    }
}

/// <summary>
/// Cache-aside result cache keyed by the normalized query; saves repeat searches
/// (very common) and makes the second search instant.
/// </summary>
public sealed class SearchResultCache
{
    private sealed record Entry(IReadOnlyList<TrackWork> Works, DateTimeOffset StoredAt);

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
                return e.Works;
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
                var oldest = _cache.OrderBy(kv => kv.Value.StoredAt).First();
                _cache.Remove(oldest.Key);
            }
            _cache[key] = new Entry(works, DateTimeOffset.UtcNow);
        }
    }

    public void Clear() { lock (_lock) _cache.Clear(); }
}
