namespace MusicEngine.Search;

using System.Collections.Concurrent;
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
/// Short-TTL per-provider response cache (PERF-03): keyed by
/// (ProviderId, query), so rescue rounds that re-ask the same provider with a
/// slightly different variant, and repeated searches within a session, skip the
/// HTTP round-trip. 45-second TTL; bounded at 256 entries.
/// </summary>
public sealed class ProviderResponseCache
{
    private sealed record Entry(DateTimeOffset At, IReadOnlyList<SearchResult> Rows);

    private readonly ConcurrentDictionary<(ProviderId Id, string Query), Entry> _store = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(45);
    private const int Cap = 256;

    public bool TryGet(ProviderId id, string query, out IReadOnlyList<SearchResult> rows)
    {
        if (_store.TryGetValue((id, query), out var e) && DateTimeOffset.UtcNow - e.At < Ttl)
        {
            rows = e.Rows;
            return true;
        }
        rows = Array.Empty<SearchResult>();
        return false;
    }

    public void Store(ProviderId id, string query, IReadOnlyList<SearchResult> rows)
    {
        if (_store.Count >= Cap) _store.Clear(); // documented simple strategy
        _store[(id, query)] = new Entry(DateTimeOffset.UtcNow, rows);
    }

    public void Clear() => _store.Clear();
}
