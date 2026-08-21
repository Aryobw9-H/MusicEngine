namespace MusicEngine.Providers;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Configuration;
using Downloads;
using Microsoft.Extensions.Logging;
using Models;

/// <summary>
/// Universal downloader driven by yt-dlp — the safety net that makes EVERY track
/// downloadable: YouTube, SoundCloud, Bandcamp, Vimeo, … or any "artist - title"
/// via a ytsearch query. Converts to MP3 via ffmpeg and embeds thumbnail +
/// metadata. Also implements search (used only as a last-resort fallback by the
/// pipeline; the in-process YouTube provider is faster for display).
///
/// Verified flags for the 2026 YouTube landscape:
///   -f "140/bestaudio[ext=m4a]/bestaudio" — m4a/140 works; opus 251 often 403s
///   --remote-components ejs:github — YouTube's JS challenge solver (required since mid-2026)
/// </summary>
public sealed class YtDlpProvider : IDownloadProvider, ISearchProvider
{
    private const string RemoteComponents = "ejs:github";

    private static readonly Regex ProgressRegex = new(
        @"\[download\]\s+([\d.]+)%", RegexOptions.Compiled);

    private readonly ILogger<YtDlpProvider> _logger;
    private readonly string? _ytDlpPath;
    private readonly string? _ffmpegPath;
    private readonly string? _proxyUrl;
    private readonly string? _cookiesBrowser;
    private readonly string? _cookiesFile;

    public ProviderId Id => ProviderId.YtDlp;
    public string DisplayName => "yt-dlp";
    public SearchTier Tier => SearchTier.DownloadOnly;
    public bool IsAvailable => _ytDlpPath is not null;

    public YtDlpProvider(Configuration.ISettings config, ILogger<YtDlpProvider>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<YtDlpProvider>.Instance;
        _proxyUrl = config.ProxyUrl;
        _cookiesBrowser = config.CookiesBrowser;
        _cookiesFile = config.CookiesFile;
        try { _ytDlpPath = ResolveBinary(config.YtDlpPath ?? Environment.GetEnvironmentVariable("YTDLP_PATH"), "yt-dlp.exe"); }
        catch (FileNotFoundException ex)
        {
            _logger.LogWarning("yt-dlp not found ({Msg}) — YouTube downloads disabled.", ex.Message);
            _ytDlpPath = null;
        }
        try { _ffmpegPath = ResolveBinary(config.FfmpegPath, "ffmpeg.exe"); }
        catch (FileNotFoundException)
        {
            _logger.LogWarning("ffmpeg not found — MP3 conversion disabled.");
            _ffmpegPath = null;
        }
    }

    public bool CanDownload(SearchResult result) => _ytDlpPath is not null;

    // ---------- search (fallback only) ----------

    public async IAsyncEnumerable<SearchResult> SearchAsync(
        string query, int maxResults = 10,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_ytDlpPath is null) yield break;

        var stdout = new StringBuilder();
        var cmd = CliWrap.Cli.Wrap(_ytDlpPath)
            .WithArguments(b =>
            {
                b.Add($"ytsearch{maxResults}:{query}")
                 .Add("-J").Add("--flat-playlist").Add("--no-warnings").Add("--quiet");
                if (_proxyUrl is not null) b.Add("--proxy").Add(_proxyUrl);
                if (_ffmpegPath is not null) b.Add("--ffmpeg-location").Add(_ffmpegPath);
            })
            .WithStandardOutputPipe(CliWrap.PipeTarget.ToStringBuilder(stdout))
            .WithStandardErrorPipe(CliWrap.PipeTarget.Null)
            .WithValidation(CliWrap.CommandResultValidation.None);

        var exit = await cmd.ExecuteAsync(ct).ConfigureAwait(false);
        var text = stdout.ToString();
        if (exit.ExitCode != 0 || string.IsNullOrWhiteSpace(text)) yield break;

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (!(root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array))
            yield break;

        foreach (var en in entries.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            if (en.ValueKind != JsonValueKind.Object) continue;
            var id = GetStr(en, "id") ?? "";
            var title = GetStr(en, "title") ?? "Untitled";
            var uploader = GetStr(en, "uploader") ?? GetStr(en, "channel") ?? "";
            var duration = GetDouble(en, "duration");
            yield return new SearchResult
            {
                Provider = ProviderId.YouTube,
                Id = id,
                Metadata = new TrackMetadata
                {
                    Title = title,
                    Artist = uploader,
                    Duration = duration is > 0 ? TimeSpan.FromSeconds(duration.Value) : null,
                    ArtworkUri = GetStr(en, "thumbnail") is { Length: > 0 } t && Uri.TryCreate(t, UriKind.Absolute, out var u) ? u : null,
                },
                MaxQuality = StreamQuality.High192K,
                SourceUrl = id.Length > 0 ? $"https://www.youtube.com/watch?v={id}" : GetStr(en, "webpage_url") ?? "",
                Downloadable = true,
            };
        }
    }

    // ---------- download ----------

    public async Task<DownloadResult> DownloadAsync(
        SearchResult track, DownloadOptions options,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        if (_ytDlpPath is null)
            throw new InvalidOperationException("yt-dlp binary not available.");

        const int maxAttempts = 3;
        Exception? last = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await DownloadOnceAsync(track, options, progress, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (IsBotCheck(ex))
            {
                // IP-level block: retrying burns ~10s and always fails the same
                // way. Fail fast and tell the user the one thing that fixes it.
                throw new InvalidOperationException(
                    "YouTube blocked yt-dlp (bot check). Set \"cookiesBrowser\": \"chrome\" " +
                    "(or \"edge\"/\"firefox\") — or export a cookies.txt and set \"cookiesFile\" — " +
                    "in appsettings.json next to the app.");
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransient(ex))
            {
                last = ex;
                _logger.LogWarning("yt-dlp attempt {Attempt}/{Max} transient failure ({Msg}); retrying",
                    attempt, maxAttempts, ex.Message);
                progress?.Report(new DownloadProgress(DownloadPhase.Resolving, 0, null,
                    $"Transient error — retry {attempt}/{maxAttempts - 1}…"));
                await Task.Delay(TimeSpan.FromSeconds(3 * attempt), ct).ConfigureAwait(false);
            }
        }
        throw last ?? new InvalidOperationException("yt-dlp download failed.");
    }

    /// <summary>YouTube's "Sign in to confirm you're not a bot" — a hard IP-level
    /// block that retries cannot fix (only cookies can).</summary>
    private static bool IsBotCheck(Exception ex) =>
        ex.Message.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("not a bot", StringComparison.OrdinalIgnoreCase);

    private static bool IsTransient(Exception ex) => !IsBotCheck(ex)
        && (ex.Message.Contains("429", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("403", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("Requested format is not available", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("Read timed out", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Download target: the track's URL when yt-dlp understands the host, else a
    /// ytsearch1: query built from artist - title so ANY track is fetchable.
    /// </summary>
    private static string BuildTarget(SearchResult track)
    {
        var url = track.SourceUrl?.Trim();
        if (!string.IsNullOrEmpty(url)
            && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && IsYtDlpHost(uri.Host))
        {
            return url;
        }
        var artist = track.Metadata.Artist;
        var title = track.Metadata.Title;
        var q = string.IsNullOrWhiteSpace(artist) ? title ?? "music" : $"{artist} - {title}";
        return $"ytsearch1:{q}";
    }

    private static bool IsYtDlpHost(string host)
    {
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host[4..];
        return host is "youtube.com" or "youtu.be" or "music.youtube.com" or "m.youtube.com"
            or "soundcloud.com" or "m.soundcloud.com" or "deezer.com" or "bandcamp.com"
            or "vimeo.com" or "twitter.com" or "x.com" or "twitch.tv" or "vk.com";
    }

    private async Task<DownloadResult> DownloadOnceAsync(
            SearchResult track, DownloadOptions options,
            IProgress<DownloadProgress>? progress, CancellationToken ct)
        {
            var finalName = FileNaming.Build(options.TagTemplate, track, _ffmpegPath is null ? "" : ".mp3", options.FilenameTemplate);
            var finalPath = Path.Combine(options.OutputDirectory, finalName);

            // Work dir under the OUTPUT directory (a real Windows path — never
            // Path.GetTempPath(), which inherits MSYS TMP quirks from git-bash launches).
            var workDir = Path.Combine(options.OutputDirectory, ".ytdlp-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(workDir);

            var template = Path.Combine(workDir, "%(title)s [%(id)s].%(ext)s");
            var stderr = new StringBuilder();

            progress?.Report(new DownloadProgress(DownloadPhase.Resolving, 0, null, "yt-dlp: resolving streams"));
        
            // Use Process directly for reliable cancellation (CliWrap doesn't kill child on cancel)
            var startInfo = new ProcessStartInfo
            {
                FileName = _ytDlpPath,
                Arguments = BuildYtDlpArgs(track, options, template),
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardErrorEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8,
            };
        
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var tcs = new TaskCompletionSource<Process>();
            process.Exited += (_, _) => tcs.TrySetResult(process);
        
            if (!process.Start())
                throw new InvalidOperationException("Failed to start yt-dlp process");
        
            // Register cancellation to kill the process
            using var cancelReg = ct.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
            });
        
            // Read stderr/stdout for progress
            var stderrTask = ReadStreamAsync(process.StandardError, stderr, progress, ct);
            var stdoutTask = ReadStreamAsync(process.StandardOutput, null, progress, ct);
        
            var exitedTask = tcs.Task;
            var completedTask = await Task.WhenAny(exitedTask, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
        
            if (completedTask != exitedTask)
            {
                // Cancellation fired — process.Kill was called via cancelReg
                try { if (!process.HasExited) process.Kill(true); } catch { }
                await exitedTask.ConfigureAwait(false); // wait for exit after kill
                ct.ThrowIfCancellationRequested();
            }
        
            await stderrTask.ConfigureAwait(false);
            await stdoutTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                try { Directory.Delete(workDir, recursive: true); } catch { }
                var err = stderr.ToString();
                throw new InvalidOperationException($"yt-dlp failed: {(err.Length > 400 ? err[..400] : err)}");
            }

            // Find the produced audio by globbing (robust against path quirks).
            var outputPath = Directory.GetFiles(workDir, "*.*")
                .FirstOrDefault(f => Path.GetExtension(f) is ".mp3" or ".m4a" or ".opus" or ".webm" or ".mkv");
            if (outputPath is null)
                throw new InvalidOperationException("yt-dlp finished but produced no audio file.");

            File.Move(outputPath, finalPath, overwrite: true);
            try { Directory.Delete(workDir, recursive: true); } catch { }

            var size = new FileInfo(finalPath).Length;
            progress?.Report(new DownloadProgress(DownloadPhase.Tagging, size, size, "Tagged by yt-dlp"));
            return new DownloadResult(finalPath, StreamQuality.High192K, ProviderId.YtDlp);
        }

        private string BuildYtDlpArgs(SearchResult track, DownloadOptions options, string template)
        {
            var args = new List<string>
            {
                BuildTarget(track),
                "-x",
                "--audio-format", "mp3",
                "--audio-quality", $"{options.MaxBitrateKbps}K",
                "-f", "140/bestaudio[ext=m4a]/bestaudio/best",
                "--newline", "--progress",
                "--no-playlist", "--no-warnings",
                "--remote-components", RemoteComponents,
                "--concurrent-fragments", "4",
                "--http-chunk-size", "10M",
                "--retries", "3", "--fragment-retries", "3",
                "--buffer-size", "16K",
                "-o", template,
            };
            if (_ffmpegPath is not null)
            {
                args.Add("--ffmpeg-location"); args.Add(_ffmpegPath);
                args.Add("--embed-thumbnail"); args.Add("--add-metadata");
            }
            if (_proxyUrl is not null) { args.Add("--proxy"); args.Add(_proxyUrl); }
            // A cookies.txt export wins over the browser DB (a file works even
            // while the browser is running, which locks the DB on Windows).
            if (_cookiesFile is { Length: > 0 } && File.Exists(_cookiesFile))
            {
                args.Add("--cookies"); args.Add(_cookiesFile);
            }
            else if (_cookiesBrowser is { Length: > 0 })
            {
                args.Add("--cookies-from-browser"); args.Add(_cookiesBrowser);
            }
            return string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        }

        private static async Task ReadStreamAsync(StreamReader reader, StringBuilder? collector, IProgress<DownloadProgress>? progress, CancellationToken ct)
        {
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (collector is not null) collector.AppendLine(line);
                if (progress is null) continue;
                var m = ProgressRegex.Match(line);
                if (m.Success && double.TryParse(m.Groups[1].Value, out var pct))
                {
                    progress.Report(new DownloadProgress(DownloadPhase.Downloading,
                        (long)(pct * 1000), 100_000, "Downloading (yt-dlp)"));
                }
                else if (line.Contains("[ExtractAudio]", StringComparison.Ordinal))
                {
                    progress.Report(new DownloadProgress(DownloadPhase.Tagging, 0, null, "Converting to MP3…"));
                }
            }
        }

    private static string? GetStr(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? GetDouble(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    private static string? ResolveBinary(string? explicitPath, string name)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath)) return explicitPath;
        var local = Path.Combine(AppContext.BaseDirectory, "Tools", name);
        if (File.Exists(local)) return local;
        // In-process PATH scan instead of shelling out to `where` (BUG-05):
        // faster and no subprocess during DI construction. Try both the exact
        // name ("yt-dlp.exe") and the bare name ("yt-dlp") for other OSes.
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(dir.Trim(), name),
                         Path.Combine(dir.Trim(), Path.GetFileNameWithoutExtension(name)),
                     })
            {
                if (File.Exists(candidate)) return candidate;
            }
        }
        throw new FileNotFoundException($"{name} not found. Place it in <app>/Tools/, add to PATH, or set it in appsettings.json.", name);
    }
}
