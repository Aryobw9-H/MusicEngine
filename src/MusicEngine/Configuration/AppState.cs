namespace MusicEngine.Configuration;

using System.Text.Json;

/// <summary>One finished download in the library history.</summary>
public sealed record HistoryEntry(
    string Title,
    string Artist,
    string FilePath,
    string Provider,
    DateTimeOffset At);

/// <summary>
/// Lightweight persisted UI state (recent searches, download history) stored in
/// %APPDATA%\MusicEngine\state.json. Kept apart from appsettings so settings can
/// be hand-edited without touching history.
/// </summary>
public sealed class AppState
{
    public List<string> RecentSearches { get; set; } = new();
    public List<HistoryEntry> History { get; set; } = new();

    // Bounded lists (XAML-05): keep state.json small no matter how long the
    // install lives. Oldest entries are trimmed first.
    public const int HistoryCap = 1000;
    public const int RecentSearchesCap = 50;

    private string _path = "";
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private CancellationTokenSource? _pendingSave;

    public static AppState Load()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MusicEngine");
        var path = Path.Combine(dir, "state.json");
        try
        {
            if (File.Exists(path))
            {
                var st = JsonSerializer.Deserialize<AppState>(File.ReadAllText(path), JsonOpts) ?? new AppState();
                st._path = path;
                return st;
            }
        }
        catch { /* corrupted state — start fresh */ }
        var fresh = new AppState { _path = path };
        Directory.CreateDirectory(dir);
        fresh.Save();
        return fresh;
    }

    public void PushSearch(string query)
    {
        query = query.Trim();
        if (query.Length == 0) return;
        RecentSearches.RemoveAll(s => string.Equals(s, query, StringComparison.OrdinalIgnoreCase));
        RecentSearches.Insert(0, query);
        if (RecentSearches.Count > RecentSearchesCap) RecentSearches.RemoveRange(RecentSearchesCap, RecentSearches.Count - RecentSearchesCap);
        ScheduleSave();
    }

    public void PushHistory(HistoryEntry entry)
    {
        History.RemoveAll(h => string.Equals(h.FilePath, entry.FilePath, StringComparison.OrdinalIgnoreCase));
        History.Insert(0, entry);
        if (History.Count > HistoryCap) History.RemoveRange(HistoryCap, History.Count - HistoryCap);
        ScheduleSave();
    }

    public void ClearHistory()
    {
        History.Clear();
        ScheduleSave();
    }

    /// <summary>
    /// Coalesce writes: mutations update the in-memory lists immediately and
    /// schedule a single background flush ~500 ms later, so a burst of searches
    /// or completed downloads never blocks the UI thread (PERF-01). The final
    /// state is flushed synchronously in <see cref="Save"/> at shutdown.
    /// </summary>
    private void ScheduleSave()
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

    /// <summary>Async, atomic, off-thread save (PERF-01).</summary>
    public async Task SaveAsync()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(this, JsonOpts);
            var tmp = _path + ".tmp";
            await File.WriteAllTextAsync(tmp, json).ConfigureAwait(false);
            File.Move(tmp, _path, overwrite: true);
        }
        catch { /* best effort */ }
    }

    public bool AlreadyOwned(string title, string artist) =>
        History.Any(h => string.Equals(h.Title, title, StringComparison.OrdinalIgnoreCase)
                      && string.Equals(h.Artist, artist, StringComparison.OrdinalIgnoreCase));

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            WriteAtomic(_path, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// Write via a temp file + atomic rename so a crash or power loss mid-write
    /// cannot leave a truncated state.json (BUG-09). A stray <path>.tmp left by a
    /// crash between write and rename is ignored by <see cref="Load"/>.
    /// </summary>
    private static void WriteAtomic(string path, string json)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }
}
