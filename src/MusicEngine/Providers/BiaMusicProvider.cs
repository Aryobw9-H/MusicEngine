namespace MusicEngine.Providers;

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Downloads;
using HtmlAgilityPack;
using Http;
using Microsoft.Extensions.Logging;
using Models;

/// <summary>
/// BiaMusic (biamusic.ir) — domestic MP3 320/128, fully domestic.
/// Search: GET https://biamusic.ir/?s={query} or /search/{query}
/// CDN: dl.biamusic.ir/Tak/{Artist}/{Artist} - {Title}.mp3
/// The search results page embeds direct CDN mp3 links in &lt;a&gt; tags.
/// </summary>
public sealed partial class BiaMusicProvider : ISearchProvider, IDownloadProvider
{
    private const string Host = "https://biamusic.ir";
    private const string CdnHost = "https://dl.biamusic.ir";
    private const string UserAgent =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1.15";

    private readonly HttpClient _http;
    private readonly ILogger<BiaMusicProvider> _logger;

    public ProviderId Id => ProviderId.BiaMusic;
    public string DisplayName => "BiaMusic";
    public SearchTier Tier => SearchTier.DownloadOnly;
    public bool IsAvailable => true;

    public BiaMusicProvider(SharedHttpClient http, ILogger<BiaMusicProvider>? logger = null)
    {
        // insecureTls: CDN pages may serve self-signed certs (BUG-13 family).
        _http = http.Create("BiaMusic", insecureTls: true);
        SharedHttpClient.ApplyBrowserHeaders(_http, "https://biamusic.ir/");
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<BiaMusicProvider>.Instance;
    }

    public bool CanDownload(SearchResult result) => result.Provider == ProviderId.BiaMusic;

    public async IAsyncEnumerable<SearchResult> SearchAsync(
        string query, int maxResults = 10,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // biamusic.ir uses /search/{query} for Persian text
        var url = $"{Host}/search/{Uri.EscapeDataString(query)}";
        var html = await GetStringAsync(url, ct).ConfigureAwait(false);
        if (html.Length == 0)
        {
            _logger.LogDebug("BiaMusic empty response for {Query}", query);
            yield break;
        }

        // BiaMusic embeds CDN mp3 links directly in search results
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // Find all <a> tags with dl.biamusic.ir mp3 links
        var links = doc.DocumentNode.SelectNodes("//a[contains(@href,'dl.biamusic.ir')]");
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

            // Parse artist and title from CDN URL pattern:
            // dl.biamusic.ir/Tak/{Artist}/{Artist} - {Title}.mp3
            var (artist, title) = ParseCdnUrl(href);
            if (string.IsNullOrEmpty(title)) continue;

            yield return new SearchResult
            {
                Provider = ProviderId.BiaMusic,
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
        progress?.Report(new DownloadProgress(DownloadPhase.Resolving, 0, null, "BiaMusic: downloading"));
        // BiaMusic embeds CDN URLs directly in search — the URL is still live
        // (CDN paths are stable), but validate with a ranged probe before committing.
        var url = track.DirectStreamUri?.OriginalString ?? track.SourceUrl;
        if (string.IsNullOrEmpty(url))
            throw new InvalidOperationException("BiaMusic: no download URL.");

        var name = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(new Uri(url).AbsolutePath));
        var finalPath = Path.Combine(options.OutputDirectory,
            FileNaming.Build(new TrackMetadata { Title = name ?? track.Metadata.Title, Artist = track.Metadata.Artist }, track, ".mp3", options.FilenameTemplate));
        await HttpDownloader.DownloadToFileAsync(_http, url, finalPath, progress, ct).ConfigureAwait(false);
        return new DownloadResult(finalPath, StreamQuality.Maximum256K, ProviderId.BiaMusic);
    }

    /// <summary>
    /// Parse CDN URL to extract artist and title.
    /// Pattern: dl.biamusic.ir/Tak/{Artist}/{Artist} - {Title}.mp3
    /// </summary>
    private static (string Artist, string Title) ParseCdnUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var path = Uri.UnescapeDataString(uri.AbsolutePath);
            // /Tak/{Artist}/{Artist} - {Title}.mp3
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3)
            {
                var artist = parts[1]; // "Ali%20Fadaei%20Fard" -> "Ali Fadaei Fard"
                var filename = parts[^1]; // "{Artist} - {Title}.mp3"
                var dashIdx = filename.IndexOf(" - ", StringComparison.Ordinal);
                if (dashIdx > 0)
                {
                    var title = filename[(dashIdx + 3)..].Replace(".mp3", "").Trim();
                    return (artist, title);
                }
                // Fallback: use filename without extension
                return (artist, filename.Replace(".mp3", "").Trim());
            }
        }
        catch { /* parse failure */ }
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
            _logger.LogDebug("BiaMusic HTTP failure for {Url}: {Msg}", url, ex.Message);
            return "";
        }
    }
}
