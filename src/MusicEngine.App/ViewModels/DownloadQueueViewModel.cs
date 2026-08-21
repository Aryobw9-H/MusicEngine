namespace MusicEngine.App.ViewModels;

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Configuration;
using Downloads;
using Models;
using Ui;

/// <summary>
/// Download queue (MVVM-06): the visible rows, the song-level dedup set that
/// prevents two workers writing the same output file, job identity tracking and
/// the <see cref="DownloadManager"/> event wiring. History recording and toasts
/// are delegated back to the owner via <see cref="JobCompleted"/> /
/// <see cref="JobFailed"/>.
/// </summary>
public sealed class DownloadQueueViewModel : IDisposable
{
    private readonly DownloadManager _downloads;
    private readonly IDispatcher _ui;
    private readonly AppConfig _config;
    private readonly ToastService _toasts;
    private readonly HashSet<string> _queuedWorks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _jobProvider = new();

    /// <summary>Per-job identity: display strings plus the stable DedupKey used for
    /// retry bookkeeping and matching results rows after a download finishes.</summary>
    private readonly ConcurrentDictionary<string, (string Title, string Artist, string DedupKey)> _jobIdentity = new();

    public ObservableCollection<DownloadItemViewModel> DownloadQueue { get; } = new();

    /// <summary>Raised (on the UI thread) whenever the active count changes.</summary>
    public event Action? ActiveChanged;

    /// <summary>Raised (on the UI thread) when a job completes — owner records history + badge.</summary>
    public event Action<DownloadItemViewModel>? JobCompleted;

    /// <summary>Raised (on the UI thread) when a job fails — owner toasts.</summary>
    public event Action<DownloadItemViewModel>? JobFailed;

    public int ActiveDownloads => DownloadQueue.Count(d => d.IsActive);

    /// <summary>Display name of the provider currently handling a job.</summary>
    public string? ProviderFor(string jobId) =>
        _jobProvider.TryGetValue(jobId, out var prov) ? prov : null;

    public DownloadQueueViewModel(DownloadManager downloads, IDispatcher ui, AppConfig config, ToastService toasts)
    {
        _downloads = downloads;
        _ui = ui;
        _config = config;
        _toasts = toasts;

        _downloads.JobAdded += job => _ui.Run(() =>
        {
            _jobIdentity[job.Id] = (job.Title, job.Artist, job.Work.Representative.DedupKey);
            if (!DownloadQueue.Any(d => d.JobId == job.Id))
            {
                DownloadQueue.Insert(0, new DownloadItemViewModel(job.Id, $"{job.Artist} — {job.Title}")
                {
                    Work = job.Work,
                });
            }
            ActiveChanged?.Invoke();
        });
        _downloads.JobProgress += (id, p) => _ui.Run(() =>
        {
            var item = DownloadQueue.FirstOrDefault(d => d.JobId == id);
            if (item is null) return;
            item.Apply(p, _jobProvider.TryGetValue(id, out var prov) ? prov : "");
            if (p.Phase is not (DownloadPhase.Completed or DownloadPhase.AlreadyOwned
                or DownloadPhase.Failed or DownloadPhase.Cancelled or DownloadPhase.Paused))
            {
                return;
            }
            ActiveChanged?.Invoke();
            switch (p.Phase)
            {
                case DownloadPhase.Completed:
                    // item.FilePath already set by Apply above.
                    if (_config.DownloadToasts)
                        _toasts.Show(new ToastViewModel { Title = "Download complete", Message = item.Title, FilePath = p.FilePath });
                    ReleaseQueueKey(id);
                    ScheduleQueueRemoval(item);
                    JobCompleted?.Invoke(item);
                    break;
                case DownloadPhase.AlreadyOwned:
                    ReleaseQueueKey(id);
                    ScheduleQueueRemoval(item);
                    break;
                case DownloadPhase.Failed:
                case DownloadPhase.Cancelled:
                    // Free the dedup key so the user can retry. Paused is NOT
                    // freed — the Resume button owns that job.
                    ReleaseQueueKey(id);
                    if (p.Phase == DownloadPhase.Failed)
                    {
                        if (_config.DownloadToasts)
                            _toasts.Show(new ToastViewModel { Title = "Download failed", Message = item.Title, IsError = true });
                        JobFailed?.Invoke(item);
                    }
                    break;
            }
        });
        _downloads.JobStarted += (id, providerName) => _jobProvider[id] = providerName;
    }

    /// <summary>Queue a track, deduped by SONG identity (the same song appears as
    /// several rows across sources; two workers on the same output file corrupt it).</summary>
    public bool Enqueue(TrackWork work, string statusMessage)
    {
        var key = SongKey(work.Title, work.Artist);
        if (!_queuedWorks.Add(key)) return false;
        _ = _downloads.EnqueueAsync(work);
        return true;
    }

    public void Cancel(string jobId) => _downloads.Cancel(jobId);

    public void CancelAll()
    {
        foreach (var item in DownloadQueue.Where(d => d.IsActive).ToList())
            _downloads.Cancel(item.JobId);
    }

    public void Pause(string jobId) => _downloads.Pause(jobId);
    public void Resume(string jobId) => _downloads.Resume(jobId);

    /// <summary>Re-download from a clean slate: drop the old row, free the dedup
    /// key, and enqueue the original work fresh (no string surgery).</summary>
    public void Restart(DownloadItemViewModel d)
    {
        if (d.Work is not { } work) return;
        _downloads.Cancel(d.JobId);
        DownloadQueue.Remove(d);
        _queuedWorks.Remove(SongKey(work.Title, work.Artist));
        _ = _downloads.EnqueueAsync(work);
    }

    public void ClearFinished()
    {
        foreach (var d in DownloadQueue.Where(x => !x.IsActive).ToList())
            DownloadQueue.Remove(d);
        ActiveChanged?.Invoke();
    }

    /// <summary>Song-level identity for the dedup set — mirrors the output
    /// filename domain (FileNaming uses the same title/artist) and is case-folded
    /// because Windows filenames are case-insensitive.</summary>
    private static string SongKey(string title, string artist) =>
        $"{title}|{artist}".Trim().ToLowerInvariant();

    /// <summary>Release the song-level dedup key once a job reaches any terminal
    /// phase, so the same song can be queued again (e.g. after deleting the file
    /// or retrying a failure).</summary>
    private void ReleaseQueueKey(string jobId)
    {
        if (_jobIdentity.TryGetValue(jobId, out var identity))
            _queuedWorks.Remove(SongKey(identity.Title, identity.Artist));
    }

    /// <summary>Drop a finished row from the downloads tab after a short grace
    /// period (long enough to click Open), so the tab shows what is actually
    /// happening instead of piling up completed items. History + toasts keep
    /// access to the file.</summary>
    private void ScheduleQueueRemoval(DownloadItemViewModel item)
    {
        _ = Task.Delay(TimeSpan.FromSeconds(12)).ContinueWith(_ =>
            _ui.Run(() =>
            {
                if (DownloadQueue.Remove(item)) ActiveChanged?.Invoke();
            }), TaskScheduler.Default);
    }

    public void Dispose()
    {
        // Event subscriptions die with the manager; nothing to release.
    }
}
