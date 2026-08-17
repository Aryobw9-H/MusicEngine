namespace MusicEngine.Providers;

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Downloads;
using Http;
using Microsoft.Extensions.Logging;
using Models;

/// <summary>
/// Native SoundCloud provider talking straight to api-v2.soundcloud.com — no
/// third-party library whose bundled client_id deactivates. Method verified
/// against current community clients (SoundMist, scdl, yt-dlp):
///
///   client_id: fetched from https://m.soundcloud.com/ HTML ("clientId":"…"),
///              refreshed automatically on 401.
///   search:    /search?q=…&client_id=…  (kind == "track" entries)
///   stream:    track.media.transcodings → "stream/progressive" URL
///              → GET {url}?client_id=… → { "url": <direct mp3> } — works for
///              ANY streamable track, not just creator-enabled downloads.
/// </summary>
public sealed class SoundCloudProvider : ISearchProvider, IDownloadProvider
{
    private const string MobileHome = "https://m.soundcloud.com/";
    private const string ApiBase = "https://api-v2.soundcloud.com";
    private const string BrowserUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    private static readonly Regex ClientIdRegex = new("\"clientId\":\"(\\w+)\"", RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly ILogger<SoundCloudProvider> _logger;
    private readonly SemaphoreSlim _idLock = new(1, 1);
    private string? _clientId;
    private DateTime _clientIdAt;

    public ProviderId Id => ProviderId.SoundCloud;
    public string DisplayName => "SoundCloud";
    public SearchTier Tier => SearchTier.Display;
    public bool IsAvailable => true;

    public SoundCloudProvider(SharedHttpClient http, ILogger<SoundCloudProvider>? logger = null, string? proxyUrl = null)
    {
        _http = http.Create("SoundCloud", proxied: !string.IsNullOrEmpty(proxyUrl));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SoundCloudProvider>.Instance;
    }

    /// <summary>Resolve (and cache) a fresh public client_id. Retries — the page
    /// fetch can fail transiently through flaky proxies.</summary>
    public async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (ClientIdFresh()) return;
        await _idLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ClientIdFresh()) return;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, MobileHome);
                    req.Headers.Add("User-Agent", BrowserUa);
                    using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                    var html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    var m = ClientIdRegex.Match(html);
                    if (m.Success)
                    {
                        _clientId = m.Groups[1].Value;
                        _clientIdAt = DateTime.UtcNow;
                        return;
                    }
                    _logger.LogWarning("SoundCloud client_id not found in page HTML (attempt {Attempt})", attempt);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("SoundCloud client_id fetch failed (attempt {Attempt}): {Msg}",
                        attempt, ex.Message);
                }
                if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _idLock.Release();
        }
    }

    private bool ClientIdFresh() => _clientId is not null && DateTime.UtcNow - _clientIdAt < TimeSpan.FromHours(12);

    private async Task<string?> GetClientIdAsync(CancellationToken ct)
    {
        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        return _clientId;
    }

    /// <summary>GET an api-v2 endpoint, auto-refreshing the client_id once on 401/403.</summary>
    private async Task<JsonElement?> ApiGetAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var id = await GetClientIdAsync(ct).ConfigureAwait(false);
            if (id is null) return null;
            var sep = url.Contains('?') ? '&' : '?';
            using var resp = await _http.GetAsync($"{url}{sep}client_id={id}", ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                try
                {
                    return (await JsonSerializer.DeserializeAsync<JsonElement>(s, JsonOpts, ct).ConfigureAwait(false));
                }
                catch (JsonException)
                {
                    return null;
                }
            }
            if (resp.StatusCode is not (System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden))
                return null;
            _clientId = null; // expired — force refresh and retry once
        }
        return null;
    }

    // ---------- search ----------

    public async IAsyncEnumerable<SearchResult> SearchAsync(
        string query, int maxResults = 10,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var root = await ApiGetAsync(
            $"{ApiBase}/search/tracks?q={Uri.EscapeDataString(query)}&limit={Math.Min(50, Math.Max(5, maxResults))}",
            ct).ConfigureAwait(false);
        if (root is not { ValueKind: JsonValueKind.Object } doc) yield break;
        if (!doc.TryGetProperty("collection", out var col) || col.ValueKind != JsonValueKind.Array) yield break;

        var emitted = 0;
        foreach (var t in col.EnumerateArray())
        {
            if (emitted >= maxResults) yield break;
            if (t.ValueKind != JsonValueKind.Object) continue;
            var kind = GetString(t, "kind");
            if (kind != "track") continue;

            var streamable = GetBool(t, "streamable") ?? false;
            var downloadable = GetBool(t, "downloadable") ?? false;
            yield return new SearchResult
            {
                Provider = ProviderId.SoundCloud,
                Id = t.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                    ? idEl.GetInt64().ToString()
                    : GetString(t, "permalink_url") ?? "",
                Metadata = new TrackMetadata
                {
                    Title = GetString(t, "title") ?? "",
                    Artist = t.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object
                        ? GetString(u, "username") ?? ""
                        : "",
                    Duration = GetInt(t, "duration") is long ms && ms > 0 ? TimeSpan.FromMilliseconds(ms) : null,
                    ArtworkUri = TryUri(GetString(t, "artwork_url")?.Replace("-large", "-t500x500")),
                    Genre = GetString(t, "genre"),
                },
                MaxQuality = StreamQuality.Maximum256K,
                SourceUrl = GetString(t, "permalink_url") ?? "",
                Downloadable = streamable || downloadable,
            };
            emitted++;
        }
    }

    // ---------- download ----------

    public bool CanDownload(SearchResult result) => result.Provider == ProviderId.SoundCloud;

    public async Task<DownloadResult> DownloadAsync(
        SearchResult track, DownloadOptions options,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(new DownloadProgress(DownloadPhase.Resolving, 0, null, "SoundCloud: resolving stream"));

        // Resolve fresh track data by id (or permalink url).
        var trackUrl = long.TryParse(track.Id, out var tid)
            ? $"{ApiBase}/tracks/{tid}"
            : $"{ApiBase}/resolve?url={Uri.EscapeDataString(track.SourceUrl)}";
        var root = await ApiGetAsync(trackUrl, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("SoundCloud track not resolvable.");

        // Prefer the creator's original file when the track allows downloads
        // (full quality); otherwise the progressive MPEG mp3 stream (any track).
        if (GetBool(root, "downloadable") == true && root.TryGetProperty("downloads", out var dl)
            && GetBool(dl, "downloadable") == true)
        {
            try
            {
                var original = await ResolveDownloadUrlAsync(root, ct).ConfigureAwait(false);
                if (original is not null)
                {
                    var p = Path.Combine(options.OutputDirectory, FileNaming.Build(options.TagTemplate, track, ".mp3", options.FilenameTemplate));
                    await HttpDownloader.DownloadToFileAsync(_http, original, p, progress, ct).ConfigureAwait(false);
                    return new DownloadResult(p, StreamQuality.Maximum256K, ProviderId.SoundCloud);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("SoundCloud original download unavailable ({Msg}); using stream", ex.Message);
            }
        }

        var streamUrl = GetProgressiveUrl(root)
            ?? throw new InvalidOperationException("No progressive stream for this track.");
        var mediaUrl = await ResolveTranscodingAsync(streamUrl, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("SoundCloud stream URL expired.");

        var finalPath = Path.Combine(options.OutputDirectory, FileNaming.Build(options.TagTemplate, track, ".mp3", options.FilenameTemplate));
        await HttpDownloader.DownloadToFileAsync(_http, mediaUrl, finalPath, progress, ct).ConfigureAwait(false);
        return new DownloadResult(finalPath, StreamQuality.Standard128K, ProviderId.SoundCloud);
    }

    private string? GetProgressiveUrl(JsonElement track)
    {
        if (!track.TryGetProperty("media", out var media)) return null;
        if (!media.TryGetProperty("transcodings", out var trans) || trans.ValueKind != JsonValueKind.Array) return null;
        foreach (var t in trans.EnumerateArray())
        {
            var url = GetString(t, "url");
            if (url is null) continue;
            var preset = GetString(t, "preset") ?? "";
            if (url.Contains("stream/progressive", StringComparison.Ordinal)
                || url.Contains("preview/progressive", StringComparison.Ordinal)
                || preset.Contains("mp3", StringComparison.OrdinalIgnoreCase) && url.Contains("progressive"))
                return url;
        }
        return null;
    }

    private async Task<string?> ResolveTranscodingAsync(string transcodingUrl, CancellationToken ct)
    {
        var id = await GetClientIdAsync(ct).ConfigureAwait(false);
        if (id is null) return null;
        using var resp = await _http.GetAsync($"{transcodingUrl}?client_id={id}", ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var doc = await JsonSerializer.DeserializeAsync<JsonElement>(s, JsonOpts, ct).ConfigureAwait(false);
        return doc.TryGetProperty("url", out var u) ? u.GetString() : null;
    }

    private async Task<string?> ResolveDownloadUrlAsync(JsonElement track, CancellationToken ct)
    {
        if (!track.TryGetProperty("download", out var dl)) return null;
        var url = GetString(dl, "url");
        if (url is null) return null;
        return await ResolveTranscodingAsync(url, ct).ConfigureAwait(false);
    }

    private static string? GetString(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? GetInt(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : null;

    private static bool? GetBool(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static Uri? TryUri(string? raw) =>
        !string.IsNullOrWhiteSpace(raw) && Uri.TryCreate(raw, UriKind.Absolute, out var u) ? u : null;
}
