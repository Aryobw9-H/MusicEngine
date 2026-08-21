namespace MusicEngine.Providers;

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Http;
using Microsoft.Extensions.Logging;
using Models;

/// <summary>
/// Deezer public API — metadata + 30s MP3 previews (full-track download is
/// subscription-gated and intentionally not implemented). Catalog tier.
/// api.deezer.com is geo-blocked on some networks; requests go through the
/// configured proxy when one is set.
/// </summary>
public sealed class DeezerProvider : ISearchProvider, IPreviewProvider, IAlbumProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<DeezerProvider> _logger;

    public ProviderId Id => ProviderId.Deezer;
    public string DisplayName => "Deezer";
    public SearchTier Tier => SearchTier.Catalog;
    public bool IsAvailable => true;

    public DeezerProvider(SharedHttpClient http, ILogger<DeezerProvider>? logger = null)
    {
        _http = http.Create("Deezer", proxied: !string.IsNullOrEmpty(http.ProxyUrl));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DeezerProvider>.Instance;
    }

    public async IAsyncEnumerable<SearchResult> SearchAsync(
        string query, int maxResults = 20,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var url = $"https://api.deezer.com/search?q={Uri.EscapeDataString(query)}&limit={Math.Min(50, Math.Max(5, maxResults))}";

        DeezerSearchResponse dto;
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            dto = await JsonSerializer.DeserializeAsync<DeezerSearchResponse>(s,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct).ConfigureAwait(false)
                ?? new DeezerSearchResponse();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Deezer search failed for {Query}: {Msg} (geo-block? check ProxyUrl)", query, ex.Message);
            yield break;
        }

        if (dto.Data is null) yield break;
        foreach (var t in dto.Data)
        {
            ct.ThrowIfCancellationRequested();
            yield return new SearchResult
            {
                Provider = ProviderId.Deezer,
                Id = t.Id.ToString(),
                Metadata = new TrackMetadata
                {
                    Title = t.Title ?? "",
                    Artist = t.Artist?.Name ?? "",
                    Album = t.Album?.Title,
                    AlbumId = t.Album?.Id is > 0 ? t.Album.Id.ToString() : null,
                    TrackNumber = t.TrackPosition is > 0 ? t.TrackPosition : null,
                    Duration = t.Duration is > 0 ? TimeSpan.FromSeconds(t.Duration.Value) : null,
                    ArtworkUri = TryUri(t.Album?.CoverXl ?? t.Album?.CoverMedium),
                },
                DirectStreamUri = TryUri(t.Preview),
                MaxQuality = StreamQuality.Preview128K,
                SourceUrl = t.Link ?? "",
                Downloadable = false,
                PreviewOnly = true,
            };
        }
    }

    public Uri? GetPreviewStreamUri(SearchResult track) => track.DirectStreamUri;

    /// <summary>Album search: fetch the album's full track list (album mode).</summary>
    public async Task<IReadOnlyList<SearchResult>> GetAlbumTracksAsync(AlbumRef album, CancellationToken ct = default)
    {
        var url = $"https://api.deezer.com/album/{Uri.EscapeDataString(album.Id)}/tracks?limit=100";
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var dto = await JsonSerializer.DeserializeAsync<DeezerTracksResponse>(s,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct).ConfigureAwait(false);
            if (dto?.Data is null) return Array.Empty<SearchResult>();
            var rows = new List<SearchResult>();
            foreach (var t in dto.Data)
            {
                rows.Add(new SearchResult
                {
                    Provider = ProviderId.Deezer,
                    Id = t.Id.ToString(),
                    Metadata = new TrackMetadata
                    {
                        Title = t.Title ?? "",
                        Artist = album.Artist,
                        Album = album.Name,
                        AlbumId = album.Id,
                        TrackNumber = t.TrackPosition is > 0 ? t.TrackPosition : null,
                        Duration = t.Duration is > 0 ? TimeSpan.FromSeconds(t.Duration.Value) : null,
                    },
                    DirectStreamUri = TryUri(t.Preview),
                    MaxQuality = StreamQuality.Preview128K,
                    SourceUrl = t.Link ?? "",
                    Downloadable = false,
                    PreviewOnly = true,
                });
            }
            return rows;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Deezer album lookup failed for {Album}: {Msg} (geo-block? check ProxyUrl)", album.Name, ex.Message);
            return Array.Empty<SearchResult>();
        }
    }

    private static Uri? TryUri(string? raw) =>
        !string.IsNullOrWhiteSpace(raw) && Uri.TryCreate(raw, UriKind.Absolute, out var u) ? u : null;

    private sealed class DeezerSearchResponse
    {
        [JsonPropertyName("data")] public List<DeezerTrack>? Data { get; set; }
    }

    private sealed class DeezerTrack
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("duration")] public int? Duration { get; set; }
        [JsonPropertyName("link")] public string? Link { get; set; }
        [JsonPropertyName("preview")] public string? Preview { get; set; }
        [JsonPropertyName("track_position")] public int? TrackPosition { get; set; }
        [JsonPropertyName("artist")] public DeezerArtist? Artist { get; set; }
        [JsonPropertyName("album")] public DeezerAlbum? Album { get; set; }
    }

    private sealed class DeezerArtist
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    private sealed class DeezerAlbum
    {
        [JsonPropertyName("id")] public long? Id { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("cover_xl")] public string? CoverXl { get; set; }
        [JsonPropertyName("cover_medium")] public string? CoverMedium { get; set; }
    }

    private sealed class DeezerTracksResponse
    {
        [JsonPropertyName("data")] public List<DeezerTrack>? Data { get; set; }
    }
}
