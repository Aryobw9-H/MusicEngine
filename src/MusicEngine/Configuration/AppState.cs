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

    private string _path = "";
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

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
        if (RecentSearches.Count > 20) RecentSearches.RemoveRange(20, RecentSearches.Count - 20);
        Save();
    }

    public void PushHistory(HistoryEntry entry)
    {
        History.RemoveAll(h => string.Equals(h.FilePath, entry.FilePath, StringComparison.OrdinalIgnoreCase));
        History.Insert(0, entry);
        if (History.Count > 200) History.RemoveRange(200, History.Count - 200);
        Save();
    }

    public void ClearHistory()
    {
        History.Clear();
        Save();
    }

    public bool AlreadyOwned(string title, string artist) =>
        History.Any(h => string.Equals(h.Title, title, StringComparison.OrdinalIgnoreCase)
                      && string.Equals(h.Artist, artist, StringComparison.OrdinalIgnoreCase));

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* best effort */ }
    }
}
