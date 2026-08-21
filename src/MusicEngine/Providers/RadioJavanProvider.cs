namespace MusicEngine.Providers;

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Downloads;
using Http;
using Microsoft.Extensions.Logging;
using Models;

/// <summary>
/// Radio Javan — first-class Persian source with a maintained API
/// (rj-deskcloud.com/api2): search returns real tracks with direct 128/256 kbps
/// MP3/AAC URLs. Display tier; downloads re-resolve a fresh CDN URL first.
/// </summary>
public sealed class RadioJavanProvider : ISearchProvider, IDownloadProvider, IAlbumProvider
{
    private readonly HttpClient _http;
    private readonly HttpClient _mediaHttp;
    private readonly ILogger<RadioJavanProvider> _logger;

    public ProviderId Id => ProviderId.RadioJavan;
    public string DisplayName => "Radio Javan";
    public SearchTier Tier => SearchTier.Display;
    public bool IsAvailable => true;

    public RadioJavanProvider(SharedHttpClient http, ILogger<RadioJavanProvider>? logger = null)
    {
        _http = http.Create("RadioJavan");
        // The media CDN (host*.media-rj.com) is blocked on some filtered networks —
        // search API stays direct, media downloads can go through the proxy.
        _mediaHttp = http.Create("RadioJavanMedia", proxied: !string.IsNullOrEmpty(http.ProxyUrl));
        if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
            _http.DefaultRequestHeaders.Add("User-Agent", "RadioJavan/5.2.0 Chrome/130 Electron/33 Safari/537.36");
        if (!_http.DefaultRequestHeaders.Contains("x-rj-user-agent"))
            _http.DefaultRequestHeaders.Add("x-rj-user-agent", "Radio Javan/5.0.0 (Desktop) com.radioJavan.rj.desktop");
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RadioJavanProvider>.Instance;
    }

    public bool CanDownload(SearchResult result) => result.Provider == ProviderId.RadioJavan;

    public async IAsyncEnumerable<SearchResult> SearchAsync(
        string query, int maxResults = 25,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var url = $"https://rj-deskcloud.com/api2/search?query={Uri.EscapeDataString(query)}&items=mp3";
        RjSearchResponse? dto;
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            dto = await JsonSerializer.DeserializeAsync<RjSearchResponse>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Radio Javan search failed for {Query}: {Msg}", query, ex.Message);
            yield break;
        }

        // mp3s = loose singles; albums = album-track rows (carry album_id). Both
        // are real downloadable tracks, so merge them (album rows feed album-mode
        // detection). Dedupe by track id — a track can appear in both arrays.
        var rows = new List<RjMp3>();
        var seenIds = new HashSet<int>();
        foreach (var s in dto?.Mp3s ?? new List<RjMp3>())
            if (seenIds.Add(s.Id)) rows.Add(s);
        foreach (var s in dto?.Albums ?? new List<RjMp3>())
            if (seenIds.Add(s.Id)) rows.Add(s);

        var emitted = 0;
        foreach (var s in rows)
        {
            if (emitted >= maxResults) yield break;
            ct.ThrowIfCancellationRequested();
            yield return MapToResult(s);
            emitted++;
        }
    }

    public async Task<DownloadResult> DownloadAsync(
        SearchResult track, DownloadOptions options,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(new DownloadProgress(DownloadPhase.Resolving, 0, null, "Radio Javan: resolving fresh URL"));

        // Re-resolve so we always get a fresh CDN URL; fall back to the cached one.
        string? streamUrl = null;
        try
        {
            var query = $"{track.Metadata.Artist} {track.Metadata.Title}".Trim();
            if (query.Length == 0) query = track.Id;
            var url = $"https://rj-deskcloud.com/api2/search?query={Uri.EscapeDataString(query)}&items=mp3";
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var dto = await JsonSerializer.DeserializeAsync<RjSearchResponse>(s,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct).ConfigureAwait(false);
            var match = dto?.Mp3s?.FirstOrDefault(x => x.Id.ToString() == track.Id);
            streamUrl = match?.HqLink ?? match?.Link;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Radio Javan re-resolve failed ({Msg}); using cached URL", ex.Message);
        }
        streamUrl ??= track.DirectStreamUri?.OriginalString
            ?? throw new InvalidOperationException("No stream URL for this Radio Javan track.");

        var finalPath = Path.Combine(options.OutputDirectory,
            FileNaming.Build(options.TagTemplate, track, ".mp3", options.FilenameTemplate));
        await HttpDownloader.DownloadToFileAsync(_mediaHttp, streamUrl, finalPath, progress, ct).ConfigureAwait(false);
        return new DownloadResult(finalPath,
            track.MaxQuality == StreamQuality.Maximum256K ? StreamQuality.Maximum256K : StreamQuality.Standard128K,
            ProviderId.RadioJavan);
    }

    private static SearchResult MapToResult(RjMp3 s) => new()
    {
        Provider = ProviderId.RadioJavan,
        Id = s.Id.ToString(),
        Metadata = new TrackMetadata
        {
            Title = s.Song ?? s.Name ?? string.Empty,
            Artist = s.Artist ?? string.Empty,
            Album = s.AlbumName ?? s.Album?.Album,
            AlbumId = (s.AlbumId ?? s.Album?.Id) is > 0 ? (s.AlbumId ?? s.Album!.Id).ToString() : null,
            TrackNumber = s.Album?.Track is > 0 ? s.Album.Track : null,
            Duration = s.Duration is > 0 ? TimeSpan.FromSeconds(s.Duration.Value) : null,
            ArtworkUri = TryUri(s.Photo),
        },
        DirectStreamUri = TryUri(s.HqLink ?? s.Link),
        MaxQuality = s.HqLink is not null ? StreamQuality.Maximum256K : StreamQuality.Standard128K,
        SourceUrl = $"https://play.radiojavan.com/song/{s.Id}",
        Downloadable = true,
    };

    /// <summary>
    /// Album search (album mode): Radio Javan has no public album-track endpoint,
    /// so the album is expanded by searching the album title AND the artist name
    /// (each returns the album's most played tracks) and keeping every row that
    /// carries the album's id. Best effort — popular tracks surface, the long
    /// tail may not.
    /// </summary>
    public async Task<IReadOnlyList<SearchResult>> GetAlbumTracksAsync(AlbumRef album, CancellationToken ct = default)
    {
        var results = new List<SearchResult>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var queries = new List<string> { album.Name };
        if (!string.IsNullOrWhiteSpace(album.Artist) && album.Artist != album.Name)
            queries.Add(album.Artist);
        foreach (var q in queries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await foreach (var row in SearchAsync(q, 25, ct).ConfigureAwait(false))
                {
                    if (row.Metadata.AlbumId != album.Id) continue;
                    if (seenIds.Add(row.Id))
                        results.Add(row);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug("Radio Javan album expansion for {Album} via {Query} failed: {Msg}",
                    album.Name, q, ex.Message);
            }
        }
        return results
            .OrderBy(r => r.Metadata.TrackNumber ?? int.MaxValue)
            .ThenBy(r => r.Metadata.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Uri? TryUri(string? raw) =>
        !string.IsNullOrWhiteSpace(raw) && Uri.TryCreate(raw, UriKind.Absolute, out var u) ? u : null;

    private sealed class RjSearchResponse
    {
        [JsonPropertyName("mp3s")] public List<RjMp3>? Mp3s { get; set; }
        // Album-track rows: same shape as mp3s but tagged with album_id/album_album.
        [JsonPropertyName("albums")] public List<RjMp3>? Albums { get; set; }
    }

    private sealed class RjMp3
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("song")] public string? Song { get; set; }
        [JsonPropertyName("artist")] public string? Artist { get; set; }
        [JsonPropertyName("link")] public string? Link { get; set; }
        [JsonPropertyName("hq_link")] public string? HqLink { get; set; }
        [JsonPropertyName("duration")] public double? Duration { get; set; }
        [JsonPropertyName("photo")] public string? Photo { get; set; }
        // Album identity: nested (album) and flat (album_*) — the API provides
        // both depending on the endpoint/shape. album_id groups the album's tracks.
        [JsonPropertyName("album_id")] public int? AlbumId { get; set; }
        [JsonPropertyName("album_album")] public string? AlbumName { get; set; }
        [JsonPropertyName("album")] public RjAlbum? Album { get; set; }
    }

    private sealed class RjAlbum
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("album")] public string? Album { get; set; }
        [JsonPropertyName("artist")] public string? Artist { get; set; }
        [JsonPropertyName("track")] public int? Track { get; set; }
    }
}
