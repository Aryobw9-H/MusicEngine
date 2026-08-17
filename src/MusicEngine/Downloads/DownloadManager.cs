namespace MusicEngine.Downloads;

using System.Collections.Concurrent;
using System.Threading.Channels;
using Audio;
using Configuration;
using Microsoft.Extensions.Logging;
using Models;
using Search;

/// <summary>
/// Owns every download. The UI enqueues a TrackWork; the manager:
///   1. resolves the best real source (ranked versions first; when none exist,
///      queries the slow download-tier Persian providers once, ~15s budget;
///      finally synthesizes a yt-dlp ytsearch target),
///   2. downloads through a fallback chain (native provider → yt-dlp),
///   3. tags + embeds artwork when the source didn't.
///
/// Jobs run through a worker pool (default 2) — genuinely bounded concurrency —
/// and every job is cancellable and removed from the active map when done.
/// </summary>
public sealed class DownloadManager
{
    public sealed record EnqueuedJob(string Id, string Title, string Artist, TrackWork Work);

    private readonly IReadOnlyList<ISearchProvider> _searchProviders;
    private readonly IReadOnlyList<IDownloadProvider> _downloadProviders;
    private readonly AppConfig _config;
    private readonly TrackTagger _tagger;
    private readonly ILogger<DownloadManager> _logger;

    private readonly Channel<(EnqueuedJob Job, TaskCompletionSource<DownloadProgress> Completion, CancellationTokenSource Cts)> _queue;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();
    private readonly ConcurrentDictionary<string, EnqueuedJob> _jobs = new();
    private readonly ConcurrentDictionary<string, bool> _paused = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _workers = new();

    public event Action<EnqueuedJob>? JobAdded;
    public event Action<string, DownloadProgress>? JobProgress;
    /// <summary>Raised when a job begins transferring via a concrete provider ("Radio Javan", "yt-dlp"…).</summary>
    public event Action<string, string>? JobStarted;

    public DownloadManager(
        IEnumerable<ISearchProvider> searchProviders,
        IEnumerable<IDownloadProvider> downloadProviders,
        AppConfig config,
        TrackTagger tagger,
        ILogger<DownloadManager>? logger = null)
    {
        _searchProviders = searchProviders.Where(p => p.IsAvailable).ToArray();
        _downloadProviders = downloadProviders.Where(p => p.IsAvailable).ToArray();
        _config = config;
        _tagger = tagger;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DownloadManager>.Instance;
        _queue = Channel.CreateUnbounded<(EnqueuedJob, TaskCompletionSource<DownloadProgress>, CancellationTokenSource)>(
            new UnboundedChannelOptions { SingleReader = false });

        var workers = Math.Clamp(_config.MaxParallelDownloads, 1, 8);
        for (var i = 0; i < workers; i++)
            _workers.Add(Task.Run(() => WorkerLoopAsync(_shutdown.Token)));
    }

    /// <summary>Enqueue a work for download; progress flows through <see cref="JobProgress"/> events.</summary>
    public Task<DownloadProgress> EnqueueAsync(TrackWork work)
    {
        var job = new EnqueuedJob(
            Guid.NewGuid().ToString("N"),
            work.Title,
            work.Artist,
            work);

        _jobs[job.Id] = job;
        var tcs = new TaskCompletionSource<DownloadProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cts = new CancellationTokenSource();
        _active[job.Id] = cts;
        _queue.Writer.TryWrite((job, tcs, cts));
        JobAdded?.Invoke(job);
        return tcs.Task;
    }

    public bool Cancel(string jobId)
    {
        _jobs.TryRemove(jobId, out _);
        _paused.TryRemove(jobId, out _);
        if (_active.TryGetValue(jobId, out var cts))
            return CancelCore(cts);

        if (_paused.TryRemove(jobId, out _))
        {
            JobProgress?.Invoke(jobId, new DownloadProgress(DownloadPhase.Cancelled, Message: "Cancelled"));
            return true;
        }
        return false;
    }

    public bool Pause(string jobId)
    {
        if (_active.TryGetValue(jobId, out var cts))
        {
            _paused[jobId] = true;
            return CancelCore(cts);
        }
        return false;
    }

    public bool Resume(string jobId)
    {
        if (_jobs.TryGetValue(jobId, out var job) && !_active.ContainsKey(jobId))
        {
            _paused.TryRemove(jobId, out _);
            var tcs = new TaskCompletionSource<DownloadProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cts = new CancellationTokenSource();
            _active[jobId] = cts;
            _queue.Writer.TryWrite((job, tcs, cts));
            return true;
        }
        return false;
    }

    private static bool CancelCore(CancellationTokenSource cts)
    {
        try { cts.Cancel(); return true; }
        catch (ObjectDisposedException) { return false; }
    }

    private async Task WorkerLoopAsync(CancellationToken shutdown)
    {
        await foreach (var (job, tcs, cts) in _queue.Reader.ReadAllAsync(shutdown))
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(shutdown, cts.Token);
            try
            {
                var final = await RunJobAsync(job, tcs, linked.Token).ConfigureAwait(false);
                tcs.TrySetResult(final);
            }
            catch (OperationCanceledException)
            {
                if (_paused.TryRemove(job.Id, out _))
                {
                    tcs.TrySetResult(new DownloadProgress(DownloadPhase.Paused, Message: "Paused"));
                }
                else
                {
                    _jobs.TryRemove(job.Id, out _);
                    tcs.TrySetResult(new DownloadProgress(DownloadPhase.Cancelled, Message: "Cancelled"));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Download job failed: {Title} — {Msg}", job.Title, ex.Message);
                _jobs.TryRemove(job.Id, out _);
                tcs.TrySetResult(new DownloadProgress(DownloadPhase.Failed, Message: ex.Message));
            }
            finally
            {
                _active.TryRemove(job.Id, out _);
                cts.Dispose();
                if (!_paused.ContainsKey(job.Id) && !_active.ContainsKey(job.Id))
                    _jobs.TryRemove(job.Id, out _);
            }
        }
    }

    private async Task<DownloadProgress> RunJobAsync(
            EnqueuedJob job, TaskCompletionSource<DownloadProgress> tcs, CancellationToken ct)
    {
        var work = job.Work;
        var options = new DownloadOptions
        {
            OutputDirectory = _config.OutputDirectory,
            MaxBitrateKbps = _config.BitrateKbps,
            EmbedTags = true,
            TagTemplate = BuildTagTemplate(work),
            FilenameTemplate = _config.FilenameTemplate,
        };
        Directory.CreateDirectory(options.OutputDirectory);

        var progress = new ProgressProxy(job.Id, tcs, JobProgress);
        progress.Report(new DownloadProgress(DownloadPhase.Resolving, 0, null, "Resolving best source…"));

        // Already downloaded? Done instantly.
        var firstDownloadable = work.DownloadableVersions.FirstOrDefault();
        var existing = FileNaming.ExistingPath(options.OutputDirectory, options.TagTemplate!,
            firstDownloadable ?? work.Representative, _config.FilenameTemplate);
        if (existing is not null)
        {
            var p = new DownloadProgress(DownloadPhase.AlreadyOwned, 1, 1, "Already in your library", existing);
            return p;
        }

        var candidates = await ResolveCandidatesAsync(work, options.TagTemplate!, progress, ct).ConfigureAwait(false);
        foreach (var candidate in candidates)
        {
            if (ct.IsCancellationRequested)
                return new DownloadProgress(DownloadPhase.Cancelled, Message: "Cancelled");

            var chain = BuildChain(candidate);
            foreach (var provider in chain)
            {
                try
                {
                    progress.Report(new DownloadProgress(DownloadPhase.Resolving, 0, null,
                                            $"Via {provider.DisplayName}…"));
                    JobStarted?.Invoke(job.Id, provider.DisplayName);

                    using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var resolveTask = provider.DownloadAsync(candidate, options, progress, watchdogCts.Token);

                    var watchdog = Task.Run(async () =>
                    {
                        try
                        {
                            while (!resolveTask.IsCompleted)
                            {
                                await Task.Delay(5000, watchdogCts.Token);
                                if (progress.TimeSinceLastProgress > TimeSpan.FromSeconds(60))
                                {
                                    watchdogCts.Cancel();
                                    break;
                                }
                            }
                        }
                        catch (OperationCanceledException) { }
                        finally
                        {
                            watchdogCts.Dispose();
                        }
                    });

                    var resolveTimeout = Task.Delay(TimeSpan.FromSeconds(30), watchdogCts.Token);
                    var winner = await Task.WhenAny(resolveTask, resolveTimeout).ConfigureAwait(false);
                    if (winner == resolveTimeout && progress.LastPhase == DownloadPhase.Resolving)
                    {
                        watchdogCts.Cancel();
                        progress.Report(new DownloadProgress(DownloadPhase.Resolving, 0, null,
                            $"{provider.DisplayName} timed out — trying fallback…"));
                        continue;
                    }

                    DownloadResult result;
                    try
                    {
                        result = await resolveTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (watchdogCts.IsCancellationRequested && !ct.IsCancellationRequested)
                    {
                        throw new TimeoutException("Download stalled for 60 seconds.");
                    }

                    if (options.EmbedTags && provider.Id != ProviderId.YtDlp)
                    {
                        progress.Report(new DownloadProgress(DownloadPhase.Tagging, 0, null, "Writing tags…"));
                        _tagger.Tag(result.FilePath, options.TagTemplate ?? candidate.Metadata);
                    }

                    var done = new DownloadProgress(DownloadPhase.Completed, 1, 1,
                        $"Saved • {provider.DisplayName}", result.FilePath);
                    return done;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogInformation("Download via {Provider} failed ({Msg}); trying next",
                        provider.DisplayName, ex.Message);
                    progress.Report(new DownloadProgress(DownloadPhase.Resolving, 0, null,
                        $"{provider.DisplayName} failed — trying fallback…"));
                }
            }
        }

        throw new InvalidOperationException("No source could download this track.");
    }

    /// <summary>Catalog metadata wins: it is the cleanest identity for tags + filenames.</summary>
    private static TrackMetadata? BuildTagTemplate(TrackWork work)
    {
        var rep = work.Representative.Metadata;
        var hasAny = !string.IsNullOrWhiteSpace(work.Artist) || !string.IsNullOrWhiteSpace(work.Title);
        if (!hasAny) return null;
        return new TrackMetadata
        {
            Title = string.IsNullOrWhiteSpace(work.Title) ? rep.Title : work.Title,
            Artist = string.IsNullOrWhiteSpace(work.Artist) ? rep.Artist : work.Artist,
            Album = rep.Album,
            Duration = work.Goal.Duration ?? rep.Duration,
            ArtworkUri = rep.ArtworkUri,
            ReleaseDate = rep.ReleaseDate,
            Genre = rep.Genre,
        };
    }

    private async Task<IEnumerable<SearchResult>> ResolveCandidatesAsync(
        TrackWork work, TrackMetadata tagTemplate, ProgressProxy progress, CancellationToken ct)
    {
        // 1. Known downloadable versions, ranked: Persian direct MP3s beat YouTube
        //    (real 320k files), duration closeness breaks ties.
        var ranked = work.DownloadableVersions
            .OrderByDescending(r => DownloadRank(r.Provider))
            .ThenBy(r => work.Goal.Duration is { } g && r.Metadata.Duration is { } d
                ? Math.Abs(d.TotalSeconds - g.TotalSeconds)
                : double.MaxValue)
            .ToList();
        if (ranked.Count > 0) return ranked;

        // 2. No versions (catalog-only row): one targeted pass over the slow
        //    download-tier Persian providers.
        var term = string.Join(" ", new[] { tagTemplate.Artist, tagTemplate.Title }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (term.Length > 0)
        {
            progress.Report(new DownloadProgress(DownloadPhase.Resolving, 0, null, "Searching Iranian sources…"));
            var slowTier = _searchProviders.Where(p => p.Tier == SearchTier.DownloadOnly).ToList();
            var found = new List<SearchResult>();
            var searchTask = Task.Run(async () =>
            {
                foreach (var p in slowTier)
                {
                    try
                    {
                        await foreach (var r in p.SearchAsync(term, 5, ct).ConfigureAwait(false))
                        {
                            if (SearchService.PassesGoalGate(r, work.Goal) || SearchService.PassesLooseGate(r, work.Goal))
                                found.Add(r);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug("Slow-tier provider {Provider} failed: {Msg}", p.Id, ex.Message);
                    }
                }
            }, ct);

            var budget = Task.WhenAny(searchTask, Task.Delay(TimeSpan.FromSeconds(15), ct));
            await (await budget.ConfigureAwait(false)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            if (found.Count > 0)
            {
                return found
                    .OrderByDescending(r => DownloadRank(r.Provider))
                    .ThenByDescending(r => r.MaxQuality);
            }
        }

        // 3. Guaranteed fallback: synthesize a yt-dlp ytsearch target.
        var ytDlp = _downloadProviders.FirstOrDefault(p => p.Id == ProviderId.YtDlp);
        if (ytDlp is null)
            throw new InvalidOperationException("No download source available (yt-dlp missing?).");
        var q = string.Join(" - ", new[] { tagTemplate.Artist, tagTemplate.Title }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return new[]
        {
            new SearchResult
            {
                Provider = ProviderId.YtDlp,
                Id = "ytsearch:" + q,
                Metadata = new TrackMetadata { Title = tagTemplate.Title, Artist = tagTemplate.Artist },
                SourceUrl = "ytsearch1:" + q,
                MaxQuality = StreamQuality.High192K,
                Downloadable = true,
            },
        };
    }

    /// <summary>Native provider first, universal yt-dlp second.</summary>
    private IEnumerable<IDownloadProvider> BuildChain(SearchResult target)
    {
        var native = _downloadProviders.FirstOrDefault(p => p.CanDownload(target));
        if (native is not null) yield return native;
        var universal = _downloadProviders.FirstOrDefault(p => p.Id == ProviderId.YtDlp && p != native);
        if (universal is not null) yield return universal;
    }

    /// <summary>Persian direct MP3 > YouTube > yt-dlp synth > SoundCloud > Radio Javan.</summary>
    private static int DownloadRank(ProviderId p) => p switch
    {
        ProviderId.PersianSites => 5,
        ProviderId.Nex1Music => 5,
        ProviderId.PersianIndex => 5,
        ProviderId.YouTube => 4,
        ProviderId.YtDlp => 3,
        ProviderId.SoundCloud => 2,
        ProviderId.RadioJavan => 1,
        _ => 0,
    };

    public async Task StopAsync()
    {
        _queue.Writer.TryComplete();
        _shutdown.Cancel();
        foreach (var cts in _active.Values) CancelCore(cts);
        try { await Task.WhenAll(_workers).ConfigureAwait(false); } catch { /* workers drained */ }
    }

    /// <summary>Forwards progress to the per-job TCS and the UI event, throttled.</summary>
    private sealed class ProgressProxy : IProgress<DownloadProgress>
    {
        private readonly string _jobId;
        private readonly TaskCompletionSource<DownloadProgress> _tcs;
        private readonly Action<string, DownloadProgress>? _event;
        private DownloadProgress _last;
        private DateTime _lastEmit = DateTime.UtcNow;
        private DateTime _lastProgress = DateTime.UtcNow;

        public TimeSpan TimeSinceLastProgress => DateTime.UtcNow - _lastProgress;
        public DownloadPhase LastPhase => _last?.Phase ?? DownloadPhase.Queued;

        public ProgressProxy(string jobId, TaskCompletionSource<DownloadProgress> tcs,
            Action<string, DownloadProgress>? @event)
        {
            _jobId = jobId;
            _tcs = tcs;
            _event = @event;
        }

        public void Report(DownloadProgress value)
        {
            _last = value;
            _lastProgress = DateTime.UtcNow;
            if (value.Phase is DownloadPhase.Failed or DownloadPhase.Cancelled or DownloadPhase.Completed
                or DownloadPhase.AlreadyOwned or DownloadPhase.Paused)
            {
                _event?.Invoke(_jobId, value);
                return;
            }
            var throttleMs = value.Phase == DownloadPhase.Downloading ? 50 : 120;
            if ((DateTime.UtcNow - _lastEmit).TotalMilliseconds >= throttleMs)
            {
                _lastEmit = DateTime.UtcNow;
                _event?.Invoke(_jobId, value);
            }
        }
    }
}