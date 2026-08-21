namespace MusicEngine.Search;

using Microsoft.Extensions.Logging;
using Models;
using Text;

/// <summary>
/// Provider fan-out (MODERN-03/Phase-4 gate): builds the per-provider query plans
/// and runs the concurrent collection with a hard deadline. Moved verbatim from
/// <see cref="SearchService"/> so the orchestrator stays under 400 lines.
/// </summary>
public sealed class ProviderFanOut
{
    private readonly IReadOnlyList<ISearchProvider> _providers;
    private readonly ProviderHealthMonitor _health;
    private readonly ProviderResponseCache? _providerCache;
    private readonly ILogger<ProviderFanOut> _logger;

    public ProviderFanOut(
        IEnumerable<ISearchProvider> providers,
        ProviderHealthMonitor health,
        ProviderResponseCache? providerCache,
        ILogger<ProviderFanOut>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.ToArray();
        _health = health;
        _providerCache = providerCache;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ProviderFanOut>.Instance;
    }

    public List<(ISearchProvider Provider, string Query, int Max)> BuildCatalogPlans(ParsedQuery parsed, string raw)
    {
        var plans = new List<(ISearchProvider, string, int)>();
        foreach (var p in _providers.Where(p => p.Tier == SearchTier.Catalog && !_health.IsQuiesced(p.Id)))
        {
            // Fielded queries when the query parsed into artist+title; iTunes/Deezer
            // rank fielded queries far better than a bag of words.
            var query = parsed.Artist is { } a && parsed.Title is { } t
                ? (p.Id == ProviderId.Deezer ? $"artist:\"{a}\" track:\"{t}\"" : $"\"{a}\" \"{t}\"")
                : raw;
            plans.Add((p, query, 25));
        }
        return plans;
    }

    /// <summary>
    /// Build retrieval plans that send BOTH the original query AND the Finglish
    /// expansion to every provider. This way "فدایی کمین" also searches "fadaei
    /// kamin" and vice versa, maximizing recall across Persian/Finglish sites.
    /// </summary>
    public List<(ISearchProvider Provider, string Query, int Max)> BuildRetrievalPlans(
        string raw, IReadOnlyList<string>? expandedVariants = null)
    {
        var plans = new List<(ISearchProvider, string, int)>();
        var variants = expandedVariants?.Count > 0
            ? expandedVariants
            : new List<string> { raw };

        foreach (var p in _providers.Where(p => p.Tier == SearchTier.Display && !_health.IsQuiesced(p.Id)))
        {
            var max = p.Id == ProviderId.RadioJavan ? 25 : 15;
            foreach (var variant in variants)
            {
                // Skip if this variant is identical to another already queued for
                // this provider (dedup by provider+query pair).
                if (plans.Any(x => x.Item1.Id == p.Id
                    && string.Equals(x.Item2, variant, StringComparison.OrdinalIgnoreCase)))
                    continue;
                plans.Add((p, variant, max));
            }
        }
        return plans;
    }

    public List<(ISearchProvider Provider, string Query, int Max)> PlansFor(
        IReadOnlyList<ProviderId> ids, string query, int max) =>
        _providers
            .Where(p => ids.Contains(p.Id) && !_health.IsQuiesced(p.Id))
            .Select(p => (p, query, max))
            .ToList();

    // ---------- fan-out ----------

    public async Task<List<SearchResult>> CollectAsync(
        IReadOnlyList<(ISearchProvider Provider, string Query, int Max)> plans,
        TimeSpan timeout,
        CancellationToken ct,
        Action<SearchResult[]>? onBatch = null,
        Action<ProviderId, ProviderState>? onProviderStatus = null)
    {
        var results = new List<SearchResult>();
        var closed = false;
        // Fan-out-scoped token: lets the hard deadline below cancel straggling
        // provider tasks instead of merely abandoning them, and lets the caller's
        // cancellation propagate to every task. Disposed via the continuation so
        // still-running tasks keep a valid token (never a `using`).
        var fanOutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = plans.Select(plan => Task.Run(async () =>
        {
            var itemResults = new List<SearchResult>();
            try
            {
                // PERF-03: serve a recent response for the same (provider, query)
                // from the session cache — repeated searches and rescue rounds
                // skip the HTTP round-trip entirely.
                if (_providerCache is not null
                    && _providerCache.TryGet(plan.Provider.Id, plan.Query, out var cachedRows))
                {
                    itemResults.AddRange(cachedRows);
                }
                else
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(fanOutCts.Token);
                    timeoutCts.CancelAfter(timeout);
                    await foreach (var item in plan.Provider.SearchAsync(plan.Query, plan.Max, timeoutCts.Token)
                                       .ConfigureAwait(false))
                        itemResults.Add(item);
                    if (_providerCache is not null && itemResults.Count > 0)
                        _providerCache.Store(plan.Provider.Id, plan.Query, itemResults.ToArray());
                }
                _health.RecordSuccess(plan.Provider.Id);
                onProviderStatus?.Invoke(plan.Provider.Id, ProviderState.Responded);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _health.RecordFailure(plan.Provider.Id, "timeout");
                _logger.LogDebug("Provider {Provider} timed out after {Sec}s ({Query})",
                    plan.Provider.Id, timeout.TotalSeconds, plan.Query);
                onProviderStatus?.Invoke(plan.Provider.Id, ProviderState.TimedOut);
            }
            catch (Exception ex)
            {
                _health.RecordFailure(plan.Provider.Id, ex.Message);
                _logger.LogWarning("Provider {Provider} search failed: {Msg}", plan.Provider.Id, ex.Message);
                onProviderStatus?.Invoke(plan.Provider.Id, ProviderState.Failed);
            }
            lock (results)
            {
                // Post-deadline stragglers must not publish rows into a search the
                // caller has already consumed.
                if (closed) return;
                results.AddRange(itemResults);
                if (onBatch is not null && itemResults.Count > 0)
                    onBatch(results.ToArray());
            }
        }, fanOutCts.Token)).ToArray();

        _ = Task.WhenAll(tasks).ContinueWith(_ => fanOutCts.Dispose(), TaskScheduler.Default);

        if (tasks.Length == 0) return results;

        // HARD DEADLINE: a provider library that ignores its cancellation token
        // (observed with YoutubeExplode under flaky proxies) must never stall the
        // whole search — abandon stragglers and keep whatever already landed.
        var grace = timeout + timeout + TimeSpan.FromSeconds(4);
        try
        {
            await Task.WhenAll(tasks).WaitAsync(grace, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            lock (results) closed = true;
            fanOutCts.Cancel();
            _logger.LogWarning("Fan-out grace deadline hit after {Sec}s; cancelling stragglers, continuing with partial results",
                grace.TotalSeconds);
        }

        lock (results) return results.ToList();
    }
}
