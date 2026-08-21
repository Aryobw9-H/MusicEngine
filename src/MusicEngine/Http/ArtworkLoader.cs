namespace MusicEngine.Http;

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

public class ArtworkLoader : IArtworkLoader
{
    private readonly SharedHttpClient _http;
    private readonly ILogger<ArtworkLoader> _logger;

    /// <summary>
    /// Shared in-flight/completed fetches keyed by URL (PERF-05): concurrent
    /// requests for the same artwork share one fetch, later requests are free.
    /// Bounded — cleared wholesale past 256 entries.
    /// </summary>
    private readonly ConcurrentDictionary<string, Task<byte[]?>> _cache = new();
    private const int CacheLimit = 256;

    public ArtworkLoader(SharedHttpClient http, ILogger<ArtworkLoader>? logger = null)
    {
        _http = http;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ArtworkLoader>.Instance;
    }

    public Task<byte[]?> LoadAsync(Uri uri, CancellationToken ct = default)
    {
        var key = uri.ToString();
        if (_cache.Count > CacheLimit) _cache.Clear(); // bounded (documented simple strategy)
        var task = _cache.GetOrAdd(key, FetchAsync);
        // A cancelling caller must not poison the shared cached task (PERF-06) —
        // cancellation is applied locally at the await site.
        return ct.CanBeCanceled ? task.WaitAsync(ct) : task;
    }

    private async Task<byte[]?> FetchAsync(string key)
    {
        byte[]? result;
        try
        {
            using var resp = await _http.Create("Artwork").GetAsync(new Uri(key)).ConfigureAwait(false);
            result = resp.IsSuccessStatusCode
                ? await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false)
                : null;
        }
        catch (Exception ex)
        {
            // Deliberate degradation — but never silent (BUG-12).
            _logger.LogDebug(ex, "Artwork fetch failed: {Uri}", key);
            result = null;
        }
        if (result is null)
        {
            // Don't pin failures — remove the entry so a later request retries
            // (a flaky proxy is this app's normal condition).
            _cache.TryRemove(key, out _);
        }
        return result;
    }

    public void Clear() => _cache.Clear();
}
