namespace MusicEngine.Providers;

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Downloads;
using HtmlAgilityPack;
using Http;
using Microsoft.Extensions.Logging;
using Models;

/// <summary>
/// RozMusic (rozmusic.com) — download tier, fully domestic.
/// Search parses the site's post links; each track page contains download buttons
/// for 320 kbps and 128 kbps. The CDN uses Solar Hijri (Jalali) calendar dates.
/// CDN structure: dl.rozmusic.com/Music/{year}/{month}/{day}/{Artist} - {Title}.mp3
/// </summary>
public sealed partial class RozMusicProvider : ISearchProvider, IDownloadProvider
{
    private const string Host = "https://rozmusic.com";
    private const string CdnHost = "https://dl.rozmusic.com";
    private const string UserAgent =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1.15";

    private readonly HttpClient _http;
    private readonly ILogger<RozMusicProvider> _logger;

    public ProviderId Id => ProviderId.RozMusic;
    public string DisplayName => "RozMusic";
    public SearchTier Tier => SearchTier.DownloadOnly;
    public bool IsAvailable => true;

    public RozMusicProvider(SharedHttpClient http, ILogger<RozMusicProvider>? logger = null)
    {
        // insecureTls: CDN pages may serve self-signed certs (BUG-13 family).
        _http = http.Create("RozMusic", insecureTls: true);
        SharedHttpClient.ApplyBrowserHeaders(_http, "https://rozmusic.com/");
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RozMusicProvider>.Instance;
    }

    public bool CanDownload(SearchResult result) => result.Provider == ProviderId.RozMusic;

    public async IAsyncEnumerable<SearchResult> SearchAsync(
        string query, int maxResults = 10,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var html = await GetStringAsync($"{Host}/?s={Uri.EscapeDataString(query)}", ct).ConfigureAwait(false);
        if (html.Length == 0)
        {
            _logger.LogDebug("RozMusic empty response for {Query}", query);
            yield break;
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        // rozmusic.com result entries: <a href="https://rozmusic.com/آهنگ-artist-title.html">
        var postNodes = doc.DocumentNode.SelectNodes("//a[contains(@href,'.html')]");
        if (postNodes is null) yield break;

        var count = 0;
        var usedHrefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in postNodes)
        {
            if (count >= maxResults) yield break;
            ct.ThrowIfCancellationRequested();
            var href = node.GetAttributeValue("href", "");
            if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)) continue;
            if (!uri.Host.Contains("rozmusic.com")) continue;
            if (!usedHrefs.Add(uri.AbsolutePath)) continue;

            var slug = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/');
            var lastSegment = slug.Split('/')[^1].Replace(".html", "");
            // "آهنگ-<artist words>-<title words>" → drop the آهنگ prefix
            var maybeTitle = lastSegment.StartsWith("آهنگ-", StringComparison.Ordinal)
                ? lastSegment["آهنگ-".Length..]
                : lastSegment;
            maybeTitle = maybeTitle.Replace('-', ' ').Trim();
            if (maybeTitle.Length < 3) continue;

            var track = await GetTrackAsync(uri.AbsoluteUri, maybeTitle, ct).ConfigureAwait(false);
            if (track is null) continue;

            yield return new SearchResult
            {
                Provider = ProviderId.RozMusic,
                Id = uri.AbsolutePath,
                Metadata = new TrackMetadata { Title = track.Title, Artist = track.Artist,
                    Duration = track.Duration, ArtworkUri = TryUri(track.ArtworkUrl) },
                DirectStreamUri = new Uri(track.Url),
                MaxQuality = StreamQuality.Maximum256K,
                SourceUrl = uri.AbsoluteUri,
                Downloadable = true,
            };
            count++;
        }
    }

    public async Task<DownloadResult> DownloadAsync(
        SearchResult track, DownloadOptions options,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(new DownloadProgress(DownloadPhase.Resolving, 0, null, "RozMusic: resolving URL"));
        // Always re-resolve: the CDN URL from search may be expired or stale.
        string? finalUrl = null;
        try
        {
            var found = await GetTrackAsync(track.SourceUrl, track.Metadata.Title, ct).ConfigureAwait(false);
            finalUrl = found?.Url;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("RozMusic re-resolve failed ({Msg}); falling back to stored URL", ex.Message);
        }
        finalUrl ??= track.DirectStreamUri?.OriginalString;
        if (string.IsNullOrEmpty(finalUrl))
            throw new InvalidOperationException("No download URL on RozMusic page.");

        var name = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(new Uri(finalUrl).AbsolutePath));
        var finalPath = Path.Combine(options.OutputDirectory,
            FileNaming.Build(new TrackMetadata { Title = name ?? track.Metadata.Title, Artist = track.Metadata.Artist }, track, ".mp3", options.FilenameTemplate));
        await HttpDownloader.DownloadToFileAsync(_http, finalUrl, finalPath, progress, ct).ConfigureAwait(false);
        return new DownloadResult(finalPath, StreamQuality.Maximum256K, ProviderId.RozMusic);
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
            _logger.LogDebug("RozMusic HTTP failure for {Url}: {Msg}", url, ex.Message);
            return "";
        }
    }

    /// <summary>Fetch the track page and pick its direct mp3 URL.</summary>
    private async Task<TrackLink?> GetTrackAsync(string trackPageUrl, string slugText, CancellationToken ct)
    {
        var html = await GetStringAsync(trackPageUrl, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(html)) return null;

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        
        // Look for download links in the page - RozMusic uses JavaScript to construct URLs
        // The pattern is typically: dl.rozmusic.com/Music/{year}/{month}/{day}/{Artist} - {Title}.mp3
        var links = doc.DocumentNode.SelectNodes("//a[contains(@href,'dl.rozmusic.com')]");
        if (links is null)
        {
            // Try to find links with download-related classes
            links = doc.DocumentNode.SelectNodes("//a[contains(@class,'download') or contains(@class,'dl')]");
        }
        
        if (links is null || links.Count == 0) return null;

        // Find the best matching link (prefer 320 over 128)
        string? bestUrl = null;
        foreach (var link in links)
        {
            var href = link.GetAttributeValue("href", "");
            if (string.IsNullOrEmpty(href)) continue;
            
            // Check if it's a direct CDN link
            if (href.Contains("dl.rozmusic.com") && href.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                bestUrl = href;
                break; // Prefer the first (usually 320)
            }
        }

        // If no direct CDN link found, try to construct one from page content
        if (bestUrl is null)
        {
            // Look for any .mp3 references in the page
            var mp3Match = Mp3UrlRegex().Match(html);
            if (mp3Match.Success)
            {
                bestUrl = mp3Match.Groups[1].Value;
            }
        }

        if (bestUrl is null) return null;

        // Parse artist and title from the URL or page content
        var artist = ExtractArtistFromPage(doc);
        var title = slugText;

        var duration = ExtractDurationFromPage(html);
        var artwork = ExtractArtworkFromPage(doc);
        return new TrackLink(bestUrl, artist, title, duration, artwork);
    }

    private static string ExtractArtistFromPage(HtmlDocument doc)
    {
        // Try to find artist name in the page
        var titleNode = doc.DocumentNode.SelectSingleNode("//title");
        if (titleNode is not null)
        {
            var title = titleNode.InnerText;
            // Title format: "دانلود آهنگ جدید {Artist} به نام {Title}"
            var match = ArtistFromTitleRegex().Match(title);
            if (match.Success) return match.Groups[1].Value.Trim();
        }
        return "";
    }

    [GeneratedRegex("href=\"(https?://dl\\.rozmusic\\.com[^\"]+\\.mp3)\"")]
    private static partial Regex Mp3UrlRegex();

    [GeneratedRegex("دانلود آهنگ جدید (.+?) به نام")]
    private static partial Regex ArtistFromTitleRegex();

    private sealed record TrackLink(string Url, string Artist, string Title,
        TimeSpan? Duration, string? ArtworkUrl);
    private static Uri? TryUri(string? s) =>
        !string.IsNullOrWhiteSpace(s) && Uri.TryCreate(s, UriKind.Absolute, out var u) ? u : null;
    private static TimeSpan? ExtractDurationFromPage(string html)
    {
        var metaMatch = DurationMetaRegex().Match(html);
        if (metaMatch.Success && int.TryParse(metaMatch.Groups[1].Value, out var secs))
            return TimeSpan.FromSeconds(secs);
        var persianMatch = PersianDurationRegex().Match(html);
        if (persianMatch.Success
            && int.TryParse(persianMatch.Groups[1].Value, out var pMin)
            && int.TryParse(persianMatch.Groups[2].Value, out var pSec))
            return TimeSpan.FromMinutes(pMin) + TimeSpan.FromSeconds(pSec);
        var textMatch = DurationTextRegex().Match(html);
        if (textMatch.Success
            && int.TryParse(textMatch.Groups[1].Value, out var min)
            && int.TryParse(textMatch.Groups[2].Value, out var sec)
            && min is >= 0 and <= 59 && sec is >= 0 and <= 59
            && !IsInTimestampContext(html, textMatch.Index))
            return TimeSpan.FromMinutes(min) + TimeSpan.FromSeconds(sec);
        return null;
    }
    private static bool IsInTimestampContext(string html, int index)
    {
        if (index > 0)
        {
            var prev = html[index - 1];
            if (prev is 'T' or '-' or '+' or ':' || char.IsDigit(prev)) return true;
        }
        var start = Math.Max(0, index - 20);
        var context = html[start..(index + 8)];
        if (T_iso_pattern().IsMatch(context)) return true;
        return false;
    }
    private static string? ExtractArtworkFromPage(HtmlDocument doc)
    {
        var ogImage = doc.DocumentNode.SelectSingleNode("//meta[@property='og:image']");
        var content = ogImage?.GetAttributeValue("content", "");
        if (!string.IsNullOrWhiteSpace(content) && content.StartsWith("http")) return content;
        var img = doc.DocumentNode.SelectSingleNode("//img[contains(@class,'cover') or contains(@class,'artwork') or contains(@class,'thumb')]");
        var src = img?.GetAttributeValue("src", "");
        if (!string.IsNullOrWhiteSpace(src) && src.StartsWith("http")) return src;
        return null;
    }
    [GeneratedRegex("music:duration\"\\s+content=\"(\\d+)\"")]
    private static partial Regex DurationMetaRegex();
    [GeneratedRegex("(\\d{1,2}):(\\d{2})")]
    private static partial Regex DurationTextRegex();
    [GeneratedRegex("زمان\\s*:\\s*(\\d{1,2}):(\\d{2})")]
    private static partial Regex PersianDurationRegex();
    [GeneratedRegex("T\\d{2}:\\d{2}")]
    private static partial Regex T_iso_pattern();
}
