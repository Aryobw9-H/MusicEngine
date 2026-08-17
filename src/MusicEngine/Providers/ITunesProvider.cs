namespace MusicEngine.Providers;

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Http;
using Microsoft.Extensions.Logging;
using Models;

/// <summary>
/// iTunes Search API — free, no auth, rich metadata + 30s AAC previews + 600x600
/// artwork. Catalog tier: defines the GOAL identity for the search pipeline.
/// Note: iTunes is OR-of-terms, so fielded "artist" "title" queries are split
/// client-side — the artist is searched, the title filters the response.
/// </summary>
public sealed class ITunesProvider : ISearchProvider, IPreviewProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<ITunesProvider> _logger;

    public ProviderId Id => ProviderId.ITunes;
    public string DisplayName => "iTunes";
    public SearchTier Tier => SearchTier.Catalog;
    public bool IsAvailable => true;

    public ITunesProvider(SharedHttpClient http, ILogger<ITunesProvider>? logger = null)
    {
        _http = http.Create("ITunes");
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ITunesProvider>.Instance;
    }

    public async IAsyncEnumerable<SearchResult> SearchAsync(
        string query, int maxResults = 25,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var (searchTerm, titleFilter) = SplitArtistTitle(query);
        var dto = await FetchAsync(searchTerm, maxResults, ct).ConfigureAwait(false);

        // iTunes is OR-of-terms: "tataloo behesht" can return 0 while "tataloo"
        // returns 25. Retry with the leading token and filter client-side on the rest.
        if ((dto.Results is null || dto.Results.Count == 0) && titleFilter is null)
        {
            var tokens = searchTerm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length >= 2)
            {
                dto = await FetchAsync(tokens[0], maxResults, ct).ConfigureAwait(false);
                titleFilter = string.Join(' ', tokens[1..]);
            }
        }

        if (dto.Results is null) yield break;
        foreach (var t in dto.Results)
        {
            ct.ThrowIfCancellationRequested();
            if (titleFilter is not null && !TitleMatches(t.TrackName ?? "", titleFilter)) continue;
            var artwork = t.ArtworkUrl100?.Replace("100x100bb", "600x600bb");
            yield return new SearchResult
            {
                Provider = ProviderId.ITunes,
                Id = t.TrackId.ToString(),
                Metadata = new TrackMetadata
                {
                    Title = t.TrackName ?? "",
                    Artist = t.ArtistName ?? "",
                    Album = t.CollectionName,
                    Duration = t.TrackTimeMillis is > 0 ? TimeSpan.FromMilliseconds(t.TrackTimeMillis.Value) : null,
                    ArtworkUri = TryUri(artwork),
                    ReleaseDate = DateTimeOffset.TryParse(t.ReleaseDate, out var rd) ? rd : null,
                    Genre = t.PrimaryGenreName,
                },
                DirectStreamUri = TryUri(t.PreviewUrl),
                MaxQuality = StreamQuality.Preview64K,
                SourceUrl = $"https://music.apple.com/us/album/{t.TrackId}",
                Downloadable = false,
                PreviewOnly = true,
            };
        }
    }

    private async Task<ITunesSearchResponse> FetchAsync(string term, int maxResults, CancellationToken ct)
    {
        var url = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(term)}&entity=song&media=music&limit={Math.Min(50, Math.Max(5, maxResults))}";
        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<ITunesSearchResponse>(s,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct).ConfigureAwait(false)
                ?? new ITunesSearchResponse();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("iTunes search failed for {Term}: {Msg}", term, ex.Message);
            return new ITunesSearchResponse();
        }
    }

    public Uri? GetPreviewStreamUri(SearchResult track) => track.DirectStreamUri;

    /// <summary>Splits fielded "artist" "title" (or artist:"X" track:"Y") queries; raw query otherwise.</summary>
    internal static (string SearchTerm, string? TitleFilter) SplitArtistTitle(string query)
    {
        var fielded = System.Text.RegularExpressions.Regex.Match(query, "artist:\"([^\"]+)\"\\s+track:\"([^\"]+)\"");
        if (fielded.Success) return (fielded.Groups[1].Value.Trim(), fielded.Groups[2].Value.Trim());
        var quoted = System.Text.RegularExpressions.Regex.Match(query, "\"([^\"]+)\"\\s+\"([^\"]+)\"");
        if (quoted.Success) return (quoted.Groups[1].Value.Trim(), quoted.Groups[2].Value.Trim());
        return (query, null);
    }

    internal static bool TitleMatches(string title, string filter)
    {
        var t = title.ToLowerInvariant();
        return filter.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(tok => t.Contains(tok.ToLowerInvariant(), StringComparison.Ordinal));
    }

    private static Uri? TryUri(string? raw) =>
        !string.IsNullOrWhiteSpace(raw) && Uri.TryCreate(raw, UriKind.Absolute, out var u) ? u : null;

    private sealed class ITunesSearchResponse
    {
        [JsonPropertyName("results")] public List<ITunesTrack>? Results { get; set; }
    }

    private sealed class ITunesTrack
    {
        [JsonPropertyName("trackId")] public long TrackId { get; set; }
        [JsonPropertyName("trackName")] public string? TrackName { get; set; }
        [JsonPropertyName("artistName")] public string? ArtistName { get; set; }
        [JsonPropertyName("collectionName")] public string? CollectionName { get; set; }
        [JsonPropertyName("previewUrl")] public string? PreviewUrl { get; set; }
        [JsonPropertyName("artworkUrl100")] public string? ArtworkUrl100 { get; set; }
        [JsonPropertyName("trackTimeMillis")] public long? TrackTimeMillis { get; set; }
        [JsonPropertyName("primaryGenreName")] public string? PrimaryGenreName { get; set; }
        [JsonPropertyName("releaseDate")] public string? ReleaseDate { get; set; }
    }
}
