namespace MusicEngine.Providers;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Configuration;
using Downloads;
using Microsoft.Extensions.Logging;
using Models;

/// <summary>
/// Persian index ultra-downloader — download tier. Spawns the bundled
/// <c>Tools/persian_fetch.py</c> (curl_cffi browser-TLS impersonation) to search
/// music-fa / upmusics / taksong and to stream their CDN MP3s. The proxy comes
/// from app settings and is passed to the script as an argument.
/// Auto-disabled when Python or curl_cffi is missing.
/// </summary>
public sealed class PersianIndexProvider : ISearchProvider, IDownloadProvider
{
    private readonly ILogger<PersianIndexProvider> _logger;
    private readonly string _pythonPath;
    private readonly string _scriptPath;
    private readonly string? _proxyUrl;
    private readonly Lazy<Task<bool>> _availability;

    public ProviderId Id => ProviderId.PersianIndex;
    public string DisplayName => "Persian Index";
    public SearchTier Tier => SearchTier.DownloadOnly;

    /// <summary>
    /// Non-blocking: false until the lazy python probe has actually completed
    /// (BUG-05). Callers that need the verdict may await <see cref="EnsureAvailableAsync"/>.
    /// </summary>
    public bool IsAvailable => _availability.Value is { IsCompletedSuccessfully: true, Result: true };

    public PersianIndexProvider(Configuration.ISettings config, ILogger<PersianIndexProvider>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PersianIndexProvider>.Instance;
        _pythonPath = config.PythonPath ?? "python";
        _proxyUrl = config.ProxyUrl;
        _scriptPath = Path.Combine(AppContext.BaseDirectory, "Tools", "persian_fetch.py");
        var enabled = config.EnablePersianIndex && File.Exists(_scriptPath);
        if (!enabled && config.EnablePersianIndex)
            _logger.LogInformation("Persian Index disabled (script={Script} python={Python} available=false)",
                _scriptPath, _pythonPath);
        _availability = new Lazy<Task<bool>>(() => enabled ? Task.Run(ProbePythonAsync) : Task.FromResult(false));
    }

    /// <summary>Await the lazily-evaluated python/curl_cffi probe.</summary>
    public Task<bool> EnsureAvailableAsync() => _availability.Value;

    private async Task<bool> ProbePythonAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _pythonPath,
                Arguments = "-c \"import curl_cffi\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already exited */ }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public bool CanDownload(SearchResult result) => result.Provider == ProviderId.PersianIndex;

    public async IAsyncEnumerable<SearchResult> SearchAsync(
        string query, int maxResults = 10,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // All index sites in parallel, results yielded per site AS THEY LAND —
        // a slow site (music-fa needs the proxy) must not delay the fast ones,
        // and the caller's timeout keeps whatever already arrived.
        var tasks = new[] { "musicfa", "musicsfa", "upmusics" }
            .Select(async site =>
            {
                try
                {
                    var json = await RunPyAsync(ct, "search", site, query, maxResults.ToString()).ConfigureAwait(false);
                    return JsonSerializer.Deserialize<SearchList>(json, JsonOpts)?.Posts ?? new List<PostItem>();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Persian index {Site} search failed: {Msg}", site, ex.Message);
                    return new List<PostItem>();
                }
            })
            .ToList();

        var emitted = 0;
        while (tasks.Count > 0 && emitted < maxResults)
        {
            var done = await Task.WhenAny(tasks).ConfigureAwait(false);
            tasks.Remove(done);
            foreach (var p in await done.ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                if (emitted >= maxResults) break;
                emitted++;
                yield return new SearchResult
                {
                    Provider = ProviderId.PersianIndex,
                    Id = p.Url,
                    Metadata = new TrackMetadata { Title = CleanTitle(p.Title) },
                    SourceUrl = p.Url,
                    MaxQuality = StreamQuality.High192K,
                    Downloadable = true,
                };
            }
        }
    }

    public async Task<DownloadResult> DownloadAsync(
        SearchResult track, DownloadOptions options,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(new DownloadProgress(DownloadPhase.Resolving, 0, null, "Persian index: opening post page"));
        LinkList? links;
        try
        {
            var json = await RunPyAsync(ct, "links", track.SourceUrl).ConfigureAwait(false);
            links = JsonSerializer.Deserialize<LinkList>(json, JsonOpts);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Post page failed: {ex.Message}");
        }
        if (links?.Mp3s is not { Count: > 0 })
            throw new InvalidOperationException("No MP3 links found on post page.");

        var pick = links.Mp3s.FirstOrDefault(m => m.Quality == "320")
                   ?? links.Mp3s.FirstOrDefault(m => m.Quality == "128")
                   ?? links.Mp3s[0];

        var finalPath = Path.Combine(options.OutputDirectory,
            FileNaming.Build(new TrackMetadata { Title = CleanTitle(links.Title ?? track.Metadata.Title), Artist = track.Metadata.Artist }, track, ".mp3", options.FilenameTemplate));

        progress?.Report(new DownloadProgress(DownloadPhase.Downloading, 0, null, $"Downloading {pick.Quality}kbps…"));
        var tempPath = finalPath + ".part";
        await RunPyAsync(ct, "dl", pick.Url, tempPath, progress: progress).ConfigureAwait(false);
        if (!File.Exists(tempPath))
            throw new InvalidOperationException("Download produced no file.");
        File.Move(tempPath, finalPath, overwrite: true);

        return new DownloadResult(finalPath,
            pick.Quality == "320" ? StreamQuality.High192K : StreamQuality.Standard128K,
            ProviderId.PersianIndex);
    }

    private async Task<string> RunPyAsync(CancellationToken ct, string mode, string a, string? b = null, string? c = null,
        IProgress<DownloadProgress>? progress = null)
    {
        var args = new List<string> { _scriptPath, "--proxy", _proxyUrl ?? "-", mode, a };
        if (b is not null) args.Add(b);
        if (c is not null) args.Add(c);

        var psi = new ProcessStartInfo
        {
            FileName = _pythonPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var proc = new Process { StartInfo = psi };
                proc.Start();
                using var reg = ct.Register(() => { try { proc.Kill(true); } catch { } });

                var stderrTask = proc.StandardError.ReadToEndAsync(ct);
                var lines = new List<string>();
        while (await proc.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            // "PROGRESS <pct> <done> <total>" lines stream download progress; the
            // final JSON payload is the last non-progress line.
            if (line.StartsWith("PROGRESS ", StringComparison.Ordinal) && progress is not null)
            {
                var parts = line.Split(' ');
                if (parts.Length == 4 && long.TryParse(parts[2], out var done) && long.TryParse(parts[3], out var total))
                    progress.Report(new DownloadProgress(DownloadPhase.Downloading, done, total > 0 ? total : null));
            }
            else if (line.Length > 0)
            {
                lines.Add(line);
            }
        }
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        // Await, never .Result — a redirected pipe can still hold buffered data
        // after WaitForExit, and .Result blocks a pool thread and wraps failures
        // in AggregateException (BUG-11).
        var stderr = await stderrTask.ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(stderr))
            _logger.LogDebug("persian_fetch stderr: {Err}", stderr[..Math.Min(stderr.Length, 300)]);
        return string.Join('\n', lines);
    }

    private static string CleanTitle(string t)
    {
        var cut = t.Split('|', '+')[0].Trim();
        cut = cut.Replace("دانلود آهنگ", "").Replace("دانلود", "")
                 .Replace("با کیفیت عالی", "").Replace("کیفیت عالی", "").Trim();
        return cut.Length > 0 ? cut : t;
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed record SearchList(List<PostItem>? Posts);
    private sealed record PostItem(
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("title")] string Title);
    private sealed record LinkList(List<Mp3Item>? Mp3s, string? Title, int Count);
    private sealed record Mp3Item(
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("quality")] string Quality);
}
