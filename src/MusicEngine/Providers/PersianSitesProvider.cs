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
/// after the user clicks Download. Two strategies, one provider:
///
///  1. aimusicall.ir — WordPress search whose SERP embeds direct
///     dl.aimusicall.ir/…/[320].mp3 links (Persian queries only, so the query is
///     Finglish-expanded first).
///  2. music-fa.com / upmusics.com — artist/tag pages embedding &lt;audio src&gt;
///     players.
///
/// These hosts intermittently sit behind Cloudflare; when they yield nothing the
/// resolver falls through to YouTube/yt-dlp.
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
                using var resp = await _http.GetAsync("https://aimusicall.ir/?s=" + Uri.EscapeDataString(q), ct)
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

        var emitted = 0;
        foreach (var mp3Url in mp3s.Distinct().Take(maxResults))
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
            emitted++;
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
