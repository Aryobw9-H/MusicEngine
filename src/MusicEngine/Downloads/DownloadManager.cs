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

    /// <summary>The concrete transfer a job is (or was) using — persisted so a
    /// restart can resume the exact URL that owns the .part/.state files.</summary>
    private sealed record JobTarget(string SourceUrl, ProviderId Provider, string TargetPath);

    private readonly IReadOnlyList<ISearchProvider> _searchProviders;
    private readonly IReadOnlyList<IDownloadProvider> _downloadProviders;
    private readonly Configuration.ISettings _config;
    private readonly TrackTagger _tagger;
    private readonly DownloadQueueStore? _store;
    private readonly ILogger<DownloadManager> _logger;

    private readonly Channel<(EnqueuedJob Job, TaskCompletionSource<DownloadProgress> Completion, CancellationTokenSource Cts)> _queue;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();
    private readonly ConcurrentDictionary<string, EnqueuedJob> _jobs = new();
    private readonly ConcurrentDictionary<string, bool> _paused = new();
    private readonly ConcurrentDictionary<string, DownloadPhase> _jobPhase = new();
    private readonly ConcurrentDictionary<string, JobTarget> _jobTarget = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _workers = new();

    public event Action<EnqueuedJob>? JobAdded;
    public event Action<string, DownloadProgress>? JobProgress;
    /// <summary>Raised when a job begins transferring via a concrete provider ("Radio Javan", "yt-dlp"…).</summary>
    public event Action<string, string>? JobStarted;

    public DownloadManager(
        IEnumerable<ISearchProvider> searchProviders,
        IEnumerable<IDownloadProvider> downloadProviders,
        Configuration.ISettings config,
        TrackTagger tagger,
        DownloadQueueStore? store = null,
        ILogger<DownloadManager>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(searchProviders);
        ArgumentNullException.ThrowIfNull(downloadProviders);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(tagger);
        _searchProviders = searchProviders.Where(p => p.IsAvailable).ToArray();
        _downloadProviders = downloadProviders.Where(p => p.IsAvailable).ToArray();
        _config = config;
        _tagger = tagger;
        _store = store;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DownloadManager>.Instance;
        _queue = Channel.CreateUnbounded<(EnqueuedJob, TaskCompletionSource<DownloadProgress>, CancellationTokenSource)>(
            new UnboundedChannelOptions { SingleReader = false });

        // FEAT-02: every progress report updates the phase and schedules a
        // debounced persist; the snapshot only keeps resumable phases, so a
        // finished job disappears from queue.json once the worker drops it.
        if (store is not null)
        {
            JobProgress += (id, p) =>
            {
                _jobPhase[id] = p.Phase;
                store.ScheduleSave();
            };
        }

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
        _jobPhase[job.Id] = DownloadPhase.Queued;
        _store?.ScheduleSave();
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
        if (_active.TryGetValue(jobId, out var cts))
        {
            _paused.TryRemove(jobId, out _);
            return CancelCore(cts);
        }
        if (_paused.TryRemove(jobId, out _))
        {
            // A paused job has no live token — deliver the terminal state directly.
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
                // Pause cancels the token mid-run; RunJobAsync surfaces that as
                // "Cancelled" — convert it back so the UI sees the truth.
                if (final.Phase == DownloadPhase.Cancelled && _paused.ContainsKey(job.Id))
                    final = new DownloadProgress(DownloadPhase.Paused, Message: "Paused");
                tcs.TrySetResult(final);
                EmitTerminal(job.Id, final);
            }
            catch (OperationCanceledException)
            {
                // Peek (not remove) — the flag must survive so the finally block
                // keeps the job in _jobs and Resume() can find it later.
                if (_paused.ContainsKey(job.Id))
                {
                    var p = new DownloadProgress(DownloadPhase.Paused, Message: "Paused");
                    tcs.TrySetResult(p);
                    EmitTerminal(job.Id, p);
                }
                else
                {
                    _jobs.TryRemove(job.Id, out _);
                    var p = new DownloadProgress(DownloadPhase.Cancelled, Message: "Cancelled");
                    tcs.TrySetResult(p);
                    EmitTerminal(job.Id, p);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Download job failed: {Title} — {Msg}", job.Title, ex.Message);
                _jobs.TryRemove(job.Id, out _);
                var p = new DownloadProgress(DownloadPhase.Failed, Message: ex.Message);
                tcs.TrySetResult(p);
                EmitTerminal(job.Id, p);
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

    /// <summary>
    /// Terminal phases must reach the UI through <see cref="JobProgress"/> — the
    /// per-job TCS only serves the enqueueing caller. Without this the rows never
    /// leave "Downloading", toasts/history never fire and the pause/resume/restart
    /// buttons never toggle.
    /// </summary>
    private void EmitTerminal(string jobId, DownloadProgress p)
    {
        if (p.Phase is DownloadPhase.Completed or DownloadPhase.AlreadyOwned or DownloadPhase.Failed
            or DownloadPhase.Cancelled or DownloadPhase.Paused)
        {
            JobProgress?.Invoke(jobId, p);
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

        var resolved = await ResolveCandidatesAsync(work, options.TagTemplate!, progress, ct).ConfigureAwait(false);

        // FEAT-02: a partial transfer from a previous session must resume that
        // exact URL — re-resolution could pick a different source, and the
        // .part/.state files are URL-bound, so that would discard the progress.
        // The persisted URL is tried first; fresh resolution remains the fallback.
        var candidates = resolved;
        if (_jobTarget.TryGetValue(job.Id, out var prior)
            && prior.TargetPath is { Length: > 0 } tp
            && File.Exists(tp + ".part")
            && !resolved.Any(r => string.Equals(r.SourceUrl, prior.SourceUrl, StringComparison.OrdinalIgnoreCase)))
        {
            var resumeSource = new SearchResult
            {
                Provider = prior.Provider,
                Id = "resume:" + job.Id,
                Metadata = new TrackMetadata { Title = job.Title, Artist = job.Artist },
                SourceUrl = prior.SourceUrl,
                MaxQuality = StreamQuality.High192K,
                Downloadable = true,
            };
            candidates = new[] { resumeSource }.Concat(resolved);
        }

        foreach (var candidate in candidates)
        {
            if (ct.IsCancellationRequested)
                return new DownloadProgress(DownloadPhase.Cancelled, Message: "Cancelled");

            var chain = BuildChain(candidate);
            foreach (var provider in chain)
            {
                try
                {
                    // FEAT-02: remember the concrete transfer so a restart can
                    // resume it. The target path is deterministic (same title/
                    // artist/template), so the provider rewrites to the same file.
                    _jobTarget[job.Id] = new JobTarget(
                        candidate.SourceUrl ?? "", provider.Id,
                        System.IO.Path.Combine(options.OutputDirectory,
                            FileNaming.Build(options.TagTemplate, candidate, ".mp3", _config.FilenameTemplate)));
                    _store?.ScheduleSave();

                    progress.Report(new DownloadProgress(DownloadPhase.Resolving, 0, null,
                                            $"Via {provider.DisplayName}…"));
                    JobStarted?.Invoke(job.Id, provider.DisplayName);

                    using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var resolveTask = provider.DownloadAsync(candidate, options, progress, watchdogCts.Token);

                    // NOTE: the watchdog must NOT dispose watchdogCts — the outer
                    // `using` does that. Disposing here races the catch filter's
                    // watchdogCts.IsCancellationRequested read below, which throws
                    // ObjectDisposedException on a disposed source.
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

                    // Reject non-audio output: blocked CDNs and dead hosts return
                    // HTML/JSON error pages that would otherwise be renamed .mp3
                    // and reported as a successful download (the classic source of
                    // "corrupt" files). Fail loudly so the chain falls back to the
                    // next source instead of shipping garbage.
                    if (!AudioFile.IsProbablyAudio(result.FilePath))
                    {
                        try { File.Delete(result.FilePath); } catch { /* best effort */ }
                        throw new InvalidOperationException(
                            $"{provider.DisplayName} returned a non-audio file (blocked page or dead CDN) — trying the next source");
                    }

                    if (options.EmbedTags && provider.Id != ProviderId.YtDlp)
                    {
                        progress.Report(new DownloadProgress(DownloadPhase.Tagging, 0, null, "Writing tags…"));
                        await _tagger.TagAsync(result.FilePath, options.TagTemplate ?? candidate.Metadata).ConfigureAwait(false);
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

        _logger.LogError("No source could download this track. Candidates tried: {Count}", candidates.Count());
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

        _logger.LogWarning("ResolveCandidates: DownloadableVersions={Count}, ranked=[{Providers}]", ranked.Count, string.Join(",", ranked.Select(r => r.Provider)));
        var term = string.Join(" ", new[] { tagTemplate.Artist, tagTemplate.Title }.Where(s => !string.IsNullOrWhiteSpace(s)));

        // 2. Always give the Iranian download-tier sources a shot. Their direct
        //    320k MP3s (upmusics/musics-fa/nex1music) rank above SoundCloud, Radio
        //    Javan and YouTube streams — and the search display only consults them
        //    speculatively for Persian queries, so international tracks and
        //    download-time candidates never got a chance before. Skipped when the
        //    best known version is already a direct Persian MP3 (nothing to improve).
        if (ranked.Count == 0 || DownloadRank(ranked[0].Provider) < 5)
        {
            if (term.Length > 0)
            {
                // Tighter budget when we already have a fallback, generous when
                // these sites are the only hope.
                var budget = ranked.Count > 0 ? TimeSpan.FromSeconds(12) : TimeSpan.FromSeconds(20);
                var slowFound = await SearchSlowTierAsync(term, work.Goal, budget, progress, ct).ConfigureAwait(false);
                _logger.LogWarning("ResolveCandidates: slowTier found {Count} results [{Providers}]", slowFound.Count, string.Join(",", slowFound.Select(r => r.Provider)));
                if (slowFound.Count > 0)
                    return slowFound.Concat(ranked); // slow tier ranks 5 → ordered ahead of ranked
            }
        }

        if (ranked.Count > 0) return ranked;

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

    /// <summary>
    /// One parallel pass over the download-tier Persian sources (nex1music,
    /// upmusics/musics-fa via the python sidecar), gated against the goal,
    /// bounded by <paramref name="budget"/>. Slow sites must not delay the fast
    /// ones, and the whole pass must never exceed the budget.
    /// </summary>
    private async Task<List<SearchResult>> SearchSlowTierAsync(
        string term, GoalSong goal, TimeSpan budget, ProgressProxy progress, CancellationToken ct)
    {
        var slowTier = _searchProviders.Where(p => p.Tier == SearchTier.DownloadOnly).ToList();
        if (slowTier.Count == 0) return new List<SearchResult>();

        // Domestic Iranian sites need Persian text. The goal term from iTunes/
        // Deezer is often Latin/Finglish ("Amir Tataloo Jahanam"); expand it
        // so domestic scrapers can find the right pages. Always include the
        // original Latin term too — some sites store metadata in Latin.
        var variants = Text.FinglishQueryExpander.Expand(term);
        var searchTerms = variants.Count > 0
            ? variants.Append(term).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : new List<string> { term };

        progress.Report(new DownloadProgress(DownloadPhase.Resolving, 0, null, "Searching Iranian sources…"));
        var found = new List<SearchResult>();
        var searchTask = Task.Run(async () =>
        {
            var perProvider = slowTier.Select(p => Task.Run(async () =>
            {
                // PERF-07: the row shows which source is being tried, so a
                // slow proxy reads as "working" rather than "hung".
                progress.Report(new DownloadProgress(DownloadPhase.Resolving, 0, null,
                    $"Trying {p.DisplayName}…"));
                var local = new List<SearchResult>();
                try
                {
                    foreach (var queryVariant in searchTerms)
                    {
                        if (local.Count > 0) break; // got a hit, no need for more variants
                        await foreach (var r in p.SearchAsync(queryVariant, 5, ct).ConfigureAwait(false))
                        {
                            // Download-resolution gate: lenient match since the user already
                            // picked the song. Falls back to search-term token containment.
                            if (SearchService.PassesGoalGate(r, goal)
                                || SearchService.PassesLooseGate(r, goal)
                                || GoalGate.PassesDownloadGate(r, goal, term))
                                local.Add(r);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Slow-tier provider {Provider} failed: {Msg}", p.Id, ex.Message);
                }
                return local;
            }, ct));
            var lists = await Task.WhenAll(perProvider).ConfigureAwait(false);
            foreach (var list in lists) found.AddRange(list);
        }, ct);

        var winner = await Task.WhenAny(searchTask, Task.Delay(budget, ct)).ConfigureAwait(false);
        if (winner == searchTask)
            await searchTask.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        return found
            .OrderByDescending(r => DownloadRank(r.Provider))
            .ThenByDescending(r => r.MaxQuality)
            .ToList();
    }

    /// <summary>Native provider first, universal yt-dlp second.</summary>
    private IEnumerable<IDownloadProvider> BuildChain(SearchResult target)
    {
        // Match by provider ID first — YtDlpProvider.CanDownload returns true for
        // everything, which poisons FirstOrDefault and makes every download go to
        // yt-dlp instead of the native domestic provider.
        var native = _downloadProviders.FirstOrDefault(p => p.Id == target.Provider)
                     ?? _downloadProviders.FirstOrDefault(p => p.CanDownload(target));
        _logger.LogWarning("BuildChain: target.Provider={Provider}, nativeFound={Native}, allDownloadProviders=[{Ids}]",
            target.Provider, native?.Id, string.Join(",", _downloadProviders.Select(p => $"{p.Id}({p.GetType().Name})")));
        if (native is not null) yield return native;
        var universal = _downloadProviders.FirstOrDefault(p => p.Id == ProviderId.YtDlp && p != native);
        if (universal is not null) yield return universal;
    }

    /// <summary>Persian direct MP3 > YouTube > yt-dlp synth > SoundCloud > Radio Javan.</summary>
    private static int DownloadRank(ProviderId p) => Providers.ProviderCatalog.Get(p).DownloadRank;

    /// <summary>Resumable jobs persisted on disk (FEAT-02) — empty when the store is absent.</summary>
    public IReadOnlyList<PersistedJob> LoadPendingJobs() => _store?.Load() ?? Array.Empty<PersistedJob>();

    /// <summary>Live queue snapshot for persistence (the store's deferred callback).</summary>
    public IReadOnlyList<PersistedJob> PendingJobsSnapshot() => Snapshot();

    /// <summary>
    /// Re-enqueue jobs saved by a previous session. Non-terminal jobs only; the
    /// persisted SourceUrl is tried first so HttpDownloader resumes the existing
    /// .part/.state instead of restarting from zero.
    /// </summary>
    public Task ResumeAsync(IEnumerable<PersistedJob> jobs, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var j in jobs)
        {
            if (j.Phase is DownloadPhase.Completed or DownloadPhase.Failed
                or DownloadPhase.Cancelled or DownloadPhase.AlreadyOwned) continue;

            var work = BuildWorkFromPersisted(j);
            var job = new EnqueuedJob(j.Id, j.Title, j.Artist, work);
            _jobs[job.Id] = job;
            _jobPhase[job.Id] = DownloadPhase.Queued;
            if (j.SourceUrl is { Length: > 0 } && j.TargetPath is { Length: > 0 })
                _jobTarget[job.Id] = new JobTarget(j.SourceUrl, j.Provider, j.TargetPath);

            var tcs = new TaskCompletionSource<DownloadProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cts = new CancellationTokenSource();
            _active[job.Id] = cts;
            _queue.Writer.TryWrite((job, tcs, cts));
            JobAdded?.Invoke(job);
        }
        _store?.ScheduleSave();
        return Task.CompletedTask;
    }

    /// <summary>Rebuild a minimal work from the persisted identity; resolution
    /// (slow tier → yt-dlp synth) refinds a live source on the next run.</summary>
    private static TrackWork BuildWorkFromPersisted(PersistedJob j)
    {
        var meta = new TrackMetadata { Title = j.Title, Artist = j.Artist };
        var source = new SearchResult
        {
            Provider = j.Provider,
            Id = "resume:" + j.Id,
            Metadata = meta,
            SourceUrl = j.SourceUrl ?? "",
            MaxQuality = StreamQuality.High192K,
            Downloadable = true,
        };
        var versions = j.SourceUrl is { Length: > 0 }
            ? new List<TrackVersion> { new(source, "resume", 1.0) }
            : new List<TrackVersion>();
        return new TrackWork(j.Title, j.Artist, source, versions,
            new GoalSong(j.Artist, j.Title, null, j.Provider));
    }

    /// <summary>Snapshot of the live queue for persistence — resumable phases only.</summary>
    private IReadOnlyList<PersistedJob> Snapshot()
    {
        var result = new List<PersistedJob>();
        foreach (var (id, job) in _jobs)
        {
            var phase = _jobPhase.GetValueOrDefault(id, DownloadPhase.Queued);
            if (phase is DownloadPhase.Completed or DownloadPhase.Failed
                or DownloadPhase.Cancelled or DownloadPhase.AlreadyOwned) continue;
            var target = _jobTarget.GetValueOrDefault(id);
            result.Add(new PersistedJob(
                id, job.Title, job.Artist,
                target?.SourceUrl,
                target?.Provider ?? ProviderId.Unknown,
                target?.TargetPath ?? "",
                phase,
                DateTimeOffset.UtcNow));
        }
        return result;
    }

    public async Task StopAsync()
    {
        _queue.Writer.TryComplete();
        _shutdown.Cancel();
        foreach (var cts in _active.Values) CancelCore(cts);
        try { await Task.WhenAll(_workers).ConfigureAwait(false); } catch { /* workers drained */ }
        // FEAT-02: flush the last-known queue so a restart can resume it.
        if (_store is not null) try { await _store.SaveAsync().ConfigureAwait(false); } catch { /* best effort */ }
    }

    /// <summary>Forwards progress to the per-job TCS and the UI event, throttled.</summary>
    private sealed class ProgressProxy : IProgress<DownloadProgress>
    {
        private readonly string _jobId;
        private readonly TaskCompletionSource<DownloadProgress> _tcs;
        private readonly Action<string, DownloadProgress>? _event;
        private DownloadProgress? _last;
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