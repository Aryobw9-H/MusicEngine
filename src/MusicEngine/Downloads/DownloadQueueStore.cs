namespace MusicEngine.Downloads;

using System.Text.Json;
using System.Text.Json.Serialization;
using Models;

/// <summary>One resumable job persisted across restarts (FEAT-02).</summary>
public sealed record PersistedJob(
    string Id,
    string Title,
    string Artist,
    string? SourceUrl,
    ProviderId Provider,
    string TargetPath,
    DownloadPhase Phase,
    DateTimeOffset QueuedAt);

/// <summary>
/// Persists the download queue to %APPDATA%\MusicEngine\queue.json so an app
/// restart can offer to resume interrupted transfers — the machinery
/// (HttpDownloader's URL-bound .part/.state resume) already exists; this just
/// keeps the queue alive across sessions. Writes are debounced (500 ms, same
/// pattern as AppState PERF-01) and atomic (temp file + rename, BUG-09), and
/// only resumable phases are ever written, so the file never fills with
/// finished/cancelled history.
/// </summary>
public sealed class DownloadQueueStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }, // names, not numbers — stable across versions
    };

    private readonly Func<IReadOnlyList<PersistedJob>> _snapshot;
    private CancellationTokenSource? _pendingSave;

    public string Path { get; }

    /// <param name="snapshot">Called at flush time to capture the current queue
    /// (deferred so the store has no constructor dependency on the manager).</param>
    /// <param name="path">Override for tests; production uses %APPDATA%.</param>
    public DownloadQueueStore(Func<IReadOnlyList<PersistedJob>> snapshot, string? path = null)
    {
        _snapshot = snapshot;
        Path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MusicEngine", "queue.json");
    }

    public IReadOnlyList<PersistedJob> Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var jobs = JsonSerializer.Deserialize<List<PersistedJob>>(File.ReadAllText(Path), JsonOpts);
                if (jobs is not null) return jobs;
            }
        }
        catch { /* corrupted/partial queue — start with an empty queue */ }
        return Array.Empty<PersistedJob>();
    }

    /// <summary>Coalesce writes: a burst of phase changes schedules one background flush.</summary>
    public void ScheduleSave()
    {
        _pendingSave?.Cancel();
        _pendingSave?.Dispose();
        var cts = _pendingSave = new CancellationTokenSource();
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, token).ConfigureAwait(false);
                await SaveAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* superseded by a newer save */ }
        });
    }

    /// <summary>Flush immediately (used at shutdown so no pending jobs are lost).</summary>
    public async Task SaveAsync()
    {
        try
        {
            var jobs = _snapshot();
            if (jobs.Count == 0)
            {
                // Remove a stale file so a restart doesn't offer to resume ghosts.
                if (File.Exists(Path))
                {
                    File.Delete(Path);
                }
                return;
            }
            var dir = System.IO.Path.GetDirectoryName(Path)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(jobs, JsonOpts);
            var tmp = Path + ".tmp";
            await File.WriteAllTextAsync(tmp, json).ConfigureAwait(false);
            File.Move(tmp, Path, overwrite: true);
        }
        catch { /* best effort — a lost queue is not worth crashing over */ }
    }
}
