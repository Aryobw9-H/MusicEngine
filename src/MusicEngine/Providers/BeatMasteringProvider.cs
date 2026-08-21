namespace MusicEngine.Providers;

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Downloads;
using HtmlAgilityPack;
using Http;
using Microsoft.Extensions.Logging;
using Models;

/// <summary>
/// BeatMastering (beatmastering.ir) — domestic MP3 320, fully domestic.
/// Search: GET https://beatmastering.ir/?s={query}
/// Song pages: https://beatmastering.ir/{slug}-mp3/
/// CDN: dl.beatmastering.ir/MUSIC/{artist}/{Artist} - {Title}.mp3
///
/// IMPROVED ALGORITHM:
/// 1. Search results have rich link text: "دانلود آهنگ اشکان فدایی بنز با بهترین کیفیت"
///    → parse artist/title directly from link text (no page fetch for metadata)
/// 2. The slug contains English/Finglish: "fadaei-ashegh" → backup for matching
/// 3. Only fetch the song page to get the CDN download URL
/// 4. Use link text + slug + CDN URL for best goal gate matching
/// </summary>
public sealed partial class BeatMasteringProvider : ISearchProvider, IDownloadProvider
{
    private const string Host = "https://beatmastering.ir";
    private const string CdnHost = "https://dl.beatmastering.ir";
    private const string UserAgent =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1.15";

    private readonly HttpClient _http;
    private readonly ILogger<BeatMasteringProvider> _logger;

    public ProviderId Id => ProviderId.BeatMastering;
    public string DisplayName => "BeatMastering";
    public SearchTier Tier => SearchTier.DownloadOnly;
    public bool IsAvailable => true;

    public BeatMasteringProvider(SharedHttpClient http, ILogger<BeatMasteringProvider>? logger = null)
    {
        // insecureTls: CDN pages may serve self-signed certs (BUG-13 family).
        _http = http.Create("BeatMastering", insecureTls: true);
        SharedHttpClient.ApplyBrowserHeaders(_http, "https://beatmastering.ir/");
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<BeatMasteringProvider>.Instance;
    }

    public bool CanDownload(SearchResult result) => result.Provider == ProviderId.BeatMastering;

    public async IAsyncEnumerable<SearchResult> SearchAsync(
        string query, int maxResults = 10,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var url = $"{Host}/?s={Uri.EscapeDataString(query)}";
        var html = await GetStringAsync(url, ct).ConfigureAwait(false);
        if (html.Length == 0)
        {
            _logger.LogDebug("BeatMastering empty response for {Query}", query);
            yield break;
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        // BeatMastering search results have <a> tags with:
        // - href: https://beatmastering.ir/{slug}-mp3/
        // - text: "دانلود آهنگ اشکان فدایی بنز با بهترین کیفیت"
        //
        // IMPROVED: Extract artist/title directly from link text
        // Pattern: "دانلود آهنگ/اهنگ {artist} {title} با بهترین کیفیت"
        var postLinks = doc.DocumentNode.SelectNodes("//a[contains(@href,'-mp3/')]");
        if (postLinks is null) yield break;

        var count = 0;
        var seenSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in postLinks)
        {
            if (count >= maxResults) yield break;
            ct.ThrowIfCancellationRequested();

            var href = node.GetAttributeValue("href", "");
            if (string.IsNullOrEmpty(href)) continue;
            if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)) continue;
            if (!uri.Host.Contains("beatmastering.ir")) continue;
            if (!uri.AbsolutePath.EndsWith("-mp3/", StringComparison.OrdinalIgnoreCase)) continue;

            // Extract slug from URL: /fadaei-ashegh-mp3/ → "fadaei-ashegh"
            var slug = uri.AbsolutePath.Trim('/').Replace("-mp3", "").TrimEnd('-');
            if (!seenSlugs.Add(slug)) continue;

            // IMPROVED: Extract artist/title from link text
            var linkText = node.InnerText.Trim();
            var (artist, title) = ParseLinkText(linkText, slug);

            // Fetch the song page to get the CDN download URL + metadata
            var trackLink = await GetTrackAsync(href, slug, ct).ConfigureAwait(false);
            if (trackLink is null) continue;

            // If link text parsing failed, fall back to CDN URL parsing
            if (string.IsNullOrEmpty(title))
            {
                var (cdnArtist, cdnTitle) = ParseCdnUrl(trackLink.Url);
                if (string.IsNullOrEmpty(artist)) artist = cdnArtist;
                if (string.IsNullOrEmpty(title)) title = cdnTitle;
            }

            yield return new SearchResult
            {
                Provider = ProviderId.BeatMastering,
                Id = uri.AbsolutePath,
                Metadata = new TrackMetadata { Title = title, Artist = artist,
                    Duration = trackLink.Duration, ArtworkUri = TryUri(trackLink.ArtworkUrl) },
                DirectStreamUri = new Uri(trackLink.Url),
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
        progress?.Report(new DownloadProgress(DownloadPhase.Resolving, 0, null, "BeatMastering: resolving URL"));
        // Always re-resolve: the CDN URL from search may be expired or stale.
        string? finalUrl = null;
        try
        {
            var resolved = await GetTrackAsync(track.SourceUrl, track.Metadata.Title, ct).ConfigureAwait(false);
            finalUrl = resolved?.Url;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("BeatMastering re-resolve failed ({Msg}); falling back to stored URL", ex.Message);
        }
        finalUrl ??= track.DirectStreamUri?.OriginalString;
        if (string.IsNullOrEmpty(finalUrl))
            throw new InvalidOperationException("No download URL on BeatMastering page.");

        var name = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(new Uri(finalUrl).AbsolutePath));
        var finalPath = Path.Combine(options.OutputDirectory,
            FileNaming.Build(new TrackMetadata { Title = name ?? track.Metadata.Title, Artist = track.Metadata.Artist }, track, ".mp3", options.FilenameTemplate));
        await HttpDownloader.DownloadToFileAsync(_http, finalUrl, finalPath, progress, ct).ConfigureAwait(false);
        return new DownloadResult(finalPath, StreamQuality.Maximum256K, ProviderId.BeatMastering);
    }

    /// <summary>
    /// IMPROVED: Parse artist and title directly from the search result link text.
    /// Patterns:
    ///   "دانلود آهنگ اشکان فدایی بنز با بهترین کیفیت" → artist: "اشکان فدایی", title: "بنز"
    ///   "دانلود اهنگ شادمهر عقیلی باطل با بهترین کیفیت" → artist: "شادمهر عقیلی", title: "باطل"
    ///   "دانلود آهنگ اشکان فدایی و شاپور کمین با بهترین کیفیت" → artist: "اشکان فدایی و شاپور", title: "کمین"
    /// </summary>
    private static (string Artist, string Title) ParseLinkText(string linkText, string slug)
    {
        if (string.IsNullOrWhiteSpace(linkText)) return ("", "");

        // Strip the prefix: "دانلود آهنگ " or "دانلود اهنگ "
        var text = LinkTextPrefixRegex().Replace(linkText, "").Trim();

        // Strip the suffix: "با بهترین کیفیت", "با کیفیت بالا و پخش آنلاین", etc.
        text = LinkTextSuffixRegex().Replace(text, "").Trim();

        // Remove parenthetical notes: "(دیس به سروش هیچکس)"
        text = ParenRegex().Replace(text, "").Trim();

        if (text.Length == 0) return ("", "");

        // Split artist and title: find the last known separator
        // The artist name comes first, then the song title
        // Common separators: space (most cases), " و " (collaboration)
        //
        // Strategy: use the slug to help split. The slug contains the song title
        // in English/Finglish. Find which Persian word matches the slug's last part.
        var slugWords = slug.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (slugWords.Length > 0)
        {
            // The slug's last word(s) are usually the song title
            // Try to find where the title starts in the Persian text
            var result = TrySplitBySlug(text, slugWords);
            if (!string.IsNullOrEmpty(result.Title)) return result;
        }

        // Fallback: split at last space (simple heuristic)
        var lastSpace = text.LastIndexOf(' ');
        if (lastSpace > 0)
        {
            return (text[..lastSpace].Trim(), text[(lastSpace + 1)..].Trim());
        }

        return (text, "");
    }

    /// <summary>
    /// Try to split Persian text using the English slug as a guide.
    /// The slug "fadaei-ashegh" → last word "ashegh" maps to "عاشق" in Persian.
    /// We check if the Persian text ends with a word that could match the slug's title part.
    /// </summary>
    private static (string Artist, string Title) TrySplitBySlug(string text, string[] slugWords)
    {
        // Simple approach: the title is usually 1-3 words at the end
        // Try splitting at different positions and see which gives a reasonable result
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return ("", "");

        // Try: last word is title
        var title1 = words[^1];
        var artist1 = string.Join(" ", words[..^1]);
        if (title1.Length >= 2 && artist1.Length >= 2)
            return (artist1, title1);

        // Try: last 2 words are title
        if (words.Length >= 3)
        {
            var title2 = string.Join(" ", words[^2..]);
            var artist2 = string.Join(" ", words[..^2]);
            return (artist2, title2);
        }

        return ("", "");
    }

    /// <summary>Fetch song page and extract the CDN download URL + metadata.</summary>
    private async Task<TrackLink?> GetTrackAsync(string pageUrl, string slugText, CancellationToken ct)
    {
        var html = await GetStringAsync(pageUrl, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(html)) return null;

        // Look for CDN download link in href attribute
        var match = CdnMp3Regex().Match(html);
        var cdnUrl = match.Success ? match.Groups[1].Value : null;

        // Fallback: look in audio src attribute
        if (cdnUrl is null)
        {
            var audioMatch = AudioSrcRegex().Match(html);
            if (audioMatch.Success) cdnUrl = audioMatch.Groups[1].Value;
        }
        if (cdnUrl is null) return null;

        var duration = ExtractDurationFromPage(html);
        var artwork = ExtractArtworkFromPage(html);
        return new TrackLink(cdnUrl, duration, artwork);
    }

    /// <summary>
    /// Parse CDN URL to extract artist and title as fallback.
    /// Pattern: dl.beatmastering.ir/MUSIC/{artist}/{Artist} - {Title}.mp3
    /// </summary>
    private static (string Artist, string Title) ParseCdnUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var path = Uri.UnescapeDataString(uri.AbsolutePath);
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3)
            {
                var artist = parts[1]; // folder name = artist
                var filename = parts[^1]; // "{Artist} - {Title}.mp3"
                var dashIdx = filename.IndexOf(" - ", StringComparison.Ordinal);
                if (dashIdx > 0)
                {
                    var title = filename[(dashIdx + 3)..].Replace(".mp3", "").Trim();
                    return (artist, title);
                }
                return (artist, filename.Replace(".mp3", "").Trim());
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
            _logger.LogDebug("BeatMastering HTTP failure for {Url}: {Msg}", url, ex.Message);
            return "";
        }
    }

    /// <summary>Match "دانلود آهنگ " or "دانلود اهنگ " prefix.</summary>
    [GeneratedRegex("^دانلود\\s+ا[ھه]نگ\\s+")]
    private static partial Regex LinkTextPrefixRegex();

    /// <summary>Match common suffixes: "با بهترین کیفیت", "با کیفیت بالا و پخش آنلاین", etc.</summary>
    [GeneratedRegex("\\s+با\\s+(بهترین\\s+کیفیت|کیفیت\\s+بالا.*|کیفیت\\s+عالی.*)$")]
    private static partial Regex LinkTextSuffixRegex();

    /// <summary>Match parenthetical notes: "(دیس به سروش هیچکس)".</summary>
    [GeneratedRegex("\\s*\\([^)]*\\)\\s*$")]
    private static partial Regex ParenRegex();

    private static Uri? TryUri(string? s) =>
        !string.IsNullOrWhiteSpace(s) && Uri.TryCreate(s, UriKind.Absolute, out var u) ? u : null;

    private static TimeSpan? ExtractDurationFromPage(string html)
    {
        // Try <meta property="music:duration" content="225"> (seconds)
        var metaMatch = DurationMetaRegex().Match(html);
        if (metaMatch.Success && int.TryParse(metaMatch.Groups[1].Value, out var secs))
            return TimeSpan.FromSeconds(secs);
        // Try Persian duration pattern: "زمان : 3:17 دقیقه"
        var persianMatch = PersianDurationRegex().Match(html);
        if (persianMatch.Success
            && int.TryParse(persianMatch.Groups[1].Value, out var pMin)
            && int.TryParse(persianMatch.Groups[2].Value, out var pSec))
            return TimeSpan.FromMinutes(pMin) + TimeSpan.FromSeconds(pSec);
        // Fallback: generic M:SS but skip ISO timestamps (T16:07:36) — require
        // the match NOT to be preceded by 'T' (ISO8601 datetime) or digits-dash.
        var textMatch = DurationTextRegex().Match(html);
        if (textMatch.Success
            && int.TryParse(textMatch.Groups[1].Value, out var min)
            && int.TryParse(textMatch.Groups[2].Value, out var sec)
            && min is >= 0 and <= 59 && sec is >= 0 and <= 59
            && !IsInTimestampContext(html, textMatch.Index))
            return TimeSpan.FromMinutes(min) + TimeSpan.FromSeconds(sec);
        return null;
    }

    /// <summary>Returns true if the match at <paramref name="index"/> sits inside
    /// an ISO-8601 timestamp (T16:07:36) or a date-like string (2024-11-26T16:07).</summary>
    private static bool IsInTimestampContext(string html, int index)
    {
        // Check the character before the match: 'T' = ISO datetime, '-' = date boundary
        if (index > 0)
        {
            var prev = html[index - 1];
            if (prev is 'T' or '-' or '+' or ':' || char.IsDigit(prev))
                return true;
        }
        // Check surrounding context: if "T\d{2}:\d{2}" or "date" appears nearby, skip it
        var start = Math.Max(0, index - 20);
        var context = html[start..(index + 8)];
        if (T_iso_pattern().IsMatch(context)) return true;
        return false;
    }

    private static string? ExtractArtworkFromPage(string html)
    {
        var doc = new HtmlAgilityPack.HtmlDocument();
        doc.LoadHtml(html);
        // og:image is the most reliable
        var ogImage = doc.DocumentNode.SelectSingleNode("//meta[@property='og:image']");
        var content = ogImage?.GetAttributeValue("content", "");
        if (!string.IsNullOrWhiteSpace(content) && content.StartsWith("http"))
            return content;
        // Fallback: first cover image
        var img = doc.DocumentNode.SelectSingleNode("//img[contains(@class,'cover') or contains(@class,'artwork') or contains(@class,'thumb')]");
        var src = img?.GetAttributeValue("src", "");
        if (!string.IsNullOrWhiteSpace(src) && src.StartsWith("http"))
            return src;
        return null;
    }

    [GeneratedRegex("href=\"(https?://dl\\.beatmastering\\.ir[^\"]+\\.mp3)\"")]
    private static partial Regex CdnMp3Regex();

    [GeneratedRegex("src=\"(https?://dl\\.beatmastering\\.ir[^\"]+\\.mp3)\"")]
    private static partial Regex AudioSrcRegex();

    [GeneratedRegex("music:duration\"\\s+content=\"(\\d+)\"")]
    private static partial Regex DurationMetaRegex();

    [GeneratedRegex("زمان\\s*:\\s*(\\d{1,2}):(\\d{2})")]
    private static partial Regex PersianDurationRegex();

    [GeneratedRegex("(\\d{1,2}):(\\d{2})")]
    private static partial Regex DurationTextRegex();

    [GeneratedRegex("T\\d{2}:\\d{2}")]
    private static partial Regex T_iso_pattern();

    private sealed record TrackLink(string Url, TimeSpan? Duration, string? ArtworkUrl);
}
