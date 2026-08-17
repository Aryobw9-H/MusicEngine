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
/// Nex1Music (largest Iranian music index) — download tier. Search parses the
/// mobile site's "more" links, track pages expose 320/128 proxy URLs whose real
/// filename rides in the <c>filename=</c> query param. Frequently behind
/// Cloudflare; when it yields nothing, other sources take over.
/// </summary>
public sealed class Nex1MusicProvider : ISearchProvider, IDownloadProvider
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
        _http = http.Create("Nex1Music");
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
            var links = await GetQualityLinksAsync(uri.AbsoluteUri, ct).ConfigureAwait(false);
            if (links is not { Count: > 0 }) continue;

            yield return new SearchResult
            {
                Provider = ProviderId.Nex1Music,
                Id = uri.AbsolutePath,
                Metadata = new TrackMetadata { Title = maybeTitle, Artist = "" },
                DirectStreamUri = new Uri(links[0].Url), // first = 320 when present
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
        progress?.Report(new DownloadProgress(DownloadPhase.Resolving, 0, null, "Nex1Music: resolving URL"));
        var finalUrl = track.DirectStreamUri?.OriginalString;
        if (string.IsNullOrEmpty(finalUrl) || !finalUrl!.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            var links = await GetQualityLinksAsync(track.SourceUrl, ct).ConfigureAwait(false) ?? new List<QualityLink>();
            finalUrl = links.FirstOrDefault(q => q.Variant == QualityVariant.Q320)?.Url
                       ?? links.FirstOrDefault()?.Url
                       ?? throw new InvalidOperationException("No download URL on Nex1Music page.");
        }

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

    private async Task<List<QualityLink>?> GetQualityLinksAsync(string trackPageUrl, CancellationToken ct)
    {
        var html = await GetStringAsync(trackPageUrl, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(html)) return null;

        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var links = doc.DocumentNode
            .SelectNodes("//div[contains(@class,'lnkdl')]//a[@href]")?
            .Select(a => (Href: a.GetAttributeValue("href", ""), Text: a.InnerText.Trim()))
            .Where(x => !string.IsNullOrEmpty(x.Href))
            .Select(x => new QualityLink(x.Href, x.Text switch
            {
                string s when s.Contains("320") => QualityVariant.Q320,
                string s when s.Contains("192") => QualityVariant.Q192,
                string s when s.Contains("128") => QualityVariant.Q128,
                _ => QualityVariant.Unknown,
            }))
            .ToList();
        return links is null ? null : links.OrderByDescending(l => (int)l.Variant).ToList();
    }

    private static string? ExtractFilename(string url)
    {
        if (!url.Contains("filename=")) return null;
        var match = Regex.Match(url, @"filename=([^&]+)");
        return match.Success ? HttpUtility.UrlDecode(match.Groups[1].Value) : null;
    }

    private enum QualityVariant { Unknown = 0, Q128 = 1, Q192 = 2, Q320 = 3 }

    private sealed record QualityLink(string Url, QualityVariant Variant);
}
