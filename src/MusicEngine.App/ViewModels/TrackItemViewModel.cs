namespace MusicEngine.App.ViewModels;

using System.Windows.Media.Imaging;
using Models;

/// <summary>One row in the results list — wraps a TrackWork.</summary>
public sealed class TrackItemViewModel : ViewModelBase
{
    private BitmapImage? _artwork;
    private bool _isPreviewPlaying;

    public required TrackWork Work { get; set; }

    /// <summary>
    /// Streaming re-emit: swap in a newer snapshot of the same work (same song,
    /// more download versions attached) and refresh every computed binding.
    /// </summary>
    public void ReplaceWork(TrackWork work)
    {
        Work = work;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Artist));
        OnPropertyChanged(nameof(Album));
        OnPropertyChanged(nameof(AlbumBadge));
        OnPropertyChanged(nameof(IsAlbumRow));
        OnPropertyChanged(nameof(TrackNumber));
        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(DurationSeconds));
        OnPropertyChanged(nameof(PreviewUrl));
        OnPropertyChanged(nameof(QualityLabel));
        OnPropertyChanged(nameof(SourcesLabel));
        OnPropertyChanged(nameof(HasPreview));
    }

    public string Title => IsAlbumRow && TrackNumber > 0
        ? $"{TrackNumber:00}. {Work.Representative.Metadata.Title}"
        : Work.Representative.Metadata.Title;

    public string Artist => Work.Representative.Metadata.Artist;

    /// <summary>Album goal name — non-null only when the search was an album query.</summary>
    public string AlbumBadge => Work.Goal.Album ?? "";

    /// <summary>True only in album mode, so normal results don't grow album chips.</summary>
    public bool IsAlbumRow => AlbumBadge.Length > 0;

    /// <summary>Position within the album, when the source provides it.</summary>
    public int TrackNumber => Work.Representative.Metadata.TrackNumber ?? 0;
    public string Album => Work.Representative.Metadata.Album ?? "";
    public double DurationSeconds => Work.Representative.Metadata.Duration?.TotalSeconds ?? 0;

    public string Duration => DurationSeconds > 0
        ? TimeSpan.FromSeconds(DurationSeconds).ToString(@"m\:ss")
        : "–:––";

    public string PreviewUrl => Work.Representative.DirectStreamUri?.OriginalString ?? "";

    /// <summary>Quality label: "320k" for direct Iranian MP3s, "HQ" for streams, "preview" for catalogs.</summary>
    public string QualityLabel => Work.Representative.PreviewOnly ? "preview"
        : Work.Representative.MaxQuality switch
        {
            StreamQuality.Maximum256K => "320k",
            StreamQuality.High192K => "HQ",
            _ => "128k",
        };

    public string SourcesLabel
    {
        get
        {
            var copies = Work.DownloadableVersions.Count();
            var prov = Work.Representative.Provider.ToString();
            return copies switch { 0 => prov, 1 => $"{prov} • 1 copy", _ => $"{prov} • {copies} copies" };
        }
    }

    private bool _isInLibrary;
    public bool IsInLibrary
    {
        get => _isInLibrary;
        set
        {
            if (Set(ref _isInLibrary, value)) OnPropertyChanged(nameof(LibraryBadge));
        }
    }

    public string LibraryBadge => IsInLibrary ? "✓ In library" : "";

    public bool HasPreview => PreviewUrl.Length > 0;

    public bool IsPreviewPlaying
    {
        get => _isPreviewPlaying;
        set => Set(ref _isPreviewPlaying, value);
    }

    public BitmapImage? Artwork
    {
        get => _artwork;
        set => Set(ref _artwork, value);
    }
}

/// <summary>Sort modes for the results list.</summary>
public enum ResultSort
{
    Relevance,
    Duration,
    Title,
}

/// <summary>One row in the downloads panel — computes speed/ETA from progress deltas.</summary>
public sealed class DownloadItemViewModel : ViewModelBase
{
    private DownloadPhase _phase = DownloadPhase.Queued;
    private int _percent;
    private string _status = "Queued";
    private string? _filePath;
    private bool _isIndeterminate = true;
    private string _speedText = "";
    private string _sizeText = "";
    private string _etaText = "";
    private long _lastBytes;
    private DateTime _lastAt = DateTime.UtcNow;

    public string JobId { get; }
    public string Title { get; }

    private string _provider = "";
    public string Provider
    {
        get => _provider;
        private set => Set(ref _provider, value);
    }

    private Models.TrackWork? _work;

    /// <summary>The original work, kept so Restart can re-enqueue it directly
    /// instead of reverse-engineering a lookup from the display title.</summary>
    public Models.TrackWork? Work
    {
        get => _work;
        set => Set(ref _work, value);
    }

    public DownloadItemViewModel(string jobId, string title)
    {
        JobId = jobId;
        Title = title;
    }

    public DownloadPhase Phase
    {
        get => _phase;
        private set
        {
            if (Set(ref _phase, value))
            {
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(IsFinished));
                OnPropertyChanged(nameof(IsFailed));
                OnPropertyChanged(nameof(IsPaused));
            }
        }
    }

    public int Percent
    {
        get => _percent;
        private set => Set(ref _percent, value);
    }

    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        private set => Set(ref _isIndeterminate, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string SpeedText
    {
        get => _speedText;
        private set => Set(ref _speedText, value);
    }

    public string SizeText
    {
        get => _sizeText;
        private set => Set(ref _sizeText, value);
    }

    public string EtaText
    {
        get => _etaText;
        private set => Set(ref _etaText, value);
    }

    public string? FilePath
    {
        get => _filePath;
        private set => Set(ref _filePath, value);
    }

    public bool IsActive => Phase is DownloadPhase.Queued or DownloadPhase.Resolving
            or DownloadPhase.Downloading or DownloadPhase.Tagging;

        public bool IsFinished => Phase is DownloadPhase.Completed or DownloadPhase.AlreadyOwned;
        public bool IsFailed => Phase is DownloadPhase.Failed or DownloadPhase.Cancelled;
        public bool IsPaused => Phase == DownloadPhase.Paused;

    public void Apply(DownloadProgress p, string provider)
    {
        Provider = provider;
        Phase = p.Phase;
        Status = p.Message ?? p.Phase.ToString();
        if (p.FilePath is { Length: > 0 }) FilePath = p.FilePath;

        if (p.Percent is { } pct)
        {
            Percent = pct;
            IsIndeterminate = false;
        }
        else
        {
            IsIndeterminate = p.Phase is DownloadPhase.Resolving or DownloadPhase.Queued or DownloadPhase.Tagging;
        }

        if (p.BytesTotal is long total && total > 0)
            SizeText = $"{HumanSize(p.BytesDone)} / {HumanSize(total)}";
        else if (p.BytesDone > 0)
            SizeText = HumanSize(p.BytesDone);
        else
            SizeText = "";

        // speed + ETA from byte deltas
        var now = DateTime.UtcNow;
        if (p.Phase == DownloadPhase.Downloading && p.BytesDone > _lastBytes && now > _lastAt)
        {
            var bytesPerSec = (p.BytesDone - _lastBytes) / (now - _lastAt).TotalSeconds;
            if (bytesPerSec > 0)
            {
                SpeedText = $"{HumanSize((long)bytesPerSec)}/s";
                if (p.BytesTotal is long t && t > 0)
                {
                    var remain = TimeSpan.FromSeconds((t - p.BytesDone) / bytesPerSec);
                    EtaText = remain.TotalSeconds > 1 ? $"{remain:m\\:ss} left" : "";
                }
            }
        }
        if (p.Phase is not DownloadPhase.Downloading)
        {
            SpeedText = "";
            EtaText = "";
        }
        _lastBytes = p.BytesDone;
        _lastAt = now;
    }

    private static string HumanSize(long bytes) => bytes switch
    {
        >= 1_048_576 => $"{bytes / 1024.0 / 1024.0:0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes} B",
    };
}

/// <summary>One history row (past downloads, persisted).</summary>
public sealed class HistoryItemViewModel
{
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public required string FilePath { get; init; }
    public required string Provider { get; init; }
    public required DateTimeOffset At { get; init; }

    public string Display => string.IsNullOrWhiteSpace(Artist) ? Title : $"{Artist} — {Title}";
    public string When => At.ToString("yyyy-MM-dd HH:mm");
}

/// <summary>
/// One provider chip in the search status strip (PERF-07): pending → responded
/// → timed out/failed as the fan-out answers, or offline from the route probe.
/// </summary>
public sealed class ProviderStatusViewModel : ViewModelBase
{
    private Search.ProviderState _state;

    public ProviderStatusViewModel(string name, Search.ProviderState state)
    {
        Name = name;
        _state = state;
    }

    public string Name { get; }

    public Search.ProviderState State
    {
        get => _state;
        set => Set(ref _state, value);
    }
}

/// <summary>One toast notification (auto-dismissed by the VM).</summary>
public sealed class ToastViewModel : ViewModelBase
{
    private bool _closing;

    public required string Title { get; init; }
    public required string Message { get; init; }
    public string? FilePath { get; init; }
    public bool IsError { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public bool Closing
    {
        get => _closing;
        set => Set(ref _closing, value);
    }
}
