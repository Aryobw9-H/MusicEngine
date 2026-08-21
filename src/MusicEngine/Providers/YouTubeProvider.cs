namespace MusicEngine.Providers;

using System.Runtime.CompilerServices;
using Http;
using Microsoft.Extensions.Logging;
using Models;
using Text;
using YoutubeExplode;
using YoutubeExplode.Playlists;
using YoutubeExplode.Search;

/// <summary>
/// YouTube search via YoutubeExplode — display tier. Includes the enormous
/// Persian music corpus on YouTube. Download is NOT handled here: the download
/// manager routes YouTube URLs through yt-dlp (which converts to MP3 + embeds
/// thumbnail/metadata), so this provider is search-only by design.
/// </summary>
public sealed class YouTubeProvider : ISearchProvider, IAlbumDiscovery
{
    private readonly YoutubeClient _yt;
    private readonly ILogger<YouTubeProvider> _logger;

    public ProviderId Id => ProviderId.YouTube;
    public string DisplayName => "YouTube";
    public SearchTier Tier => SearchTier.Display;
    public bool IsAvailable => true;

    public YouTubeProvider(SharedHttpClient http, ILogger<YouTubeProvider>? logger = null)
    {
        _yt = new YoutubeClient(http.Create("YouTube", proxied: !string.IsNullOrEmpty(http.ProxyUrl)));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<YouTubeProvider>.Instance;
    }

    public async IAsyncEnumerable<SearchResult> SearchAsync(
        string query, int maxResults = 15,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Collect first (yield isn't allowed inside try/catch), then stream out.
        var results = new List<SearchResult>();
        var enumerator = _yt.Search.GetVideosAsync(query, ct).GetAsyncEnumerator(ct);
        try
        {
            while (results.Count < maxResults && await enumerator.MoveNextAsync().ConfigureAwait(false))
            {
                var v = enumerator.Current;
                results.Add(MapVideo(v));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning("YouTube search failed for {Query}: {Msg}", query, ex.Message);
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var r in results)
            yield return r;
    }

    /// <summary>
    /// Album search (IAlbumDiscovery): Persian albums (Tataloo's \"Jahanam\",
    /// Bahram's \"Sokoot\", …) are uploaded to YouTube as playlists — the catalogs
    /// index them as singles or not at all, and Radio Javan's search only surfaces
    /// one track per album. Probe the query against YouTube PLAYLISTS, keep the
    /// best-titled match, and expand it into its full track list. Every track is
    /// an ordinary YouTube video, so downloads route through yt-dlp as usual.
    /// </summary>
    public async Task<AlbumCandidate?> FindAlbumAsync(string query, CancellationToken ct = default)
    {
        try
        {
            // 1. Search playlists for the query.
            var playlists = new List<PlaylistSearchResult>();
            var enumerator = _yt.Search.GetPlaylistsAsync(query, ct).GetAsyncEnumerator(ct);
            try
            {
                while (playlists.Count < 12 && await enumerator.MoveNextAsync().ConfigureAwait(false))
                    playlists.Add(enumerator.Current);
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }

            // 2. Pick the playlist whose title best matches the query. Require a
            //    real match (score >= 3) — a playlist that just shares one token
            //    (\"fadaei mix\") must not hijack a song query.
            var best = playlists
                .Select(p => new { P = p, Score = ScorePlaylistTitle(p.Title ?? "", query) })
                .Where(x => x.Score >= 3)
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();
            if (best is null) return null;

            // 3. Expand the playlist into its tracks (capped — albums rarely
            //    exceed this, and huge fan-made mixes would swamp the UI).
            var videos = new List<SearchResult>();
            var ve = _yt.Playlists.GetVideosAsync(best.P.Id, ct).GetAsyncEnumerator(ct);
            try
            {
                while (videos.Count < 40 && await ve.MoveNextAsync().ConfigureAwait(false))
                    videos.Add(MapVideo(ve.Current, best.P.Title ?? "", videos.Count + 1));
            }
            finally
            {
                await ve.DisposeAsync().ConfigureAwait(false);
            }

            if (videos.Count < 3) return null; // a 1-2 video playlist is a single, not an album
            var album = new AlbumRef(
                best.P.Id.Value,
                best.P.Title ?? query,
                best.P.Author?.ChannelTitle ?? "",
                ProviderId.YouTube);
            return new AlbumCandidate(album, videos);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug("YouTube album discovery failed for {Query}: {Msg}", query, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// How strongly a playlist title matches the query (0 = no match). All query
    /// tokens must appear in the title (cross-script, fuzzy) — \"tataloo jahanam\"
    /// needs a playlist that names BOTH, so a random \"jahanam\"-themed mix can't
    /// claim the query. Album-ish marker words add a bonus so \"Hagh (Full Album)\"
    /// beats \"Hagh - Live 2019\".
    /// </summary>
    internal static int ScorePlaylistTitle(string title, string query)
    {
        if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(query)) return 0;
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 2)
            .ToArray();
        if (tokens.Length == 0) return 0;

        var titleNorm = TrackTextNormalizer.Normalize(title);
        if (titleNorm.Length == 0) return 0;

        // Every query token must be found in the title (fuzzy cross-script).
        foreach (var t in tokens)
        {
            var ok = TrackTextNormalizer.KeysOverlap(title, t)
                     || TrackTextNormalizer.ContainsAllTokens(title, t, fuzzy: true, substring: true)
                     || TrackTextNormalizer.ContainsPhraseSpaceless(title, t);
            if (!ok) return 0;
        }

        var score = tokens.Length;
        // Album markers strengthen the claim; song-ish markers weaken it.
        var tl = title.ToLowerInvariant();
        if (tl.Contains("album") || title.Contains("آلبوم") || title.Contains("کامل") || tl.Contains("full"))
            score += 2;
        if (tl.Contains("live") || title.Contains("لایو") || tl.Contains("clip") || tl.Contains("موزیک ویدیو"))
            score -= 2;
        // Multi-word titles that match MORE than the query's tokens are likely
        // the real album (\"fadaei - hagh (full album)\" vs the query \"fadaei hagh\").
        var titleTokens = titleNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (titleTokens >= tokens.Length + 1) score += 1;
        return score;
    }

    private static SearchResult MapVideo(VideoSearchResult v) => new()
    {
        Provider = ProviderId.YouTube,
        Id = v.Id.Value,
        Metadata = new TrackMetadata
        {
            Title = v.Title,
            Artist = v.Author?.ChannelTitle ?? "",
            Duration = v.Duration,
            ArtworkUri = v.Thumbnails.FirstOrDefault() is { } t && Uri.TryCreate(t.Url, UriKind.Absolute, out var u) ? u : null,
        },
        MaxQuality = StreamQuality.High192K,
        SourceUrl = $"https://www.youtube.com/watch?v={v.Id.Value}",
        Downloadable = true,
    };

    private static SearchResult MapVideo(PlaylistVideo v, string album, int trackNumber) => new()
    {
        Provider = ProviderId.YouTube,
        Id = v.Id.Value,
        Metadata = new TrackMetadata
        {
            Title = v.Title,
            Artist = v.Author?.ChannelTitle ?? "",
            Album = album,
            AlbumId = "yt:" + album,
            TrackNumber = trackNumber,
            Duration = v.Duration,
            ArtworkUri = v.Thumbnails.FirstOrDefault() is { } t && Uri.TryCreate(t.Url, UriKind.Absolute, out var u) ? u : null,
        },
        MaxQuality = StreamQuality.High192K,
        SourceUrl = $"https://www.youtube.com/watch?v={v.Id.Value}",
        Downloadable = true,
    };
}
