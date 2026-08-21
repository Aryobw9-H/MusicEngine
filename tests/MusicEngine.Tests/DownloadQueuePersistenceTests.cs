namespace MusicEngine.Tests;

using Audio;
using Downloads;
using Http;
using Models;
using Xunit;

/// <summary>FEAT-02: the persistent download queue — store round-trip, atomic
/// cleanup, and the manager lifecycle (running jobs persisted, cancelled jobs
/// dropped). Fully offline.</summary>
public class DownloadQueuePersistenceTests
{
    [Fact]
    public async Task SaveThenLoadRoundTripsEnumsAsNames()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Value, "queue.json");
        var jobs = new List<PersistedJob>
        {
            new("job1", "بهشت", "تتلو", "http://cdn.test/behesht.mp3",
                ProviderId.RadioJavan, System.IO.Path.Combine(dir.Value, "تتلو - بهشت.mp3"),
                DownloadPhase.Downloading, DateTimeOffset.UtcNow),
        };

        var store = new DownloadQueueStore(() => jobs, path);
        await store.SaveAsync();

        var loaded = new DownloadQueueStore(() => new List<PersistedJob>(), path).Load();
        var job = Assert.Single(loaded);
        Assert.Equal("job1", job.Id);
        Assert.Equal(ProviderId.RadioJavan, job.Provider);   // names, not numbers
        Assert.Equal(DownloadPhase.Downloading, job.Phase);
        Assert.Equal("http://cdn.test/behesht.mp3", job.SourceUrl);
        Assert.Equal("تتلو - بهشت.mp3", System.IO.Path.GetFileName(job.TargetPath));
    }

    [Fact]
    public async Task EmptySnapshotRemovesStaleQueueFile()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Value, "queue.json");
        var jobs = new List<PersistedJob>
        {
            new("job1", "Bargard", "Sijal", null, ProviderId.ITunes, "", DownloadPhase.Queued, DateTimeOffset.UtcNow),
        };

        await new DownloadQueueStore(() => jobs, path).SaveAsync();
        Assert.True(File.Exists(path));

        // A later session with nothing pending must not offer stale ghosts.
        await new DownloadQueueStore(() => new List<PersistedJob>(), path).SaveAsync();
        Assert.False(File.Exists(path));
        Assert.Empty(new DownloadQueueStore(() => new List<PersistedJob>(), path).Load());
    }

    [Fact]
    public async Task RunningJobIsPersistedAndCancelDropsIt()
    {
        using var dir = new TempDir();
        var path = System.IO.Path.Combine(dir.Value, "queue.json");
        var settings = new TestSettings { OutputDirectory = dir.Value };
        var provider = new BlockingProvider();

        DownloadManager? manager = null;
        var store = new DownloadQueueStore(() => manager!.PendingJobsSnapshot(), path);
        manager = new DownloadManager(
            new ISearchProvider[] { provider },
            new IDownloadProvider[] { provider },
            settings,
            new TrackTagger(new SharedHttpClient()),
            store);

        var started = new TaskCompletionSource();
        manager.JobStarted += (_, _) => started.TrySetResult();

        var version = new SearchResult
        {
            Provider = ProviderId.RadioJavan,
            Id = "rj:1",
            Metadata = new TrackMetadata { Title = "Bargard", Artist = "Sijal" },
            SourceUrl = "http://cdn.test/song.mp3",
            MaxQuality = StreamQuality.High192K,
            Downloadable = true,
        };
        var work = new TrackWork("Bargard", "Sijal", version,
            new List<TrackVersion> { new(version, "main", 1.0) },
            new GoalSong("Sijal", "Bargard", null, ProviderId.RadioJavan));

        try
        {
            var enqueued = manager.EnqueueAsync(work);
            // Generous bounds: the full suite runs the slow HTTP tests in
            // parallel, so the worker can be starved for a few seconds.
            await started.Task.WaitAsync(TimeSpan.FromSeconds(15));

            // The job is mid-transfer → persisted with the concrete source.
            // Wait until a progress report escapes the throttle so the phase
            // hook has recorded the live phase.
            await WaitUntilAsync(() =>
                manager.PendingJobsSnapshot() is [{ Phase: DownloadPhase.Downloading } _],
                TimeSpan.FromSeconds(15));
            await store.SaveAsync();
            var job = Assert.Single(manager.LoadPendingJobs());
            Assert.Equal(DownloadPhase.Downloading, job.Phase);
            Assert.Equal("http://cdn.test/song.mp3", job.SourceUrl);
            Assert.Equal(ProviderId.RadioJavan, job.Provider);
            Assert.Contains("Bargard", job.TargetPath);

            // Cancel → the job leaves the persisted queue.
            manager.Cancel(job.Id);
            await WaitUntilAsync(() => manager.PendingJobsSnapshot().Count == 0, TimeSpan.FromSeconds(15));
            await store.SaveAsync();
            Assert.Empty(manager.LoadPendingJobs());

            await enqueued; // completes as Cancelled once the worker unwinds
        }
        finally
        {
            await manager.StopAsync();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        Assert.Fail("Condition not met within " + timeout);
    }

    /// <summary>A download provider that starts a transfer and never finishes —
    /// deterministic stand-in for a real CDN so the manager lifecycle is testable.</summary>
    private sealed class BlockingProvider : ISearchProvider, IDownloadProvider
    {
        public ProviderId Id => ProviderId.RadioJavan;
        public string DisplayName => "Blocking";
        public SearchTier Tier => SearchTier.Display;
        public bool IsAvailable => true;

        public async IAsyncEnumerable<SearchResult> SearchAsync(string query, int maxResults, CancellationToken ct = default)
        {
            yield break;
        }

        public bool CanDownload(SearchResult result) => true;

        public async Task<DownloadResult> DownloadAsync(
            SearchResult track, DownloadOptions options,
            IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            // Keep reporting so the manager's phase tracking sees a real phase
            // past the progress throttle; cancel → OperationCanceledException.
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new DownloadProgress(DownloadPhase.Downloading, 0, 1000, "Downloading"));
                await Task.Delay(25, ct);
            }
        }
    }
}
