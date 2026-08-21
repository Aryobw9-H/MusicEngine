namespace MusicEngine.Providers;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Downloads;
using Http;
using Microsoft.Extensions.Logging;
using Models;

/// <summary>
/// Aparat (aparat.com) — Iran's largest video platform, fully domestic, hosts
/// enormous amounts of Persian music (official videos, lyric videos, full albums
/// uploaded as playlists). Both APIs are plain JSON, no auth, no Cloudflare.
/// Search: GET https://www.aparat.com/api/fa/v1/video/video/search?text={query}
/// File: GET https://www.aparat.com/api/fa/v1/video/video/show/videohash/{uid}
/// CDN mirrors: persian8, persian9, persian14, persian1, persian2, as1, as2,
/// arvan1, arvan2, m1, m2, caspian1, caspian2, caspian12, caspian20
///
/// Downloads are VIDEO files (MP4) — ffmpeg extracts audio to MP3.
/// </summary>
public sealed class AparatProvider : ISearchProvider, IDownloadProvider
{
    private const string SearchApi = "https://www.aparat.com/api/fa/v1/video/video/search";
    private const string FileApi = "https://www.aparat.com/api/fa/v1/video/video/show/videohash";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";

    private readonly HttpClient _http;
    private readonly ILogger<AparatProvider> _logger;
    private readonly string? _ffmpegPath;

    public ProviderId Id => ProviderId.Aparat;
    public string DisplayName => "Aparat";
    public SearchTier Tier => SearchTier.DownloadOnly;
    public bool IsAvailable => true;

    public AparatProvider(SharedHttpClient http, Configuration.ISettings? config = null, ILogger<AparatProvider>? logger = null)
    {
        // insecureTls: CDN video hosts may serve self-signed certs (BUG-13 family).
        _http = http.Create("Aparat", insecureTls: true);
        _http.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AparatProvider>.Instance;
        try { _ffmpegPath = ResolveFfmpeg(config?.FfmpegPath); }
        catch
        {
            _logger.LogDebug("ffmpeg not found — Aparat audio extraction disabled.");
            _ffmpegPath = null;
        }
    }

    public bool CanDownload(SearchResult result) =>
        result.Provider == ProviderId.Aparat
        || (result.SourceUrl?.Contains("aparat.com", StringComparison.OrdinalIgnoreCase) == true);

    // ---------- search ----------

    public async IAsyncEnumerable<SearchResult> SearchAsync(
        string query, int maxResults = 10,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2 || query.Length > 512)
            yield break;

        var url = $"{SearchApi}?text={Uri.EscapeDataString(query)}";
        var json = await GetStringAsync(url, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(json)) yield break;

        AparatSearchResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<AparatSearchResponse>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Aparat JSON parse error: {Msg}", ex.Message);
            yield break;
        }

        if (response?.Data is null) yield break;

        var count = 0;
        foreach (var item in response.Data)
        {
            if (count >= maxResults) yield break;
            ct.ThrowIfCancellationRequested();

            var videoRefs = item.Relationships?.Video?.Data;
            if (videoRefs is null || videoRefs.Count == 0) continue;

            var videoRef = videoRefs[0];
            // Find the full video object in included[]
            var fullVideo = response.Included?.FirstOrDefault(i =>
                i.Type == "Video" && i.Id == videoRef.Id);
            if (fullVideo is null) continue;

            var uid = fullVideo.Attributes?.Uid;
            if (string.IsNullOrEmpty(uid)) continue;

            var title = fullVideo.Attributes?.Title ?? "";
            var username = fullVideo.Attributes?.Username ?? "";
            var duration = fullVideo.Attributes?.Duration ?? 0;

            // Get the file URL for this video
            var fileUrl = await GetFileUrlAsync(uid, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(fileUrl)) continue;

            yield return new SearchResult
            {
                Provider = ProviderId.Aparat,
                Id = uid,
                Metadata = new TrackMetadata
                {
                    Title = title,
                    Artist = username,
                    Duration = duration > 0 ? TimeSpan.FromSeconds(duration) : null,
                },
                DirectStreamUri = new Uri(fileUrl),
                MaxQuality = StreamQuality.Maximum256K,
                SourceUrl = $"https://www.aparat.com/v/{uid}",
                Downloadable = true,
            };
            count++;
        }
    }

    // ---------- download ----------

    public async Task<DownloadResult> DownloadAsync(
        SearchResult track, DownloadOptions options,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        // 1. Resolve the CDN URL
        progress?.Report(new DownloadProgress(DownloadPhase.Resolving, 0, null, "Aparat: resolving video URL"));

        var videoUrl = track.DirectStreamUri?.OriginalString;
        if (string.IsNullOrEmpty(videoUrl) && track.Id is { Length: > 0 } uid)
        {
            videoUrl = await GetFileUrlAsync(uid, ct).ConfigureAwait(false);
        }
        if (string.IsNullOrEmpty(videoUrl) && track.SourceUrl is { Length: > 0 })
        {
            // Extract uid from https://www.aparat.com/v/{uid}
            var path = new Uri(track.SourceUrl).AbsolutePath;
            var videoUid = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (!string.IsNullOrEmpty(videoUid))
                videoUrl = await GetFileUrlAsync(videoUid, ct).ConfigureAwait(false);
        }
        if (string.IsNullOrEmpty(videoUrl))
            throw new InvalidOperationException("Aparat: could not resolve video download URL.");

        // 2. Download the MP4
        var mp3Path = FileNaming.Build(
            track.Metadata ?? new TrackMetadata { Title = track.Metadata?.Title ?? "Unknown", Artist = track.Metadata?.Artist ?? "" },
            track, ".mp3", options.FilenameTemplate);
        var finalPath = Path.Combine(options.OutputDirectory, mp3Path);

        if (_ffmpegPath is not null)
        {
            // Download to temp, then convert with ffmpeg
            var tempDir = Path.Combine(options.OutputDirectory, ".aparat-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tempDir);
            var tempMp4 = Path.Combine(tempDir, "video.mp4");

            try
            {
                progress?.Report(new DownloadProgress(DownloadPhase.Downloading, 0, null, "Aparat: downloading video"));
                await HttpDownloader.DownloadToFileAsync(_http, videoUrl, tempMp4, progress, ct).ConfigureAwait(false);

                // 3. Extract audio with ffmpeg
                progress?.Report(new DownloadProgress(DownloadPhase.Tagging, 0, null, "Aparat: extracting audio"));
                await ExtractAudioAsync(tempMp4, finalPath, options.MaxBitrateKbps, ct).ConfigureAwait(false);

                return new DownloadResult(finalPath, StreamQuality.Maximum256K, ProviderId.Aparat);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
            }
        }
        else
        {
            // No ffmpeg — download as-is (video file, but at least it works)
            progress?.Report(new DownloadProgress(DownloadPhase.Downloading, 0, null, "Aparat: downloading (no ffmpeg, video as-is)"));
            var tempPath = finalPath + ".mp4";
            await HttpDownloader.DownloadToFileAsync(_http, videoUrl, tempPath, progress, ct).ConfigureAwait(false);
            File.Move(tempPath, finalPath, overwrite: true);
            return new DownloadResult(finalPath, StreamQuality.Maximum256K, ProviderId.Aparat);
        }
    }

    // ---------- ffmpeg audio extraction ----------

    private async Task ExtractAudioAsync(string inputPath, string outputPath, int bitrateKbps, CancellationToken ct)
    {
        if (_ffmpegPath is null)
            throw new InvalidOperationException("ffmpeg not found — cannot extract audio from Aparat video.");

        var args = $"-i \"{inputPath}\" -vn -ab {bitrateKbps}k -ar 44100 -y \"{outputPath}\"";
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath,
            Arguments = args,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var tcs = new TaskCompletionSource<int>();
        process.Exited += (_, _) => tcs.TrySetResult(process.ExitCode);

        if (!process.Start())
            throw new InvalidOperationException("Failed to start ffmpeg process.");

        using var cancelReg = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
        });

        var stderr = new StringBuilder();
        var readTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                stderr.AppendLine(line);
        }, ct);

        var exitCode = await tcs.Task.ConfigureAwait(false);
        await readTask.ConfigureAwait(false);

        if (exitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed (exit {exitCode}): {(stderr.Length > 300 ? stderr.ToString(0, Math.Min(300, stderr.Length)) : stderr)}");
    }

    // ---------- HTTP helpers ----------

    private async Task<string?> GetFileUrlAsync(string uid, CancellationToken ct)
    {
        var url = $"{FileApi}/{uid}";
        var json = await GetStringAsync(url, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(json)) return null;

        AparatFileResponse? response;
        try
        {
            response = JsonSerializer.Deserialize<AparatFileResponse>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Aparat file JSON parse error: {Msg}", ex.Message);
            return null;
        }

        // Pick the lowest acceptable profile (360p) to minimize bandwidth
        var fileLinks = response?.Data?.Attributes?.FileLinkAll;
        if (fileLinks is null || fileLinks.Count == 0) return null;

        // Prefer 360p, fallback to 240p, then any available
        var preferred = fileLinks.FirstOrDefault(f => f.Profile == "360p")
                     ?? fileLinks.FirstOrDefault(f => f.Profile == "240p")
                     ?? fileLinks.FirstOrDefault();

        return preferred?.Urls?.FirstOrDefault();
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
            _logger.LogDebug("Aparat HTTP failure for {Url}: {Msg}", url, ex.Message);
            return "";
        }
    }

    private static string? ResolveFfmpeg(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath)) return explicitPath;
        var local = Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg.exe");
        if (File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // Search response models
    private sealed class AparatSearchResponse
    {
        [JsonPropertyName("data")]
        public List<AparatSearchData>? Data { get; set; }

        [JsonPropertyName("included")]
        public List<AparatIncludedVideo>? Included { get; set; }
    }

    private sealed class AparatSearchData
    {
        [JsonPropertyName("relationships")]
        public AparatRelationships? Relationships { get; set; }
    }

    private sealed class AparatRelationships
    {
        [JsonPropertyName("video")]
        public AparatVideoRel? Video { get; set; }
    }

    private sealed class AparatVideoRel
    {
        [JsonPropertyName("data")]
        public List<AparatVideoRef>? Data { get; set; }
    }

    private sealed class AparatVideoRef
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
    }

    private sealed class AparatIncludedVideo
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("attributes")]
        public AparatVideoAttributes? Attributes { get; set; }
    }

    private sealed class AparatVideoAttributes
    {
        [JsonPropertyName("uid")]
        public string? Uid { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("username")]
        public string? Username { get; set; }

        [JsonPropertyName("duration")]
        public int Duration { get; set; }
    }

    // File response models
    private sealed class AparatFileResponse
    {
        [JsonPropertyName("data")]
        public AparatFileData? Data { get; set; }
    }

    private sealed class AparatFileData
    {
        [JsonPropertyName("attributes")]
        public AparatFileAttributes? Attributes { get; set; }
    }

    private sealed class AparatFileAttributes
    {
        [JsonPropertyName("file_link_all")]
        public List<AparatFileLink>? FileLinkAll { get; set; }
    }

    private sealed class AparatFileLink
    {
        [JsonPropertyName("profile")]
        public string? Profile { get; set; }

        [JsonPropertyName("urls")]
        public List<string>? Urls { get; set; }
    }
}
