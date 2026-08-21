namespace MusicEngine.Providers;

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Downloads;
using HtmlAgilityPack;
using Http;
using Microsoft.Extensions.Logging;
using Models;
using Text;

/// <summary>
/// Direct-MP3 finder across Iranian music indexes — download tier, resolved only
/// after the user clicks Download.
///
/// aimusicall.ir — WordPress search (Persian queries only, so the query is
/// Finglish-expanded first). The site moved its SERP from <c>?s=&lt;q&gt;</c> to
/// <c>/search/&lt;q&gt;</c> (the old URL 302s), so we hit the canonical path
/// directly. The SERP and post pages embed dl.aimusicall.ir/…/&lt;artist&gt; -
/// &lt;title&gt;.mp3 links; the CDN has been serving 404 for all of them, so each
/// candidate is liveness-probed (ranged GET) before it is surfaced — a dead
/// link must never reach the queue. When nothing is alive the resolver falls
/// through to the other sources (nex1music, Radio Javan, yt-dlp).
/// </summary>
public sealed class PersianSitesProvider : ISearchProvider, IDownloadProvider
{
    private static readonly Regex Mp3Regex = new(
        @"(?:https?://)?(?:www\.)?dl\.aimusicall\.ir/musics/\d{4}/\d{2}/\d{2}/[^""'<> ]+\.mp3",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly HttpClient _dlHttp; // dl.aimusicall.ir serves a self-signed cert
    private readonly ILogger<PersianSitesProvider> _logger;

    public ProviderId Id => ProviderId.PersianSites;
    public string DisplayName => "Iranian Music Sites";
    public SearchTier Tier => SearchTier.DownloadOnly;
    public bool IsAvailable => true;

    public PersianSitesProvider(SharedHttpClient http, ILogger<PersianSitesProvider>? logger = null)
    {
        _http = http.Create("PersianSites");
        _dlHttp = http.Create("PersianSitesDownload", insecureTls: true);
        // Full browser fingerprint — Iranian CDNs commonly reject bare .NET clients.
        SharedHttpClient.ApplyBrowserHeaders(_http, "https://aimusicall.ir/");
        SharedHttpClient.ApplyBrowserHeaders(_dlHttp, "https://aimusicall.ir/");
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PersianSitesProvider>.Instance;
    }

    public bool CanDownload(SearchResult result) => result.Provider == ProviderId.PersianSites;

    public async IAsyncEnumerable<SearchResult> SearchAsync(
        string query, int maxResults = 5,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // aimusicall's search is Persian-only: "tataloo behesht" → "تتلو بهشت".
        var persianQueries = FinglishQueryExpander.Expand(query)
            .Where(TrackTextNormalizer.HasPersian)
            .Take(2)
            .ToList();
        if (persianQueries.Count == 0) persianQueries.Add(query);

        var mp3s = new List<string>();
        foreach (var q in persianQueries)
        {
            ct.ThrowIfCancellationRequested();
            string html;
            try
            {
                using var resp = await _http.GetAsync("https://aimusicall.ir/search/" + Uri.EscapeDataString(q), ct)
                    .ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) continue;
                html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug("aimusicall search failed: {Msg}", ex.Message);
                continue;
            }
            mp3s.AddRange(Mp3Regex.Matches(html).Select(m => m.Value));
        }

        // The CDN has 404'd every file we've seen (stale posts keep their dead
        // links). Probe each candidate before surfacing it — a ranged GET that
        // returns anything but 200/206 means the file is gone.
        var alive = new List<string>();
        foreach (var mp3Url in mp3s.Distinct().Take(maxResults))
        {
            ct.ThrowIfCancellationRequested();
            if (await IsAliveAsync(mp3Url, ct).ConfigureAwait(false)) alive.Add(mp3Url);
        }

        foreach (var mp3Url in alive)
        {
            ct.ThrowIfCancellationRequested();
            var (fileArtist, fileTitle) = ParseFileName(mp3Url);
            yield return new SearchResult
            {
                Provider = ProviderId.PersianSites,
                Id = mp3Url,
                Metadata = new TrackMetadata
                {
                    Title = fileTitle,
                    Artist = fileArtist.Length > 0 ? fileArtist : query,
                },
                DirectStreamUri = new Uri(mp3Url),
                MaxQuality = mp3Url.Contains("320") ? StreamQuality.Maximum256K : StreamQuality.Standard128K,
                SourceUrl = mp3Url,
                Downloadable = true,
            };
        }
    }

    /// <summary>Range-probe the CDN file. dl.aimusicall.ir serves a self-signed
    /// cert and 404s files the site still links — only real files should surface.</summary>
    private async Task<bool> IsAliveAsync(string mp3Url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, mp3Url);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1023);
            using var resp = await _dlHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            return resp.StatusCode is System.Net.HttpStatusCode.OK or System.Net.HttpStatusCode.PartialContent;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("aimusicall probe failed for {Url}: {Msg}", mp3Url, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// CDN filenames carry "artist - title [320]" — parse into (artist, title)
    /// so rows display cleanly and group with their catalog counterparts.
    /// </summary>
    private static (string Artist, string Title) ParseFileName(string mp3Url)
    {
        var name = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(mp3Url));
        name = System.Text.RegularExpressions.Regex.Replace(name, @"[\[\(]\s*\d{3}\s*[\]\)]", "").Trim();
        var dash = name.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0 && dash < name.Length - 3)
            return (name[..dash].Trim(), name[(dash + 3)..].Trim());
        return ("", name);
    }

    public async Task<DownloadResult> DownloadAsync(
        SearchResult track, DownloadOptions options,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var url = track.DirectStreamUri?.OriginalString
            ?? throw new InvalidOperationException("This result has no direct stream URL.");
        var finalPath = Path.Combine(options.OutputDirectory, FileNaming.Build(options.TagTemplate, track, ".mp3", options.FilenameTemplate));
        await HttpDownloader.DownloadToFileAsync(_dlHttp, url, finalPath, progress, ct).ConfigureAwait(false);
        return new DownloadResult(finalPath, track.MaxQuality, ProviderId.PersianSites);
    }
}
