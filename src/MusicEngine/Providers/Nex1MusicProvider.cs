namespace MusicEngine.Providers;

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Web;
using Downloads;
using HtmlAgilityPack;
using Http;
using Microsoft.Extensions.Logging;
using Models;

/// <summary>
/// Nex1Music (largest Iranian music index) — download tier, fully domestic.
/// Search parses the site's "آهنگ-…" post links; each track page embeds the
/// direct mp3 in a <c>data-music</c> attribute on <c>div.item</c> (with
/// <c>data-artist</c>/<c>data-track</c>) — the old <c>div.lnkdl</c> buttons were
/// removed in a 2025 redesign. The site currently serves 128 kbps only.
/// </summary>
public sealed partial class Nex1MusicProvider : ISearchProvider, IDownloadProvider
{
    private const string HostMobile = "https://nex1music.com";
    private const string UserAgent =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1.15";

    private readonly HttpClient _http;
    private readonly ILogger<Nex1MusicProvider> _logger;

    public ProviderId Id => ProviderId.Nex1Music;
    public string DisplayName => "Nex1Music";
    public SearchTier Tier => SearchTier.DownloadOnly;
    public bool IsAvailable => true;

    public Nex1MusicProvider(SharedHttpClient http, ILogger<Nex1MusicProvider>? logger = null)
    {
        // insecureTls: CDN pages may serve self-signed certs (BUG-13 family).
        _http = http.Create("Nex1Music", insecureTls: true);
        // Full browser fingerprint — the site serves bots a stub page.
        SharedHttpClient.ApplyBrowserHeaders(_http, "https://nex1music.com/");
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<Nex1MusicProvider>.Instance;
    }

    public bool CanDownload(SearchResult result) => result.Provider == ProviderId.Nex1Music;

    public async IAsyncEnumerable<SearchResult> SearchAsync(
        string query, int maxResults = 10,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // The mobile site's search is Persian-first: "tataloo behesht" → "تتلو بهشت".
        var effective = Text.TrackTextNormalizer.HasPersian(query)
            ? query
            : Text.FinglishConverter.Convert(query);
        var dashed = effective.Trim().Replace(' ', '-');
        var html = await GetStringAsync($"{HostMobile}/?s={Uri.EscapeDataString(dashed)}", ct).ConfigureAwait(false);
        if (html.Length == 0)
        {
            _logger.LogDebug("Nex1Music empty response (Cloudflare?) for {Query}", query);
            yield break;
        }

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        // nex1music.com result entries: <a href="https://nex1music.com/آهنگ-artist-title/">
        var moreNodes = doc.DocumentNode.SelectNodes("//a[contains(@href,'/آهنگ-')]");
        if (moreNodes is null) yield break;

        var count = 0;
        var usedHrefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in moreNodes)
        {
            if (count >= maxResults) yield break;
            ct.ThrowIfCancellationRequested();
            var href = node.GetAttributeValue("href", "");
            if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)) continue;
            if (!usedHrefs.Add(uri.AbsolutePath)) continue;

            var slug = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/');
            var lastSegment = slug.Split('/')[^1];
            // "آهنگ-<artist words>-<title words>" → drop the آهنگ prefix; the
            // goal gate matches the combined text, so artist/title ambiguity
            // inside the slug is fine.
            var maybeTitle = lastSegment.StartsWith("آهنگ-", StringComparison.Ordinal)
                ? lastSegment["آهنگ-".Length..]
                : lastSegment;
            maybeTitle = maybeTitle.Replace('-', ' ').Trim();
            if (maybeTitle.Length < 3) continue;
            var track = await GetTrackAsync(uri.AbsoluteUri, maybeTitle, ct).ConfigureAwait(false);
            if (track is null) continue;

            yield return new SearchResult
            {
                Provider = ProviderId.Nex1Music,
                Id = uri.AbsolutePath,
                Metadata = new TrackMetadata { Title = track.Title, Artist = track.Artist,
                    Duration = track.Duration, ArtworkUri = TryUri(track.ArtworkUrl) },
                DirectStreamUri = new Uri(track.Url),
                MaxQuality = track.Variant == QualityVariant.Q320 ? StreamQuality.Maximum256K
                    : track.Variant == QualityVariant.Q192 ? StreamQuality.High192K
                    : StreamQuality.Standard128K,
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
        progress?.Report(new DownloadProgress(DownloadPhase.Resolving, 0, null, "Nex1Music: resolving URL"));
        // Always re-resolve: the CDN URL from search may be expired or stale.
        // Fall back to the stored URL only if re-resolution fails.
        string? finalUrl = null;
        try
        {
            var found = await GetTrackAsync(track.SourceUrl, track.Metadata.Title, ct).ConfigureAwait(false);
            finalUrl = found?.Url;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Nex1Music re-resolve failed ({Msg}); falling back to stored URL", ex.Message);
        }
        finalUrl ??= track.DirectStreamUri?.OriginalString;
        if (string.IsNullOrEmpty(finalUrl))
            throw new InvalidOperationException("No download URL on Nex1Music page.");

        var name = ExtractFilename(finalUrl);
        var finalPath = Path.Combine(options.OutputDirectory,
            FileNaming.Build(new TrackMetadata { Title = name ?? track.Metadata.Title, Artist = track.Metadata.Artist }, track, ".mp3", options.FilenameTemplate));
        await HttpDownloader.DownloadToFileAsync(_http, finalUrl, finalPath, progress, ct).ConfigureAwait(false);
        return new DownloadResult(finalPath, StreamQuality.Maximum256K, ProviderId.Nex1Music);
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
            _logger.LogDebug("Nex1Music HTTP failure for {Url}: {Msg}", url, ex.Message);
            return "";
        }
    }

    /// <summary>Fetch the track page and pick its direct mp3. The 2025 redesign
    /// embeds every track on the page in <c>div.item[data-music]</c> — prefer the
    /// item whose artist+title matches the slug (the page also carries a generic
    /// "latest songs" widget), fall back to the first item.</summary>
    private async Task<TrackLink?> GetTrackAsync(string trackPageUrl, string slugText, CancellationToken ct)
    {
        var html = await GetStringAsync(trackPageUrl, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(html)) return null;

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var items = doc.DocumentNode
            .SelectNodes("//div[contains(@class,'item')][@data-music]")
            ?.Select(n => new TrackLink(
                Url: (n.GetAttributeValue("data-music", "") ?? "").Replace(" ", "%20"),
                Artist: n.GetAttributeValue("data-artist", "") ?? "",
                Title: n.GetAttributeValue("data-track", "") ?? "",
                Variant: QualityFromUrl(n.GetAttributeValue("data-music", "") ?? ""),
                Duration: null,
                ArtworkUrl: null))
            .Where(t => t.Url.Length > 0)
            .ToList();
        if (items is null || items.Count == 0) return null;

        var best = items.FirstOrDefault(t =>
            Text.TrackTextNormalizer.KeysOverlap(t.Artist + " " + t.Title, slugText)
            || Text.TrackTextNormalizer.ContainsAllTokens(t.Artist + " " + t.Title, slugText));
        best ??= items.First();
        var duration = ExtractDurationFromPage(html);
        var artwork = ExtractArtworkFromPage(doc);
        return new TrackLink(
            best.Url,
            best.Artist.Length > 0 ? best.Artist : "",
            best.Title.Length > 0 ? best.Title : slugText,
            best.Variant,
            duration,
            artwork);
    }

    private static QualityVariant QualityFromUrl(string url)
    {
        if (url.Contains("320")) return QualityVariant.Q320;
        if (url.Contains("192")) return QualityVariant.Q192;
        if (url.Contains("128")) return QualityVariant.Q128;
        return QualityVariant.Unknown;
    }

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

    private static Uri? TryUri(string? s) =>
        !string.IsNullOrWhiteSpace(s) && Uri.TryCreate(s, UriKind.Absolute, out var u) ? u : null;

    private static string? ExtractArtworkFromPage(HtmlDocument doc)
    {
        // og:image is the most reliable across all Persian music sites
        var ogImage = doc.DocumentNode.SelectSingleNode("//meta[@property='og:image']");
        var content = ogImage?.GetAttributeValue("content", "");
        if (!string.IsNullOrWhiteSpace(content) && content.StartsWith("http"))
            return content;
        // Fallback: first large cover image
        var img = doc.DocumentNode.SelectSingleNode("//img[contains(@class,'cover') or contains(@class,'artwork') or contains(@class,'thumb')]");
        var src = img?.GetAttributeValue("src", "");
        if (!string.IsNullOrWhiteSpace(src) && src.StartsWith("http"))
            return src;
        return null;
    }

    private static string? ExtractFilename(string url)
    {
        if (!url.Contains("filename=")) return null;
        var match = FilenameRegex().Match(url);
        return match.Success ? HttpUtility.UrlDecode(match.Groups[1].Value) : null;
    }

    // BUG-15: source-generated instead of the static Regex.Match(string, pattern) path.
    [System.Text.RegularExpressions.GeneratedRegex(@"filename=([^&]+)")]
    private static partial System.Text.RegularExpressions.Regex FilenameRegex();

    [GeneratedRegex("music:duration\"\\s+content=\"(\\d+)\"")]
    private static partial Regex DurationMetaRegex();

    [GeneratedRegex("(\\d{1,2}):(\\d{2})")]
    private static partial Regex DurationTextRegex();

    [GeneratedRegex("زمان\\s*:\\s*(\\d{1,2}):(\\d{2})")]
    private static partial Regex PersianDurationRegex();

    [GeneratedRegex("T\\d{2}:\\d{2}")]
    private static partial Regex T_iso_pattern();

    private enum QualityVariant { Unknown = 0, Q128 = 1, Q192 = 2, Q320 = 3 }

    private sealed record TrackLink(string Url, string Artist, string Title, QualityVariant Variant,
        TimeSpan? Duration, string? ArtworkUrl);
}
