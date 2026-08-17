namespace MusicEngine.App.ViewModels;

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using Configuration;
using Downloads;
using Models;
using Providers;
using Search;

/// <summary>
/// Coordinates search, preview playback, downloads, history and toasts with the
/// UI thread. Search results stream in from the pipeline; downloads run through
/// the DownloadManager and update their rows via progress events.
/// </summary>
public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly ProviderRegistry _providers;
    private readonly DownloadManager _downloads;
    private readonly PreviewPlayer _preview;
    private readonly AppConfig _config;
    private readonly AppState _state;
        private readonly SearchResultCache _cache;
        private readonly ProviderHealthMonitor _health;
        private readonly IDispatcher _ui;
        private readonly IArtworkLoader _artwork;
        private readonly System.Threading.Timer _playerTimer;
        private readonly System.Threading.Timer _toastTimer;
        private readonly System.Threading.Timer _clipboardTimer;

    private CancellationTokenSource? _searchCts;
    private TrackItemViewModel? _playingTrack;
    private readonly HashSet<string> _queuedWorks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _jobProvider = new();
    private readonly ConcurrentDictionary<string, (string Title, string Artist)> _jobIdentity = new();
    private string _lastClipboard = "";

    public ObservableCollection<TrackItemViewModel> Results { get; } = new();
    public ObservableCollection<TrackItemViewModel> ResultsView { get; } = new();
    public ObservableCollection<DownloadItemViewModel> DownloadQueue { get; } = new();
    public ObservableCollection<HistoryItemViewModel> History { get; } = new();
    public ObservableCollection<ToastViewModel> Toasts { get; } = new();
    public ObservableCollection<string> RecentSearches { get; } = new();

    private string _query = "";
    public string Query { get => _query; set => Set(ref _query, value); }

    private bool _isSearching;
    public bool IsSearching { get => _isSearching; set => Set(ref _isSearching, value); }

    private string _status = "Ready — type a song name (Finglish, Persian, or paste a link) and press Enter";
    public string Status { get => _status; set => Set(ref _status, value); }

    private bool _hasResults;
    public bool HasResults { get => _hasResults; set => Set(ref _hasResults, value); }

    private ResultSort _sortMode = ResultSort.Relevance;
    public ResultSort SortMode
    {
        get => _sortMode;
        set
        {
            if (Set(ref _sortMode, value)) RebuildResultsView();
        }
    }

    private bool _hideInLibrary;
    public bool HideInLibrary
    {
        get => _hideInLibrary;
        set
        {
            if (Set(ref _hideInLibrary, value)) RebuildResultsView();
        }
    }

    private bool _showDownloads = true;
    public bool ShowDownloads { get => _showDownloads; set => Set(ref _showDownloads, value); }

    private bool _showHistory;
    public bool ShowHistory { get => _showHistory; set => Set(ref _showHistory, value); }

    private string _clipboardPill = "";
    public string ClipboardPill { get => _clipboardPill; set => Set(ref _clipboardPill, value); }

    // ---------- now playing ----------

    private TrackItemViewModel? _nowPlaying;
    public TrackItemViewModel? NowPlaying
    {
        get => _nowPlaying;
        private set => Set(ref _nowPlaying, value);
    }

    private string _playerTitle = "";
    public string PlayerTitle { get => _playerTitle; private set => Set(ref _playerTitle, value); }

    private string _playerArtist = "";
    public string PlayerArtist { get => _playerArtist; private set => Set(ref _playerArtist, value); }

    private BitmapImage? _playerArtwork;
    public BitmapImage? PlayerArtwork { get => _playerArtwork; private set => Set(ref _playerArtwork, value); }

    private double _playerPosition;
    public double PlayerPosition { get => _playerPosition; set => Set(ref _playerPosition, value); }

    private double _playerDuration = 30;
    public double PlayerDuration { get => _playerDuration; private set => Set(ref _playerDuration, value); }

    private double _volume = 80;
    public double Volume { get => _volume; set { if (Set(ref _volume, value)) _preview.Volume = value / 100.0; } }

    public bool IsPreviewPlaying => NowPlaying is not null;

    public string PlayerTime => $"{TimeSpan.FromSeconds(PlayerPosition):m\\:ss} / {TimeSpan.FromSeconds(PlayerDuration):m\\:ss}";

    public RelayCommand DownloadSelectedCommand { get; }
    public RelayCommand DownloadAllCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand ClearFinishedCommand { get; }
    public RelayCommand ClearHistoryCommand { get; }
    public RelayCommand ShowDownloadsCommand { get; }
    public RelayCommand ShowHistoryCommand { get; }
    public RelayCommand StopPreviewCommand { get; }
    public RelayCommand SearchClipboardCommand { get; }
    public RelayCommand DismissClipboardCommand { get; }

    public string OutputDirectory => _config.OutputDirectory;
    public int ActiveDownloads => DownloadQueue.Count(d => d.IsActive);

    public event Action? SettingsRequested;

    public MainViewModel(
            ProviderRegistry providers,
            DownloadManager downloads,
            PreviewPlayer preview,
            AppConfig config,
            AppState state,
            SearchResultCache cache,
            ProviderHealthMonitor health,
            IDispatcher ui,
            IArtworkLoader artwork)
        {
            _providers = providers;
            _downloads = downloads;
            _preview = preview;
            _config = config;
            _state = state;
            _cache = cache;
            _health = health;
            _ui = ui;
            _artwork = artwork;

        foreach (var r in state.RecentSearches.Take(8)) RecentSearches.Add(r);
        foreach (var h in state.History.Take(100)) History.Add(new HistoryItemViewModel
        {
            Title = h.Title, Artist = h.Artist, FilePath = h.FilePath, Provider = h.Provider, At = h.At,
        });
        Volume = 80;

        DownloadSelectedCommand = new RelayCommand(_ => { if (ResultsView.FirstOrDefault() is { } t) Download(t); });
        DownloadAllCommand = new RelayCommand(_ => DownloadAll(), _ => ResultsView.Count > 0);
        OpenSettingsCommand = new RelayCommand(_ => SettingsRequested?.Invoke());
        OpenFolderCommand = new RelayCommand(_ => OpenFolder());
        ClearFinishedCommand = new RelayCommand(_ =>
        {
            foreach (var d in DownloadQueue.Where(d => !d.IsActive).ToList())
                DownloadQueue.Remove(d);
        });
        ClearHistoryCommand = new RelayCommand(_ => { _state.ClearHistory(); History.Clear(); });
        ShowDownloadsCommand = new RelayCommand(_ => { ShowDownloads = true; ShowHistory = false; });
        ShowHistoryCommand = new RelayCommand(_ => { ShowDownloads = false; ShowHistory = true; });
        StopPreviewCommand = new RelayCommand(_ => StopPreview());
        SearchClipboardCommand = new RelayCommand(_ => { Query = ClipboardPill; ClipboardPill = ""; _ = SearchAsync(); });
        DismissClipboardCommand = new RelayCommand(_ => ClipboardPill = "");

        _downloads.JobAdded += job => _ui.Run(() =>
                        {
                            _jobIdentity[job.Id] = (job.Title, job.Artist);
                            DownloadQueue.Insert(0, new DownloadItemViewModel(job.Id, $"{job.Artist} — {job.Title}"));
                            OnPropertyChanged(nameof(ActiveDownloads));
                        });
                        _downloads.JobProgress += (id, p) => _ui.Run(() =>
                {
                    var item = DownloadQueue.FirstOrDefault(d => d.JobId == id);
                    if (item is null) return;
                    item.Apply(p, _jobProvider.TryGetValue(id, out var prov) ? prov : "");
                    if (p.Phase is DownloadPhase.Completed or DownloadPhase.AlreadyOwned
                        or DownloadPhase.Failed or DownloadPhase.Cancelled)
                    {
                        OnPropertyChanged(nameof(ActiveDownloads));
                        if (p.Phase == DownloadPhase.Completed)
                        {
                            if (_jobIdentity.TryGetValue(id, out var identity) && p.FilePath is { Length: > 0 })
                                RecordHistory(identity.Title, identity.Artist, p.FilePath,
                                    _jobProvider.TryGetValue(id, out var provName) ? provName : "MusicEngine");
                            if (_config.DownloadToasts)
                                PushToast(new ToastViewModel { Title = "Download complete", Message = item.Title, FilePath = p.FilePath });
                        }
                        else if (p.Phase is DownloadPhase.Failed or DownloadPhase.Cancelled)
                        {
                            // Allow retry: remove from queued works so user can re-download
                            if (_jobIdentity.TryGetValue(id, out var failedIdentity))
                            {
                                var key = $"{failedIdentity.Title}|{failedIdentity.Artist}".Trim();
                                _queuedWorks.Remove(key);
                            }
                            if (p.Phase == DownloadPhase.Failed && _config.DownloadToasts)
                                PushToast(new ToastViewModel { Title = "Download failed", Message = item.Title, IsError = true });
                        }
                    }
                });
                _downloads.JobStarted += (id, providerName) => _jobProvider[id] = providerName;

        _playerTimer = new System.Threading.Timer(_ => _ui.Run(UpdatePlayerPosition), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                _toastTimer = new System.Threading.Timer(_ => _ui.Run(AgeToasts), null, 1000, 1000);
                _clipboardTimer = new System.Threading.Timer(_ => _ui.Run(CheckClipboard), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                if (_config.ClipboardMonitor) _clipboardTimer.Change(1200, 1200);
            }

            public void SetClipboardMonitor(bool enabled)
            {
                if (enabled) _clipboardTimer.Change(1200, 1200);
                else { _clipboardTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite); ClipboardPill = ""; }
            }

    // ---------------- search ----------------

    public async Task SearchAsync(string? overrideQuery = null)
    {
        var query = (overrideQuery ?? Query)?.Trim();
        if (query.Length == 0) return;
        Query = query;

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        StopPreview();
        _state.PushSearch(query);
                var offline = _providers.OfflineSources;
                _ui.Run(() =>
                {
                    RecentSearches.Clear();
                    foreach (var r in _state.RecentSearches.Take(8)) RecentSearches.Add(r);
                    Results.Clear();
                    ResultsView.Clear();
                    HasResults = false;
                    IsSearching = true;
                    Status = offline.Count > 0
                        ? $"Searching “{query}”… · unreachable: {string.Join(", ", offline)}"
                        : $"Searching “{query}”…";
                });

                var search = new SearchService(
                    _providers.EnabledSearchProviders(),
                    _health, _cache, null, _config.SearchTimeoutSeconds);

                var started = Stopwatch.StartNew();
                var cb = new SearchService.Callbacks
                {
                    Status = s => _ui.Run(() => Status = s),
                    Batch = batch => _ui.Run(() => ApplyResults(batch)),
                };

                try
                {
                    var works = await search.RunAsync(query, cb, ct).ConfigureAwait(true);
                    _ui.Run(() =>
                    {
                        ApplyResults(works);
                        if (works.Count == 0)
                            Status = "No results — try another spelling, or check the proxy (YouTube needs it on filtered networks)";
                    });
                }
                catch (OperationCanceledException)
                {
                    _ui.Run(() => Status = "Search cancelled");
                }
                catch (Exception ex)
                {
                    _ui.Run(() => Status = $"Search error: {ex.Message}");
                }
                finally
                {
                    _ui.Run(() => IsSearching = false);
                }
    }

    /// <summary>
    /// Streaming merge: works arrive in successive snapshots as providers answer.
    /// Reuse existing row ViewModels (by title+artist) so preview state, artwork
    /// and library badges survive re-emits; rows never flicker.
    /// </summary>
    private void ApplyResults(IReadOnlyList<TrackWork> works)
    {
        var existing = Results.ToDictionary(
            t => TrackKey(t.Title, t.Artist), t => t, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var fresh = new List<TrackItemViewModel>();
        foreach (var work in works)
        {
            var key = TrackKey(work.Title, work.Artist);
            if (!seen.Add(key)) continue;
            if (existing.TryGetValue(key, out var vm))
            {
                if (!ReferenceEquals(vm.Work, work)) vm.ReplaceWork(work);
                fresh.Add(vm);
                continue;
            }
            var item = new TrackItemViewModel
            {
                Work = work,
                IsInLibrary = _state.AlreadyOwned(work.Title, work.Artist),
            };
            fresh.Add(item);
            LoadArtwork(item);
        }
        Results.Clear();
        foreach (var i in fresh) Results.Add(i);
        HasResults = Results.Count > 0;
        RebuildResultsView();
    }

    private static string TrackKey(string title, string artist) =>
        $"{(title ?? "").Trim()}|{(artist ?? "").Trim()}";

    private void RebuildResultsView()
    {
        IEnumerable<TrackItemViewModel> items = Results;
        if (HideInLibrary) items = items.Where(t => !t.IsInLibrary);
        items = SortMode switch
        {
            ResultSort.Duration => items.OrderByDescending(t => t.DurationSeconds),
            ResultSort.Title => items.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase),
            _ => items, // pipeline order = relevance
        };
        ResultsView.Clear();
        foreach (var i in items) ResultsView.Add(i);
    }

    // ---------------- preview player ----------------

    public void TogglePreview(TrackItemViewModel track)
    {
        if (track.PreviewUrl.Length == 0) return;

        _preview.Toggle(track.PreviewUrl,
            onStarted: () =>
            {
                if (_playingTrack is { } previous) previous.IsPreviewPlaying = false;
                _playingTrack = track;
                track.IsPreviewPlaying = true;
                NowPlaying = track;
                PlayerTitle = track.Title;
                PlayerArtist = track.Artist;
                PlayerArtwork = track.Artwork;
                                OnPropertyChanged(nameof(IsPreviewPlaying));
                                _playerTimer.Change(250, 250);
                            },
                            onStopped: () =>
                            {
                                track.IsPreviewPlaying = false;
                                if (ReferenceEquals(_playingTrack, track))
                                {
                                    _playingTrack = null;
                                    NowPlaying = null;
                                    OnPropertyChanged(nameof(IsPreviewPlaying));
                                }
                                _playerTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                            });
    }

    public void SeekPreview(double seconds) => _preview.Seek(TimeSpan.FromSeconds(seconds));

    public void StopPreview()
        {
            _playingTrack?.Let(t => t.IsPreviewPlaying = false);
            _playingTrack = null;
            _preview.Stop();
            NowPlaying = null;
            OnPropertyChanged(nameof(IsPreviewPlaying));
            _playerTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        }

        private void UpdatePlayerPosition()
        {
            PlayerDuration = Math.Max(1, _preview.Duration.TotalSeconds);
            PlayerPosition = _preview.Position.TotalSeconds;
            OnPropertyChanged(nameof(PlayerTime));
        }

    // ---------------- downloads ----------------

    public void Download(TrackItemViewModel track)
    {
        var key = track.Work.Representative.DedupKey;
        if (!_queuedWorks.Add(key)) return;
        _ = _downloads.EnqueueAsync(track.Work);
        Status = $"Downloading “{track.Title}”…";
    }

    public void DownloadAll()
    {
        var queued = 0;
        foreach (var track in ResultsView.Where(t => !t.IsInLibrary).Take(30).ToList())
        {
            Download(track);
            queued++;
        }
        if (queued > 0) Status = $"Queued {queued} downloads";
    }

    public void CancelDownload(DownloadItemViewModel item) => _downloads.Cancel(item.JobId);

    public void CancelAll()
    {
        foreach (var item in DownloadQueue.Where(d => d.IsActive).ToList())
            _downloads.Cancel(item.JobId);
    }

    public void OpenDownload(DownloadItemViewModel item)
    {
        if (item.FilePath is { Length: > 0 } && File.Exists(item.FilePath))
            Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true });
    }

    public void OpenDownloadFolder(DownloadItemViewModel item)
    {
        if (item.FilePath is { Length: > 0 })
        {
            var dir = Path.GetDirectoryName(item.FilePath);
            if (Directory.Exists(dir))
                Process.Start(new ProcessStartInfo(dir!) { UseShellExecute = true });
        }
    }

    public void OpenHistoryItem(HistoryItemViewModel item)
    {
        if (File.Exists(item.FilePath))
            Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true });
    }

    private void OpenFolder()
    {
        Directory.CreateDirectory(_config.OutputDirectory);
        Process.Start(new ProcessStartInfo(_config.OutputDirectory) { UseShellExecute = true });
    }

    /// <summary>Record a finished download into the persisted history and flag matching results.</summary>
            public void RecordHistory(string title, string artist, string filePath, string provider)
            {
                _state.PushHistory(new HistoryEntry(title, artist, filePath, provider, DateTimeOffset.Now));
                _ui.Run(() =>
                {
                    History.Insert(0, new HistoryItemViewModel
                    {
                        Title = title, Artist = artist, FilePath = filePath, Provider = provider, At = DateTimeOffset.Now,
                    });
                    // Match by stable DedupKey instead of fragile title/artist strings
                    var key = $"{title}|{artist}".Trim();
                    var match = Results.FirstOrDefault(r => r.Work.Representative.DedupKey == key);
                    if (match is not null)
                    {
                        match.IsInLibrary = true;
                        match.OnPropertyChanged(nameof(match.LibraryBadge));
                        if (HideInLibrary) RebuildResultsView();
                    }
                });
            }

    // ---------------- toasts ----------------

    private void PushToast(ToastViewModel toast)
    {
        Toasts.Add(toast);
        if (Toasts.Count > 4) Toasts.RemoveAt(0);
    }

    private void AgeToasts()
    {
        foreach (var t in Toasts.ToList())
        {
            var age = DateTime.UtcNow - t.CreatedAt;
            if (age.TotalSeconds > 4.2 && !t.Closing) t.Closing = true;
            else if (t.Closing) Toasts.Remove(t);
        }
    }

    public void DismissToast(ToastViewModel toast) => Toasts.Remove(toast);

    public void OpenToastFile(ToastViewModel toast)
    {
        if (toast.FilePath is { Length: > 0 } && File.Exists(toast.FilePath))
            Process.Start(new ProcessStartInfo(toast.FilePath) { UseShellExecute = true });
    }

    // ---------------- clipboard monitor ----------------

    public void CheckClipboard()
    {
        if (!_config.ClipboardMonitor) return;
        try
        {
            if (!System.Windows.Clipboard.ContainsText()) return;
            var text = System.Windows.Clipboard.GetText()?.Trim() ?? "";
            if (text.Length == 0 || text == _lastClipboard || text.Length > 300) return;
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
            if (uri.Host.Contains("spotify.") || uri.Host.Contains("youtube.") || uri.Host.Contains("youtu.be")
                || uri.Host.Contains("soundcloud.") || uri.Host.Contains("music.apple."))
            {
                _lastClipboard = text;
                ClipboardPill = text;
            }
        }
        catch { /* clipboard locked by another process */ }
    }

    // ---------------- settings ----------------

    public void ApplyAccent(string accent)
    {
        _config.Accent = accent;
        _config.Save();
        AccentTheme.Apply(accent);
    }

    /// <summary>Apply + persist a batch of settings changes from the dialog.</summary>
    public void SaveSettings(Action<AppConfig> mutate)
    {
        mutate(_config);
        _config.Save();
        OnPropertyChanged(nameof(OutputDirectory));
        Status = "Settings saved. Restart the app for proxy/tool/source changes to fully apply.";
    }

    private void LoadArtwork(TrackItemViewModel item)
        {
            var uri = item.Work.Representative.Metadata.ArtworkUri;
            if (uri is null) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    var bytes = await _artwork.LoadAsync(uri).ConfigureAwait(false);
                    if (bytes is null || bytes.Length == 0) return;
                    _ui.Run(() =>
                    {
                        var bmp = BitmapImageFromBytes(bytes);
                        item.Artwork = bmp;
                        if (ReferenceEquals(item, NowPlaying)) PlayerArtwork = bmp;
                    });
                }
                catch { /* placeholder stays */ }
            });
        }

        private BitmapImage BitmapImageFromBytes(byte[] bytes)
        {
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 128;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        public void Dispose()
        {
            // A search in flight may race the shutdown — dispose-vs-cancel is benign.
            try { _searchCts?.Cancel(); } catch (ObjectDisposedException) { }
            try { _searchCts?.Dispose(); } catch (ObjectDisposedException) { }
            StopPreview();
            _toastTimer.Dispose();
            _clipboardTimer.Dispose();
            _playerTimer.Dispose();
        }
}

internal static class FunctionalExtensions
{
    public static T Let<T>(this T self, Action<T> block) where T : class
    {
        block(self);
        return self;
    }
}
