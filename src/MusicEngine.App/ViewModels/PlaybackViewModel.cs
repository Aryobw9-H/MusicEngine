namespace MusicEngine.App.ViewModels;

using System.Windows.Media.Imaging;
using System.Windows.Threading;

/// <summary>
/// Preview playback (MVVM-06): owns the <see cref="PreviewPlayer"/>, the 250 ms
/// position timer and the now-playing bar state (title / artist / artwork /
/// position / duration / volume). The position timer is a
/// <see cref="DispatcherTimer"/> — UI-thread delivery, no marshalling hop.
/// </summary>
public sealed class PlaybackViewModel : ViewModelBase, IDisposable
{
    private readonly PreviewPlayer _preview;
    private readonly DispatcherTimer _timer;
    private TrackItemViewModel? _playingTrack;

    private TrackItemViewModel? _nowPlaying;
    public TrackItemViewModel? NowPlaying { get => _nowPlaying; private set => Set(ref _nowPlaying, value); }

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
    public double Volume
    {
        get => _volume;
        set
        {
            if (Set(ref _volume, value)) _preview.Volume = value / 100.0;
        }
    }

    public bool IsPreviewPlaying => NowPlaying is not null;

    public string PlayerTime => $"{TimeSpan.FromSeconds(PlayerPosition):m\\:ss} / {TimeSpan.FromSeconds(PlayerDuration):m\\:ss}";

    /// <summary>Raised when the current stream fails to open (status line).</summary>
    public event Action<string>? PreviewFailed;

    public PlaybackViewModel(PreviewPlayer preview)
    {
        _preview = preview;
        _preview.Failed += message => PreviewFailed?.Invoke(message);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => UpdatePlayerPosition();
        Volume = 80;
    }

    public void Toggle(TrackItemViewModel track)
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
                _timer.Start();
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
                _timer.Stop();
            });
    }

    public void Seek(double seconds) => _preview.Seek(TimeSpan.FromSeconds(seconds));

    public void Stop()
    {
        _playingTrack?.Let(t => t.IsPreviewPlaying = false);
        _playingTrack = null;
        _preview.Stop();
        NowPlaying = null;
        OnPropertyChanged(nameof(IsPreviewPlaying));
        _timer.Stop();
    }

    private void UpdatePlayerPosition()
    {
        PlayerDuration = Math.Max(1, _preview.Duration.TotalSeconds);
        PlayerPosition = _preview.Position.TotalSeconds;
        OnPropertyChanged(nameof(PlayerTime));
    }

    /// <summary>Update the now-playing artwork if <paramref name="item"/> is the
    /// currently playing track (artwork loads finish asynchronously).</summary>
    internal void SetNowPlayingArtwork(TrackItemViewModel item, BitmapImage? bmp)
    {
        if (ReferenceEquals(item, NowPlaying)) PlayerArtwork = bmp;
    }

    public void Dispose() => _timer.Stop();
}
