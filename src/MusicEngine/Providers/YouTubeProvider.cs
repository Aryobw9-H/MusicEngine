namespace MusicEngine.Providers;

using System.Runtime.CompilerServices;
using Http;
using Microsoft.Extensions.Logging;
using Models;
using YoutubeExplode;

/// <summary>
/// YouTube search via YoutubeExplode — display tier. Includes the enormous
/// Persian music corpus on YouTube. Download is NOT handled here: the download
/// manager routes YouTube URLs through yt-dlp (which converts to MP3 + embeds
/// thumbnail/metadata), so this provider is search-only by design.
/// </summary>
public sealed class YouTubeProvider : ISearchProvider
{
    private readonly YoutubeClient _yt;
    private readonly ILogger<YouTubeProvider> _logger;

    public ProviderId Id => ProviderId.YouTube;
    public string DisplayName => "YouTube";
    public SearchTier Tier => SearchTier.Display;
    public bool IsAvailable => true;

    public YouTubeProvider(SharedHttpClient http, ILogger<YouTubeProvider>? logger = null, string? proxyUrl = null)
    {
        _yt = new YoutubeClient(http.Create("YouTube", proxied: !string.IsNullOrEmpty(proxyUrl)));
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
                results.Add(new SearchResult
                {
                    Provider = ProviderId.YouTube,
                    Id = v.Id.Value,
                    Metadata = new TrackMetadata
                    {
                        Title = v.Title,
                        Artist = v.Author?.ChannelTitle ?? v.Author?.Title ?? "",
                        Duration = v.Duration,
                        ArtworkUri = v.Thumbnails.FirstOrDefault() is { } t && Uri.TryCreate(t.Url, UriKind.Absolute, out var u) ? u : null,
                    },
                    MaxQuality = StreamQuality.High192K,
                    SourceUrl = $"https://www.youtube.com/watch?v={v.Id.Value}",
                    Downloadable = true,
                });
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
}
