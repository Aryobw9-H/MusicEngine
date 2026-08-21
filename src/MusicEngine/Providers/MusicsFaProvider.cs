namespace MusicEngine.Providers;

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Downloads;
using HtmlAgilityPack;
using Http;
using Microsoft.Extensions.Logging;
using Models;

/// <summary>
/// MusicsFa (musics-fa.com) — domestic MP3 320/128, fully domestic.
/// Search: GET https://musics-fa.com/?s={query}
/// Song pages: https://musics-fa.com/download-song/{id}/
/// CDN: dls.musics-fa.com/tagdl/{year}/{Artist} - {Title} (320).mp3
///      dls.musics-fa.com/song/{uploader}/{year}/{month}/{Artist} - {Title}.mp3
///
/// The search results page embeds CDN mp3 links directly in the HTML.
/// Song pages also have CDN links for 320kbps and 128kbps.
/// </summary>
public sealed partial class MusicsFaProvider : ISearchProvider, IDownloadProvider
{
    private const string Host = "https://musics-fa.com";
    private const string CdnHost = "https://dls.musics-fa.com";
    private const string UserAgent =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1.15";

    private readonly HttpClient _http;
    private readonly ILogger<MusicsFaProvider> _logger;

    public ProviderId Id => ProviderId.MusicsFa;
    public string DisplayName => "MusicsFa";
    public SearchTier Tier => SearchTier.DownloadOnly;
    public bool IsAvailable => true;

    public MusicsFaProvider(SharedHttpClient http, ILogger<MusicsFaProvider>? logger = null)
    {
        // insecureTls: CDN pages may serve self-signed certs (BUG-13 family).
        _http = http.Create("MusicsFa", insecureTls: true);
        SharedHttpClient.ApplyBrowserHeaders(_http, "https://musics-fa.com/");
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MusicsFaProvider>.Instance;
    }

    public bool CanDownload(SearchResult result) => result.Provider == ProviderId.MusicsFa;

    public async IAsyncEnumerable<SearchResult> SearchAsync(
        string query, int maxResults = 10,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var url = $"{Host}/?s={Uri.EscapeDataString(query)}";
        var html = await GetStringAsync(url, ct).ConfigureAwait(false);
        if (html.Length == 0)
        {
            _logger.LogDebug("MusicsFa empty response for {Query}", query);
            yield break;
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // MusicsFa embeds CDN mp3 links directly in search results:
        // dls.musics-fa.com/tagdl/{year}/{Artist} - {Title} (320).mp3
        // dls.musics-fa.com/song/{uploader}/{year}/{month}/{Artist} - {Title}.mp3
        var links = doc.DocumentNode.SelectNodes("//a[contains(@href,'dls.musics-fa.com')]");
        if (links is null) yield break;

        var count = 0;
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in links)
        {
            if (count >= maxResults) yield break;
            ct.ThrowIfCancellationRequested();

            var href = link.GetAttributeValue("href", "");
            if (string.IsNullOrEmpty(href)) continue;
            if (!href.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)) continue;
            if (!seenUrls.Add(href)) continue;

            // Parse artist and title from CDN URL
            var (artist, title) = ParseCdnUrl(href);
            if (string.IsNullOrEmpty(title)) continue;

            yield return new SearchResult
            {
                Provider = ProviderId.MusicsFa,
                Id = href,
                Metadata = new TrackMetadata { Title = title, Artist = artist },
                DirectStreamUri = new Uri(href),
                MaxQuality = StreamQuality.Maximum256K,
                SourceUrl = href,
                Downloadable = true,
            };
            count++;
        }
    }

    public async Task<DownloadResult> DownloadAsync(
        SearchResult track, DownloadOptions options,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(new DownloadProgress(DownloadPhase.Resolving, 0, null, "MusicsFa: downloading"));
        // MusicsFa embeds CDN URLs directly in search — the URL is still live
        // (CDN paths are stable), but validate with a ranged probe before committing.
        var url = track.DirectStreamUri?.OriginalString ?? track.SourceUrl;
        if (string.IsNullOrEmpty(url))
            throw new InvalidOperationException("MusicsFa: no download URL.");

        var name = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(new Uri(url).AbsolutePath));
        var finalPath = Path.Combine(options.OutputDirectory,
            FileNaming.Build(new TrackMetadata { Title = name ?? track.Metadata.Title, Artist = track.Metadata.Artist }, track, ".mp3", options.FilenameTemplate));
        await HttpDownloader.DownloadToFileAsync(_http, url, finalPath, progress, ct).ConfigureAwait(false);
        return new DownloadResult(finalPath, StreamQuality.Maximum256K, ProviderId.MusicsFa);
    }

    /// <summary>
    /// Parse CDN URL to extract artist and title.
    /// Patterns:
    ///   dls.musics-fa.com/tagdl/{year}/{Artist} - {Title} (320).mp3
    ///   dls.musics-fa.com/song/{uploader}/{year}/{month}/{Artist} - {Title}.mp3
    /// </summary>
    private static (string Artist, string Title) ParseCdnUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var path = Uri.UnescapeDataString(uri.AbsolutePath);
            var filename = Path.GetFileNameWithoutExtension(path);
            // Remove quality suffix like " (320)" or " (128)"
            filename = QualitySuffixRegex().Replace(filename, "").Trim();
            var dashIdx = filename.IndexOf(" - ", StringComparison.Ordinal);
            if (dashIdx > 0)
            {
                var artist = filename[..dashIdx].Trim();
                var title = filename[(dashIdx + 3)..].Trim();
                return (artist, title);
            }
        }
        catch { }
        return ("", "");
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false) : "";
        }
        catch (Exception ex)
        {
            _logger.LogDebug("MusicsFa HTTP failure for {Url}: {Msg}", url, ex.Message);
            return "";
        }
    }

    [GeneratedRegex("\\s*\\(\\d+\\)\\s*$")]
    private static partial Regex QualitySuffixRegex();
}
