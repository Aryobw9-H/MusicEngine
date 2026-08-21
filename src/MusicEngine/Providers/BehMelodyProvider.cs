namespace MusicEngine.Providers;

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Downloads;
using HtmlAgilityPack;
using Http;
using Microsoft.Extensions.Logging;
using Models;

/// <summary>
/// BehMelody (behmelody.in) — download tier, fully domestic.
/// Search parses the site's post links; each track page contains download buttons
/// for 320, 128 kbps, and FLAC lossless. The CDN uses Solar Hijri (Jalali) calendar dates.
/// CDN structure:
///   MP3: dl.behmelody.in/{year}/{month}/{day}/{Album}/{Title} (320).mp3
///   FLAC: dl.behmelody.in/{year}/{month}/{day}/{Album} [Flac]/{Track} - {Title}.flac
/// </summary>
public sealed partial class BehMelodyProvider : ISearchProvider, IDownloadProvider
{
    private const string Host = "https://behmelody.in";
    private const string CdnHost = "https://dl.behmelody.in";
    private const string UserAgent =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1.15";

    private readonly HttpClient _http;
    private readonly ILogger<BehMelodyProvider> _logger;

    public ProviderId Id => ProviderId.BehMelody;
    public string DisplayName => "BehMelody";
    public SearchTier Tier => SearchTier.DownloadOnly;
    public bool IsAvailable => true;

    public BehMelodyProvider(SharedHttpClient http, ILogger<BehMelodyProvider>? logger = null)
    {
        // insecureTls: CDN pages may serve self-signed certs (BUG-13 family).
        _http = http.Create("BehMelody", insecureTls: true);
        SharedHttpClient.ApplyBrowserHeaders(_http, "https://behmelody.in/");
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<BehMelodyProvider>.Instance;
    }

    public bool CanDownload(SearchResult result) => result.Provider == ProviderId.BehMelody;

    public async IAsyncEnumerable<SearchResult> SearchAsync(
        string query, int maxResults = 10,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var html = await GetStringAsync($"{Host}/?s={Uri.EscapeDataString(query)}", ct).ConfigureAwait(false);
        if (html.Length == 0)
        {
            _logger.LogDebug("BehMelody empty response for {Query}", query);
            yield break;
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        // behmelody.in result entries: <a href="https://behmelody.in/دانلود-آهنگ-artist-title/">
        var postNodes = doc.DocumentNode.SelectNodes("//a[contains(@href,'دانلود-آهنگ')]");
        if (postNodes is null) yield break;

        var count = 0;
        var usedHrefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in postNodes)
        {
            if (count >= maxResults) yield break;
            ct.ThrowIfCancellationRequested();
            var href = node.GetAttributeValue("href", "");
            if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)) continue;
            if (!uri.Host.Contains("behmelody.in")) continue;
            if (!usedHrefs.Add(uri.AbsolutePath)) continue;

            var slug = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/');
            var lastSegment = slug.Split('/')[^1];
            // "دانلود-آهنگ-<artist>-<title>" → drop the prefix
            var maybeTitle = lastSegment.StartsWith("دانلود-آهنگ-", StringComparison.Ordinal)
                ? lastSegment["دانلود-آهنگ-".Length..]
                : lastSegment;
            maybeTitle = maybeTitle.Replace('-', ' ').Trim();
            if (maybeTitle.Length < 3) continue;

            var track = await GetTrackAsync(uri.AbsoluteUri, maybeTitle, ct).ConfigureAwait(false);
            if (track is null) continue;

            yield return new SearchResult
            {
                Provider = ProviderId.BehMelody,
                Id = uri.AbsolutePath,
                Metadata = new TrackMetadata { Title = track.Title, Artist = track.Artist,
                    Duration = track.Duration, ArtworkUri = TryUri(track.ArtworkUrl) },
                DirectStreamUri = new Uri(track.Url),
                MaxQuality = track.IsFlac ? StreamQuality.Maximum256K : StreamQuality.Maximum256K,
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
        progress?.Report(new DownloadProgress(DownloadPhase.Resolving, 0, null, "BehMelody: resolving URL"));
        // Always re-resolve: the CDN URL from search may be expired or stale.
        string? finalUrl = null;
        try
        {
            var found = await GetTrackAsync(track.SourceUrl, track.Metadata.Title, ct).ConfigureAwait(false);
            finalUrl = found?.Url;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("BehMelody re-resolve failed ({Msg}); falling back to stored URL", ex.Message);
        }
        finalUrl ??= track.DirectStreamUri?.OriginalString;
        if (string.IsNullOrEmpty(finalUrl))
            throw new InvalidOperationException("No download URL on BehMelody page.");

        var isFlac = finalUrl.EndsWith(".flac", StringComparison.OrdinalIgnoreCase);
        var extension = isFlac ? ".flac" : ".mp3";
        var name = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(new Uri(finalUrl!).AbsolutePath));
        var finalPath = Path.Combine(options.OutputDirectory,
            FileNaming.Build(new TrackMetadata { Title = name ?? track.Metadata.Title, Artist = track.Metadata.Artist }, track, extension, options.FilenameTemplate));
        await HttpDownloader.DownloadToFileAsync(_http, finalUrl, finalPath, progress, ct).ConfigureAwait(false);
        return new DownloadResult(finalPath, StreamQuality.Maximum256K, ProviderId.BehMelody);
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
            _logger.LogDebug("BehMelody HTTP failure for {Url}: {Msg}", url, ex.Message);
            return "";
        }
    }

    /// <summary>Fetch the track page and pick its direct mp3/flac URL.</summary>
    private async Task<TrackLink?> GetTrackAsync(string trackPageUrl, string slugText, CancellationToken ct)
    {
        var html = await GetStringAsync(trackPageUrl, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(html)) return null;

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        
        // Look for download links in the page - BehMelody uses JavaScript to construct URLs
        var links = doc.DocumentNode.SelectNodes("//a[contains(@href,'dl.behmelody.in')]");
        if (links is null)
        {
            // Try to find links with download-related classes
            links = doc.DocumentNode.SelectNodes("//a[contains(@class,'download') or contains(@class,'dl')]");
        }
        
        if (links is null || links.Count == 0) return null;

        // Find the best matching link (prefer FLAC over 320 over 128)
        string? bestUrl = null;
        bool isFlac = false;
        foreach (var link in links)
        {
            var href = link.GetAttributeValue("href", "");
            if (string.IsNullOrEmpty(href)) continue;
            
            // Check if it's a direct CDN link
            if (href.Contains("dl.behmelody.in"))
            {
                if (href.EndsWith(".flac", StringComparison.OrdinalIgnoreCase))
                {
                    bestUrl = href;
                    isFlac = true;
                    break; // Prefer FLAC
                }
                if (href.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
                {
                    bestUrl = href;
                    // Don't break yet, might find FLAC later
                }
            }
        }

        // If no direct CDN link found, try to construct one from page content
        if (bestUrl is null)
        {
            // Look for any .mp3 or .flac references in the page
            var flacMatch = FlacUrlRegex().Match(html);
            if (flacMatch.Success)
            {
                bestUrl = flacMatch.Groups[1].Value;
                isFlac = true;
            }
            else
            {
                var mp3Match = Mp3UrlRegex().Match(html);
                if (mp3Match.Success)
                {
                    bestUrl = mp3Match.Groups[1].Value;
                }
            }
        }

        if (bestUrl is null) return null;

        // Parse artist and title from the page content
        var artist = ExtractArtistFromPage(doc);
        var title = ExtractTitleFromPage(doc) ?? slugText;

        var duration = ExtractDurationFromPage(html);
        var artwork = ExtractArtworkFromPage(doc);
        return new TrackLink(bestUrl, artist, title, isFlac, duration, artwork);
    }

    private static string ExtractArtistFromPage(HtmlDocument doc)
    {
        // Try to find artist name in the page
        var artistNode = doc.DocumentNode.SelectSingleNode("//span[contains(@class,'artist')]");
        if (artistNode is not null) return artistNode.InnerText.Trim();
        
        // Fallback: look in title
        var titleNode = doc.DocumentNode.SelectSingleNode("//title");
        if (titleNode is not null)
        {
            var title = titleNode.InnerText;
            var match = ArtistFromTitleRegex().Match(title);
            if (match.Success) return match.Groups[1].Value.Trim();
        }
        return "";
    }

    private static string? ExtractTitleFromPage(HtmlDocument doc)
    {
        var titleNode = doc.DocumentNode.SelectSingleNode("//title");
        if (titleNode is not null)
        {
            var title = titleNode.InnerText;
            var match = TitleFromTitleRegex().Match(title);
            if (match.Success) return match.Groups[1].Value.Trim();
        }
        return null;
    }

    [GeneratedRegex("href=\"(https?://dl\\.behmelody\\.in[^\"]+\\.flac)\"")]
    private static partial Regex FlacUrlRegex();

    [GeneratedRegex("href=\"(https?://dl\\.behmelody\\.in[^\"]+\\.mp3)\"")]
    private static partial Regex Mp3UrlRegex();

    [GeneratedRegex("دانلود آهنگ (.+?) به نام")]
    private static partial Regex ArtistFromTitleRegex();

    [GeneratedRegex("به نام (.+?)$")]
    private static partial Regex TitleFromTitleRegex();

    private sealed record TrackLink(string Url, string Artist, string Title, bool IsFlac,
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
