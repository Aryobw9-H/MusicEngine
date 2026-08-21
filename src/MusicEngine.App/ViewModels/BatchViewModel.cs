namespace MusicEngine.App.ViewModels;

using System.Collections.ObjectModel;
using Http;
using Models;
using Providers;
using Search;
using Ui;

/// <summary>One resolved line in the batch dialog (FEAT-06).</summary>
public sealed class BatchItemViewModel : ViewModelBase
{
    private bool _isSelected;

    public required string Query { get; init; }

    /// <summary>"Artist — Title (Source)" when matched, a plain note when not.</summary>
    public required string MatchLabel { get; init; }

    /// <summary>Null when the query resolved to nothing — always unselected then.</summary>
    public TrackWork? Work { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}

/// <summary>
/// Batch queue from a pasted list (FEAT-06): one query per line, each resolved
/// through the normal <see cref="SearchService"/> pipeline (so pasted URLs work
/// too) with a per-query timeout, then the top match per line is queued through
/// the same deduped path as a single download.
/// </summary>
public sealed class BatchViewModel : ViewModelBase
{
    private readonly IReadOnlyList<ISearchProvider> _providers;
    private readonly ProviderHealthMonitor _health;
    private readonly SearchResultCache _cache;
    private readonly ProviderResponseCache _providerCache;
    private readonly SharedHttpClient _http;
    private readonly int _timeoutSeconds;
    private readonly Action<TrackWork> _enqueue;
    private readonly IDispatcher _ui;
    private CancellationTokenSource? _cts;

    public ObservableCollection<BatchItemViewModel> Items { get; } = new();

    private string _queriesText = "";
    public string QueriesText { get => _queriesText; set => Set(ref _queriesText, value); }

    private bool _isResolving;
    public bool IsResolving
    {
        get => _isResolving;
        set { if (Set(ref _isResolving, value)) OnPropertyChanged(nameof(CanEdit)); }
    }

    /// <summary>Block editing while resolving; the text box binds this.</summary>
    public bool CanEdit => !IsResolving;

    private string _progress = "";
    public string Progress { get => _progress; set => Set(ref _progress, value); }

    public RelayCommand ResolveCommand { get; }
    public RelayCommand CancelResolveCommand { get; }
    public RelayCommand QueueSelectedCommand { get; }

    public BatchViewModel(
        IReadOnlyList<ISearchProvider> providers,
        ProviderHealthMonitor health,
        SearchResultCache cache,
        ProviderResponseCache providerCache,
        SharedHttpClient http,
        int searchTimeoutSeconds,
        Action<TrackWork> enqueue,
        IDispatcher ui)
    {
        _providers = providers;
        _health = health;
        _cache = cache;
        _providerCache = providerCache;
        _http = http;
        _timeoutSeconds = Math.Max(searchTimeoutSeconds, 20); // per-query budget (FEAT-06)
        _enqueue = enqueue;
        _ui = ui;

        ResolveCommand = new RelayCommand(_ => _ = ResolveAsync(), _ => !IsResolving);
        CancelResolveCommand = new RelayCommand(_ => _cts?.Cancel());
        QueueSelectedCommand = new RelayCommand(_ =>
        {
            var selected = Items.Where(i => i.IsSelected && i.Work is not null).Select(i => i.Work!).ToList();
            if (selected.Count == 0) return;
            foreach (var work in selected) _enqueue(work);
            Progress = $"Queued {selected.Count} download{(selected.Count == 1 ? "" : "s")} — check the Downloads tab";
        }, _ => !IsResolving && Items.Any(i => i.IsSelected && i.Work is not null));
    }

    private async Task ResolveAsync()
    {
        var lines = QueriesText.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (lines.Count == 0)
        {
            Progress = "Nothing to resolve — paste one query per line first";
            return;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        var cts = _cts = new CancellationTokenSource();

        Items.Clear();
        IsResolving = true;
        Progress = $"Resolving 0 of {lines.Count}…";

        var search = new SearchService(
            _providers, _health, _cache, _providerCache,
            null /*gate*/, null /*logger*/, _timeoutSeconds, _http);

        var done = 0;
        try
        {
            foreach (var line in lines)
            {
                if (cts.IsCancellationRequested) break;
                done++;
                _ui.Run(() => Progress = $"Resolving {done} of {lines.Count}…");
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));
                    var works = await search.RunAsync(line, SearchService.Callbacks.None, timeout.Token);
                    var top = works.FirstOrDefault();
                    _ui.Run(() => Items.Add(new BatchItemViewModel
                    {
                        Query = line,
                        MatchLabel = top is null
                            ? "No match — will be skipped"
                            : $"{top.Artist} — {top.Title} ({top.Representative.Provider})",
                        Work = top,
                        IsSelected = top is not null,
                    }));
                }
                catch (OperationCanceledException)
                {
                    _ui.Run(() => Items.Add(new BatchItemViewModel
                    {
                        Query = line, MatchLabel = "Timed out — will be skipped", IsSelected = false,
                    }));
                }
                catch (Exception ex)
                {
                    _ui.Run(() => Items.Add(new BatchItemViewModel
                    {
                        Query = line, MatchLabel = $"Failed — {ex.Message}", IsSelected = false,
                    }));
                }
            }
        }
        finally
        {
            IsResolving = false;
            _ui.Run(() => Progress = $"Done — {Items.Count(i => i.IsSelected)} of {Items.Count} matched");
        }
    }
}
