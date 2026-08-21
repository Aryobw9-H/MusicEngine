namespace MusicEngine.Audio;

using System.Collections.Generic;
using System.IO;
using System.Threading;
using Configuration;
using Microsoft.Extensions.Logging;
using Text;

/// <summary>
/// A real library index (FEAT-03): the set of songs actually present on disk
/// under <see cref="ISettings.OutputDirectory"/>, so the "✓ In library" badge
/// reflects files rather than download history. Scanned on startup (background),
/// kept live by a <see cref="FileSystemWatcher"/> with a 1 s debounce, and
/// updated per-file when downloads complete.
///
/// Keys are the cross-script match keys of "artist title" (via
/// <see cref="TrackTextNormalizer.MatchKeys"/>), so the badge matches however
/// the search result spells the song — Persian, Finglish, or junk-suffixed.
/// Files are read with TagLib (same library the tagger uses); files without
/// tags fall back to "Artist - Title" filename parsing.
/// </summary>
public sealed class LibraryIndex : IDisposable
{
    private static readonly string[] AudioExtensions = { ".mp3", ".m4a", ".flac", ".opus" };
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(1);

    private readonly ISettings _settings;
    private readonly ILogger<LibraryIndex> _logger;
    private readonly SemaphoreSlim _scanGate = new(1, 1);

    private readonly object _lock = new();
    // Every match key → how many files hold it (refcount so deleting one copy
    // of a duplicated song keeps the badge).
    private readonly Dictionary<string, int> _keyCount = new(StringComparer.Ordinal);
    // Full path → the keys that file contributed.
    private readonly Dictionary<string, string[]> _byPath = new(StringComparer.OrdinalIgnoreCase);

    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _debounce;
    private volatile string _watchedDirectory = "";

    /// <summary>Raised (on a threadpool thread) after a scan or debounced batch updated the index.</summary>
    public event Action? Changed;

    public LibraryIndex(ISettings settings, ILogger<LibraryIndex>? logger = null)
    {
        _settings = settings;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryIndex>.Instance;
    }

    /// <summary>True when any file in the library matches this song (cross-script).</summary>
    public bool Contains(string artist, string title)
    {
        var keys = TrackTextNormalizer.MatchKeys($"{artist ?? ""} {title ?? ""}".Trim());
        if (keys.Length == 0) return false;
        lock (_lock)
        {
            foreach (var k in keys)
                if (_keyCount.ContainsKey(k))
                    return true;
        }
        return false;
    }

    /// <summary>Register a freshly written file so its badge flips without waiting for a rescan.</summary>
    public void Add(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !IsAudioFile(filePath)) return;
        try
        {
            var keys = ReadKeys(filePath);
            if (keys.Length == 0) return;
            lock (_lock)
            {
                AddKeysLocked(filePath, keys);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LibraryIndex.Add skipped {File}", filePath);
        }
    }

    /// <summary>
    /// Full scan (startup / directory change). Starts the watcher on the current
    /// output directory and replaces the index contents. Serialized against
    /// watcher batches by <see cref="_scanGate"/>.
    /// </summary>
    public async Task BuildAsync(CancellationToken ct = default)
    {
        await _scanGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() => ScanAndReplace(ct), ct).ConfigureAwait(false);
            Changed?.Invoke();
        }
        finally
        {
            _scanGate.Release();
        }
    }

    /// <summary>Re-scan after the output directory changed (Settings → Save).</summary>
    public async Task RebuildAsync(CancellationToken ct = default)
    {
        StopWatcher();
        await BuildAsync(ct).ConfigureAwait(false);
    }

    private void ScanAndReplace(CancellationToken ct)
    {
        var directory = _settings.OutputDirectory;
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LibraryIndex: cannot create output directory");
            return;
        }

        // Swap the watcher first so events during the scan are caught by the
        // debounce rather than lost.
        StartWatcher(directory);

        var found = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in EnumerateAudioFiles(directory, ct))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var keys = ReadKeys(file);
                if (keys.Length > 0) found[file] = keys;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "LibraryIndex scan skipped {File}", file);
            }
        }

        lock (_lock)
        {
            _keyCount.Clear();
            _byPath.Clear();
            foreach (var (path, keys) in found)
                AddKeysLocked(path, keys);
        }
        _logger.LogInformation("LibraryIndex: {Count} files indexed under {Dir}", found.Count, directory);
    }

    private static IEnumerable<string> EnumerateAudioFiles(string directory, CancellationToken ct)
    {
        var queue = new Queue<string>();
        queue.Enqueue(directory);
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = queue.Dequeue();
            string[] files;
            string[] subdirs;
            try
            {
                files = Directory.GetFiles(dir);
                subdirs = Directory.GetDirectories(dir);
            }
            catch
            {
                continue; // unreadable subfolder — skip, never fail the scan
            }
            foreach (var f in files)
                if (IsAudioFile(f))
                    yield return f;
            foreach (var d in subdirs)
                queue.Enqueue(d);
        }
    }

    private static bool IsAudioFile(string path)
    {
        var ext = Path.GetExtension(path);
        foreach (var e in AudioExtensions)
            if (string.Equals(ext, e, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Artist/Title from tags, falling back to "Artist - Title.mp3" parsing.</summary>
    private static string[] ReadKeys(string path)
    {
        string artist;
        string title;
        try
        {
            using var file = TagLib.File.Create(path);
            artist = string.Join(", ", file.Tag.Performers ?? Array.Empty<string>());
            title = file.Tag.Title ?? "";
            if (!string.IsNullOrWhiteSpace(artist) || !string.IsNullOrWhiteSpace(title))
                return TrackTextNormalizer.MatchKeys($"{artist} {title}".Trim());
        }
        catch
        {
            // corrupt/foreign file — fall through to filename parsing
        }

        var stem = Path.GetFileNameWithoutExtension(path);
        var sep = stem.IndexOf(" - ", StringComparison.Ordinal);
        if (sep > 0)
        {
            artist = stem[..sep].Trim();
            title = stem[(sep + 3)..].Trim();
        }
        else
        {
            artist = "";
            title = stem.Trim();
        }
        return TrackTextNormalizer.MatchKeys($"{artist} {title}".Trim());
    }

    private void AddKeysLocked(string path, string[] keys)
    {
        _byPath[path] = keys;
        foreach (var k in keys)
            _keyCount[k] = _keyCount.GetValueOrDefault(k) + 1;
    }

    private void RemovePathLocked(string path)
    {
        if (!_byPath.TryGetValue(path, out var keys)) return;
        _byPath.Remove(path);
        foreach (var k in keys)
        {
            var count = _keyCount.GetValueOrDefault(k) - 1;
            if (count <= 0) _keyCount.Remove(k);
            else _keyCount[k] = count;
        }
    }

    // ---------------- FileSystemWatcher ----------------

    private void StartWatcher(string directory)
    {
        try
        {
            var watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            };
            watcher.Created += (_, e) => OnFsEvent(e.FullPath, e.ChangeType);
            watcher.Deleted += (_, e) => OnFsEvent(e.FullPath, e.ChangeType);
            watcher.Renamed += (_, e) => OnFsEvent(e.FullPath, e.ChangeType);
            watcher.Changed += (_, e) => OnFsEvent(e.FullPath, e.ChangeType);
            // Buffer overflow (many files at once) — rescan everything.
            watcher.Error += (_, _) => OnFsEvent(null, WatcherChangeTypes.All);
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
            _watchedDirectory = directory;
        }
        catch (Exception ex)
        {
            // Watching can fail on network shares / permission edges; the
            // startup scan still gave us a valid snapshot.
            _logger.LogWarning(ex, "LibraryIndex: folder watch unavailable for {Dir}", directory);
        }
    }

    private void StopWatcher()
    {
        try { _watcher?.Dispose(); } catch { /* best effort */ }
        _watcher = null;
        _watchedDirectory = "";
    }

    private void OnFsEvent(string? path, WatcherChangeTypes changeType)
    {
        if (path is not null && !IsAudioFile(path)) return;

        // Debounce: bursts (a finished download writes file + tag + rename) and
        // full-directory changes (drag-drop of many files) coalesce into one batch.
        CancellationTokenSource? cts;
        lock (_lock)
        {
            _debounce?.Cancel();
            _debounce?.Dispose();
            _debounce = cts = new CancellationTokenSource();
        }
        var token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(DebounceDelay, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            await HandleBatch(token, path, changeType).ConfigureAwait(false);
        }, token);
    }

    private async Task HandleBatch(CancellationToken token, string? path, WatcherChangeTypes changeType)
    {
        await _scanGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // The output directory may have been changed in Settings while we
            // were debouncing — rescan under the new folder in that case.
            var directory = _settings.OutputDirectory;
            if (!string.Equals(directory, _watchedDirectory, StringComparison.OrdinalIgnoreCase))
            {
                StopWatcher();
                ScanAndReplace(token);
                Changed?.Invoke();
                return;
            }

            if (changeType == WatcherChangeTypes.All)
            {
                // Buffer overflow / watcher error — rescan everything.
                ScanAndReplace(token);
                Changed?.Invoke();
                return;
            }

            if (changeType == WatcherChangeTypes.Changed && path is not null && File.Exists(path))
            {
                // A re-tag rewrote an existing file — refresh just that path.
                lock (_lock) RemovePathLocked(path);
                Add(path);
                Changed?.Invoke();
                return;
            }

            // Created / Deleted / Renamed: reconcile by diffing the directory.
            var additions = new List<string>();
            lock (_lock)
            {
                foreach (var file in EnumerateAudioFiles(directory, token))
                    if (!_byPath.ContainsKey(file))
                        additions.Add(file);
                foreach (var p in _byPath.Keys.ToList())
                    if (!File.Exists(p))
                        RemovePathLocked(p);
            }
            foreach (var file in additions)
                Add(file);
            Changed?.Invoke();
        }
        finally
        {
            _scanGate.Release();
        }
    }

    public void Dispose()
    {
        StopWatcher();
        lock (_lock)
        {
            _debounce?.Cancel();
            _debounce?.Dispose();
            _debounce = null;
        }
        _scanGate.Dispose();
    }
}
