namespace MusicEngine.App.ViewModels;

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using Audio;
using Configuration;
using Downloads;
using Http;
using Models;
using Providers;
using Search;
using Ui;

/// <summary>
/// Coordinates search, preview playback, downloads, history and toasts with the
/// UI thread. The heavy lifting lives in extracted collaborators (MVVM-06):
/// <see cref="Playback"/> (preview), <see cref="DownloadQueue"/> (queue +
/// dedup), <see cref="ToastService"/> (toasts) and <see cref="ClipboardWatcher"/>
/// (clipboard polling). This class composes them and owns search + history.
/// </summary>
public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly ProviderRegistry _providers;
    private readonly YtDlpProvider _ytDlp;
    private readonly PersianIndexProvider _persianIndex;
    private readonly LibraryIndex _library;
    private readonly DownloadManager _downloads;
    private readonly AppConfig _config;
    private readonly AppState _state;
    private readonly SearchResultCache _cache;
    private readonly ProviderResponseCache _providerCache;
    private readonly ProviderHealthMonitor _health;
    private readonly IDispatcher _ui;
    private readonly IArtworkLoader _artwork;
    private readonly SharedHttpClient _http;
    private readonly ToastService _toasts;
    private readonly ClipboardWatcher _clipboard;

    /// <summary>The DI AppConfig singleton — the single source of truth (BUG-10).</summary>
    public AppConfig Config => _config;

    /// <summary>True when the app should hide to tray instead of exiting.</summary>
    public bool MinimizeToTray => _config.MinimizeToTray;

    /// <summary>Preview playback state (position, duration, now-playing bar).</summary>
    public PlaybackViewModel Playback { get; }

    /// <summary>Download queue rows and job bookkeeping.</summary>
    public DownloadQueueViewModel DownloadQueue { get; }

    private CancellationTokenSource? _searchCts;

    public ObservableCollection<TrackItemViewModel> Results { get; } = new();

    /// <summary>
    /// Live filtered/sorted view over <see cref="Results"/> (MVVM-04): the bound
    /// list is never cleared and refilled per batch, so selection and scroll
    /// position survive streaming updates. Configured by <see cref="RebuildResultsView"/>.
    /// </summary>
    public ICollectionView ResultsView { get; }

    public ObservableCollection<HistoryItemViewModel> History { get; } = new();

    /// <summary>Passthrough for XAML binding compatibility.</summary>
    public ObservableCollection<ToastViewModel> Toasts => _toasts.Toasts;

    public ObservableCollection<string> RecentSearches { get; } = new();

    /// <summary>Per-provider status chips beneath the search bar (PERF-07).</summary>
    public ObservableCollection<ProviderStatusViewModel> ProviderStatuses { get; } = new();

    /// <summary>Chip strip shows while searching, or whenever a source is offline.</summary>
    public bool ShowProviderStatuses =>
        IsSearching || ProviderStatuses.Any(c => c.State == Search.ProviderState.Offline);

    public int ActiveDownloads => DownloadQueue.ActiveDownloads;

    private readonly Dictionary<ProviderId, ProviderStatusViewModel> _providerChips = new();

    private string _query = "";
    public string Query { get => _query; set => Set(ref _query, value); }

    // Albums toggle: forces album-mode search (whole album per query, e.g.
    // "tataloo jahanam") instead of the automatic song/album guessing.
    private bool _albumsOnly;
    public bool AlbumsOnly { get => _albumsOnly; set => Set(ref _albumsOnly, value); }

    private bool _isSearching;
    public bool IsSearching
    {
        get => _isSearching;
        set
        {
            if (Set(ref _isSearching, value))
            {
                OnPropertyChanged(nameof(ShowEmptyState));
                OnPropertyChanged(nameof(ShowProviderStatuses));
            }
        }
    }

    private string _status = "Ready — type a song name (Finglish, Persian, or paste a link) and press Enter";
    public string Status { get => _status; set => Set(ref _status, value); }

    private bool _hasResults;
    public bool HasResults
    {
        get => _hasResults;
        set
        {
            if (Set(ref _hasResults, value))
            {
                OnPropertyChanged(nameof(ShowEmptyState));
                OnPropertyChanged(nameof(ResultsCountLabel));
            }
        }
    }

    /// <summary>"(12)" style result counter for the results toolbar.</summary>
    public string ResultsCountLabel
    {
        get
        {
            var count = ResultsView.Cast<TrackItemViewModel>().Count();
            return count > 0 ? $"({count})" : "";
        }
    }

    /// <summary>True only when there is nothing to show and nothing is loading.</summary>
    public bool ShowEmptyState => !HasResults && !IsSearching;

    /// <summary>Sort choices for the results toolbar ComboBox (enum names are the labels).</summary>
    public IReadOnlyList<ResultSort> SortOptions { get; } = Enum.GetValues<ResultSort>();

    /// <summary>Re-check every visible row against the on-disk index (FEAT-03).</summary>
    private void ReevaluateLibraryBadges()
    {
        foreach (var t in Results)
            t.IsInLibrary = _library.Contains(t.Artist, t.Title) || _state.AlreadyOwned(t.Title, t.Artist);
    }

    /// <summary>Two-way binding surface for the sort ComboBox.</summary>
    public ResultSort SelectedSort
    {
        get => SortMode;
        set => SortMode = value;
    }

    private ResultSort _sortMode = ResultSort.Relevance;
    public ResultSort SortMode
    {
        get => _sortMode;
        set
        {
            if (Set(ref _sortMode, value))
            {
                OnPropertyChanged(nameof(SelectedSort));
                RebuildResultsView();
            }
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
    public bool ShowDownloads
    {
        get => _showDownloads;
        set
        {
            if (Set(ref _showDownloads, value))
            {
                OnPropertyChanged(nameof(ShowDownloadsEmpty));
                OnPropertyChanged(nameof(ShowDownloadsIdle));
            }
        }
    }

    private bool _showHistory;
    public bool ShowHistory
    {
        get => _showHistory;
        set { if (Set(ref _showHistory, value)) OnPropertyChanged(nameof(ShowHistoryEmpty)); }
    }

    /// <summary>MVVM-10: empty-state overlays for the two tab lists.</summary>
    public bool HasDownloads => DownloadQueue.DownloadQueue.Count > 0;
    public bool HasHistory => History.Count > 0;
    public bool ShowDownloadsEmpty => ShowDownloads && !HasDownloads;
    public bool ShowHistoryEmpty => ShowHistory && !HasHistory;

    private string _clipboardPill = "";
    public string ClipboardPill { get => _clipboardPill; set => Set(ref _clipboardPill, value); }

    private bool _isRecentsOpen;
    public bool IsRecentsOpen { get => _isRecentsOpen; set => Set(ref _isRecentsOpen, value); }

    public RelayCommand SearchCommand { get; }
    public RelayCommand SearchRecentCommand { get; }
    public RelayCommand ToggleRecentsCommand { get; }
    public RelayCommand DownloadSelectedCommand { get; }
    public RelayCommand DownloadAllCommand { get; }
    public RelayCommand PauseDownloadCommand { get; }
    public RelayCommand ResumeDownloadCommand { get; }
    public RelayCommand RestartDownloadCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand OpenBatchCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand ClearFinishedCommand { get; }
    public RelayCommand ClearHistoryCommand { get; }
    public RelayCommand ShowDownloadsCommand { get; }
    public RelayCommand ShowHistoryCommand { get; }
    public RelayCommand StopPreviewCommand { get; }
    public RelayCommand SearchClipboardCommand { get; }
    public RelayCommand DismissClipboardCommand { get; }
    public RelayCommand ResumeQueueCommand { get; }

    /// <summary>FEAT-02: interrupted downloads from a previous session, offered not auto-run.</summary>
    private int _pendingResumeCount;
    public int PendingResumeCount
    {
        get => _pendingResumeCount;
        set
        {
            if (Set(ref _pendingResumeCount, value))
            {
                OnPropertyChanged(nameof(HasPendingResume));
                OnPropertyChanged(nameof(PendingResumeLabel));
                OnPropertyChanged(nameof(ShowDownloadsIdle));
            }
        }
    }

    public bool HasPendingResume => PendingResumeCount > 0;
    public string PendingResumeLabel => PendingResumeCount == 1
        ? "1 download was interrupted — resume it to continue"
        : $"{PendingResumeCount} downloads were interrupted — resume them to continue";

    /// <summary>The idle "no downloads" message must not fight the resume prompt.</summary>
    public bool ShowDownloadsIdle => ShowDownloadsEmpty && !HasPendingResume;

    public string OutputDirectory => _config.OutputDirectory;

    public event Action? SettingsRequested;

    /// <summary>FEAT-06: user asked for the batch dialog — the window hosts the prepared VM.</summary>
    public event Action<BatchViewModel>? BatchRequested;

    public MainViewModel(
        ProviderRegistry providers,
        YtDlpProvider ytDlp,
        PersianIndexProvider persianIndex,
        LibraryIndex library,
        DownloadManager downloads,
        PreviewPlayer preview,
        AppConfig config,
        AppState state,
        SearchResultCache cache,
        ProviderHealthMonitor health,
        ProviderResponseCache providerCache,
        IDispatcher ui,
        IArtworkLoader artwork,
        SharedHttpClient http,
        ToastService toasts,
        ClipboardWatcher clipboard,
        PlaybackViewModel playback,
        DownloadQueueViewModel queue)
    {
        _providers = providers;
        _ytDlp = ytDlp;
        _persianIndex = persianIndex;
        _library = library;
        _downloads = downloads;
        _config = config;
        _state = state;
        _cache = cache;
        _providerCache = providerCache;
        _health = health;
        _ui = ui;
        _artwork = artwork;
        _http = http;
        _toasts = toasts;
        _clipboard = clipboard;
        Playback = playback;
        DownloadQueue = queue;
        ResultsView = new ListCollectionView(Results);

        // Surface preview load failures in the status line instead of silently
        // resetting the now-playing bar (BUG-07).
        Playback.PreviewFailed += message => Status = $"Preview failed: {message}";

        // Clipboard: a detected music link raises the pill.
        _clipboard.UrlDetected += url => ClipboardPill = url;
        if (_config.ClipboardMonitor) _clipboard.Start();

        // Finished downloads: record history + mark the results row as owned.
        queue.JobCompleted += item =>
        {
            if (item.Work is { } work && item.FilePath is { Length: > 0 })
                RecordHistory(work.Title, work.Artist, work.Representative.DedupKey, item.FilePath,
                    queue.ProviderFor(item.JobId) ?? "MusicEngine");
        };
        queue.ActiveChanged += () => OnPropertyChanged(nameof(ActiveDownloads));
        // MVVM-10: empty-state overlays react to queue/history changes.
        DownloadQueue.DownloadQueue.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasDownloads));
            OnPropertyChanged(nameof(ShowDownloadsEmpty));
            OnPropertyChanged(nameof(ShowDownloadsIdle));
        };
        History.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasHistory));
            OnPropertyChanged(nameof(ShowHistoryEmpty));
        };
        // FEAT-03: files appearing/disappearing on disk re-evaluate visible badges.
        _library.Changed += () => _ui.Run(ReevaluateLibraryBadges);

        foreach (var r in state.RecentSearches.Take(8)) RecentSearches.Add(r);
        foreach (var h in state.History.Take(100)) History.Add(new HistoryItemViewModel
        {
            Title = h.Title, Artist = h.Artist, FilePath = h.FilePath, Provider = h.Provider, At = h.At,
        });

        DownloadSelectedCommand = new RelayCommand(_ => { if (ResultsView.Cast<TrackItemViewModel>().FirstOrDefault() is { } t) Download(t); });
        DownloadAllCommand = new RelayCommand(_ => DownloadAll(), _ => ResultsView.Cast<TrackItemViewModel>().Any());
        PauseDownloadCommand = new RelayCommand(p => { if (p is DownloadItemViewModel d) queue.Pause(d.JobId); });
        ResumeDownloadCommand = new RelayCommand(p => { if (p is DownloadItemViewModel d) queue.Resume(d.JobId); });
        RestartDownloadCommand = new RelayCommand(p => { if (p is DownloadItemViewModel d) queue.Restart(d); });
        SearchCommand = new RelayCommand(_ =>
        {
            IsRecentsOpen = false;
            try { _ = SearchAsync(); }
            catch (Exception ex)
            {
                Status = $"Search failed: {ex.Message}";
                CrashLog.Write("search", ex);
            }
        });
        SearchRecentCommand = new RelayCommand(q =>
        {
            if (q is not string query) return;
            Query = query;
            SearchCommand.Execute(null);
        });
        ToggleRecentsCommand = new RelayCommand(_ =>
            IsRecentsOpen = RecentSearches.Count > 0 && !IsRecentsOpen);
        OpenSettingsCommand = new RelayCommand(_ => SettingsRequested?.Invoke());
        OpenBatchCommand = new RelayCommand(_ => BatchRequested?.Invoke(CreateBatchViewModel()));
        OpenFolderCommand = new RelayCommand(_ => OpenFolder());
        ClearFinishedCommand = new RelayCommand(_ => queue.ClearFinished());
        ClearHistoryCommand = new RelayCommand(_ => { _state.ClearHistory(); History.Clear(); });
        ShowDownloadsCommand = new RelayCommand(_ => { ShowDownloads = true; ShowHistory = false; });
        ShowHistoryCommand = new RelayCommand(_ => { ShowDownloads = false; ShowHistory = true; });
        StopPreviewCommand = new RelayCommand(_ => Playback.Stop());
        SearchClipboardCommand = new RelayCommand(_ => { Query = ClipboardPill; ClipboardPill = ""; _ = SearchAsync(); });
        DismissClipboardCommand = new RelayCommand(_ => ClipboardPill = "");

        // FEAT-02: offer interrupted downloads instead of silently re-running them.
        var pending = downloads.LoadPendingJobs();
        if (pending.Count > 0)
        {
            PendingResumeCount = pending.Count;
            Status = $"{pending.Count} interrupted download{(pending.Count == 1 ? "" : "s")} — open the Downloads tab to resume.";
        }
        ResumeQueueCommand = new RelayCommand(_ =>
        {
            var jobs = _downloads.LoadPendingJobs();
            if (jobs.Count == 0) { PendingResumeCount = 0; Status = "Nothing to resume."; return; }
            try
            {
                _ = _downloads.ResumeAsync(jobs);
                PendingResumeCount = 0;
                Status = $"Resuming {jobs.Count} download{(jobs.Count == 1 ? "" : "s")}…";
            }
            catch (Exception ex)
            {
                Status = $"Resume failed: {ex.Message}";
                CrashLog.Write("resume", ex);
            }
        });
    }

    public void SetClipboardMonitor(bool enabled)
    {
        if (enabled) _clipboard.Start();
        else { _clipboard.Stop(); ClipboardPill = ""; }
    }

    // ---------------- batch queue (FEAT-06) ----------------

    /// <summary>Fresh dialog state each time — resolves never share cancellation state.</summary>
    private BatchViewModel CreateBatchViewModel() => new(
        _providers.EnabledSearchProviders(),
        _health,
        _cache,
        _providerCache,
        _http,
        _config.SearchTimeoutSeconds,
        work => DownloadQueue.Enqueue(work, work.Title),
        _ui);

    // ---------------- search ----------------

    public async Task SearchAsync(string? overrideQuery = null)
    {
        var query = (overrideQuery ?? Query)?.Trim();
        if (query is not { Length: > 0 }) return;
        Query = query;

        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        Playback.Stop();
        _state.PushSearch(query);
        var offline = _providers.OfflineSources;
        _ui.Run(() =>
        {
            RecentSearches.Clear();
            foreach (var r in _state.RecentSearches.Take(8)) RecentSearches.Add(r);
            Results.Clear(); // the bound view reflects this (MVVM-04)
            HasResults = false;
            IsSearching = true;
            // PERF-07: rebuild the provider chip strip for this search — pending
            // until the fan-out answers, offline if the route probe says so.
            ProviderStatuses.Clear();
            _providerChips.Clear();
            foreach (var p in _providers.EnabledSearchProviders())
            {
                var chip = new ProviderStatusViewModel(p.DisplayName,
                    offline.Contains(p.DisplayName) ? Search.ProviderState.Offline : Search.ProviderState.Pending);
                _providerChips[p.Id] = chip;
                ProviderStatuses.Add(chip);
            }
            OnPropertyChanged(nameof(ShowProviderStatuses));
            Status = offline.Count > 0
                ? $"Searching {(AlbumsOnly ? "album " : "")}“{query}”… · unreachable: {string.Join(", ", offline)}"
                : $"Searching {(AlbumsOnly ? "album " : "")}“{query}”…";
        });

        var search = new SearchService(
            _providers.EnabledSearchProviders(),
            _health, _cache, _providerCache, null /*gate*/, null /*logger*/,
            _config.SearchTimeoutSeconds, _http);

        var started = Stopwatch.StartNew();
        var cb = new SearchService.Callbacks
        {
            Status = s => _ui.Run(() => Status = s),
            Batch = batch => _ui.Run(() => ApplyResults(batch)),
            ProviderStatus = (id, state) => _ui.Run(() =>
            {
                if (_providerChips.TryGetValue(id, out var chip)
                    && chip.State != Search.ProviderState.Offline)
                {
                    chip.State = state;
                }
            }),
        };

        try
        {
            var works = await search.RunAsync(query, cb, ct, AlbumsOnly).ConfigureAwait(true);
            _ui.Run(() =>
            {
                ApplyResults(works);
                if (works.Count == 0)
                    Status = offline.Count > 0
                        ? $"{(AlbumsOnly ? "No albums" : "No results")} — offline sources: {string.Join(", ", offline)}. Check the proxy, then try again."
                        : AlbumsOnly
                            ? "No albums found — try \"artist album\" as the query, or check the proxy (YouTube needs it on filtered networks)"
                            : "No results — try another spelling, or check the proxy (YouTube needs it on filtered networks)";
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
                IsInLibrary = _library.Contains(work.Artist, work.Title) || _state.AlreadyOwned(work.Title, work.Artist),
            };
            fresh.Add(item);
            LoadArtwork(item);
        }

        // Incremental diff (MVVM-04): no Clear() during batch application, so the
        // bound view keeps selection and scroll position. Pipeline order is
        // authoritative; remove rows that vanished, move rows that reordered.
        var desiredKeys = fresh.Select(t => TrackKey(t.Title, t.Artist)).ToHashSet(StringComparer.Ordinal);
        for (var i = Results.Count - 1; i >= 0; i--)
        {
            if (!desiredKeys.Contains(TrackKey(Results[i].Title, Results[i].Artist)))
                Results.RemoveAt(i);
        }
        var index = 0;
        foreach (var item in fresh)
        {
            var currentPos = -1;
            for (var j = 0; j < Results.Count; j++)
                if (ReferenceEquals(Results[j], item)) { currentPos = j; break; }
            if (currentPos < 0)
                Results.Insert(index, item);
            else if (currentPos != index)
                Results.Move(currentPos, index);
            index++;
        }
        HasResults = Results.Count > 0;
        RebuildResultsView();
    }

    private static string TrackKey(string title, string artist) =>
        $"{(title ?? "").Trim()}|{(artist ?? "").Trim()}";

    /// <summary>Reconfigure the live view: filter (HideInLibrary) + sort (SortMode).</summary>
    private void RebuildResultsView()
    {
        if (ResultsView is ListCollectionView view)
        {
            view.Filter = HideInLibrary
                ? obj => obj is TrackItemViewModel t && !t.IsInLibrary
                : null;
            view.SortDescriptions.Clear();
            switch (SortMode)
            {
                case ResultSort.Duration:
                    view.SortDescriptions.Add(new SortDescription(nameof(TrackItemViewModel.DurationSeconds), ListSortDirection.Descending));
                    break;
                case ResultSort.Title:
                    view.SortDescriptions.Add(new SortDescription(nameof(TrackItemViewModel.Title), ListSortDirection.Ascending));
                    break;
            }
            view.Refresh();
        }
        OnPropertyChanged(nameof(ResultsCountLabel));
    }

    // ---------------- preview player ----------------

    public void TogglePreview(TrackItemViewModel track) => Playback.Toggle(track);
    public void SeekPreview(double seconds) => Playback.Seek(seconds);
    public void StopPreview() => Playback.Stop();

    // ---------------- downloads ----------------

    public void Download(TrackItemViewModel track)
    {
        if (!DownloadQueue.Enqueue(track.Work, track.Title)) return;
        Status = $"Downloading “{track.Title}”…";
    }

    public void DownloadAll()
    {
        var queued = 0;
        foreach (var track in ResultsView.Cast<TrackItemViewModel>().Where(t => !t.IsInLibrary).Take(30).ToList())
        {
            Download(track);
            queued++;
        }
        if (queued > 0) Status = $"Queued {queued} downloads";
    }

    public void CancelDownload(DownloadItemViewModel item) => DownloadQueue.Cancel(item.JobId);
    public void CancelAll() => DownloadQueue.CancelAll();

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

    /// <summary>FEAT-04: fully local, redacted diagnostics report for the Settings dialog's copy button.</summary>
    public string BuildDiagnosticsReport() =>
        Diagnostics.DiagnosticsBundle.Build(_config, _providers, _ytDlp, _persianIndex);

    /// <summary>Record a finished download into the persisted history and flag matching results.</summary>
    public void RecordHistory(string title, string artist, string dedupKey, string filePath, string provider)
    {
        // FEAT-03: the file is real on disk now — index it so the badge flips
        // immediately and survives restarts even if state.json is lost.
        _library.Add(filePath);
        _state.PushHistory(new HistoryEntry(title, artist, filePath, provider, DateTimeOffset.Now));
        _ui.Run(() =>
        {
            History.Insert(0, new HistoryItemViewModel
            {
                Title = title, Artist = artist, FilePath = filePath, Provider = provider, At = DateTimeOffset.Now,
            });
            // Match by the stable DedupKey (provider::id / url) — title/artist
            // strings differ across scripts and parsers, so string matching
            // never found the row and the "✓ In library" badge never showed.
            var match = Results.FirstOrDefault(r => r.Work.Representative.DedupKey == dedupKey);
            if (match is not null)
            {
                match.IsInLibrary = true; // setter raises LibraryBadge (MVVM-05)
                if (HideInLibrary) RebuildResultsView();
            }
        });
    }

    // ---------------- toasts ----------------

    public void DismissToast(ToastViewModel toast) => _toasts.Dismiss(toast);

    public void OpenToastFile(ToastViewModel toast)
    {
        if (toast.FilePath is { Length: > 0 } && File.Exists(toast.FilePath))
            Process.Start(new ProcessStartInfo(toast.FilePath) { UseShellExecute = true });
    }

    // ---------------- settings ----------------

    public void ApplyAccent(string accent)
    {
        _config.Accent = accent;
        _ = Task.Run(() => _config.Save()); // off the UI thread (PERF-01)
        AccentTheme.Apply(accent);
    }

    /// <summary>Apply + persist a batch of settings changes from the dialog.</summary>
    public void SaveSettings(Action<AppConfig> mutate)
    {
        mutate(_config);
        _ = Task.Run(() => _config.Save()); // off the UI thread (PERF-01)
        OnPropertyChanged(nameof(OutputDirectory));
        Status = "Settings saved. Restart the app for proxy/tool/source changes to fully apply.";
    }

    /// <summary>
    /// Decoded-artwork second-level cache (PERF-05): the loader caches bytes;
    /// this caches the frozen 128px BitmapImage so repeated rows skip the decode.
    /// Only touched on the UI thread (LoadArtwork and the _ui.Run callback), so
    /// no locking is needed. Cleared wholesale past 256 entries.
    /// </summary>
    private readonly Dictionary<string, System.Windows.Media.Imaging.BitmapImage> _decodedArtwork = new();
    private const int DecodedArtworkLimit = 256;

    private void LoadArtwork(TrackItemViewModel item)
    {
        var uri = item.Work.Representative.Metadata.ArtworkUri;
        if (uri is null) return;
        var key = uri.ToString();
        if (_decodedArtwork.TryGetValue(key, out var hit))
        {
            item.Artwork = hit;
            Playback.SetNowPlayingArtwork(item, hit);
            return;
        }
        // PERF-06: tie the fetch to the current search so a superseded search's
        // artwork downloads are cancelled instead of competing for the proxy.
        var ct = _searchCts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try
            {
                var bytes = await _artwork.LoadAsync(uri, ct).ConfigureAwait(false);
                if (bytes is null || bytes.Length == 0) return;
                if (ct.IsCancellationRequested) return;
                _ui.Run(() =>
                {
                    if (_decodedArtwork.TryGetValue(key, out var bmp))
                    {
                        item.Artwork = bmp;
                        Playback.SetNowPlayingArtwork(item, bmp);
                        return;
                    }
                    bmp = BitmapImageFromBytes(bytes);
                    if (_decodedArtwork.Count >= DecodedArtworkLimit) _decodedArtwork.Clear();
                    _decodedArtwork[key] = bmp;
                    item.Artwork = bmp;
                    Playback.SetNowPlayingArtwork(item, bmp);
                });
            }
            catch (OperationCanceledException) { /* superseded search — drop silently */ }
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
        Playback.Dispose();
        _toasts.Dispose();
        _clipboard.Dispose();
        DownloadQueue.Dispose();
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
