namespace MusicEngine.App;

using System.Windows.Media;

/// <summary>
/// Plays 30-second catalog previews (iTunes m4a / Deezer mp3). One player, one
/// stream at a time. Exposes position/duration/volume for the now-playing bar
/// and raises state changes for play/pause UI.
/// </summary>
public sealed class PreviewPlayer : IDisposable
{
    private MediaPlayer? _player;
    private string? _currentUrl;
    private bool _suppressEnd; // stop() called explicitly — don't raise Ended

    /// <summary>Raised whenever playback state, position, or duration changes materially.</summary>
    public event Action? Changed;

    public bool IsPlaying => _player is not null;
    public string? CurrentUrl => _currentUrl;

    public TimeSpan Position => _player?.Position ?? TimeSpan.Zero;
    public TimeSpan Duration => _player is { } p && p.NaturalDuration.HasTimeSpan
        ? p.NaturalDuration.TimeSpan
        : TimeSpan.Zero;

    public double Volume
    {
        get => _player?.Volume ?? 0.8;
        set { if (_player is not null) _player.Volume = Math.Clamp(value, 0, 1); }
    }

    public void Toggle(string url, Action onStarted, Action onStopped)
    {
        if (_player is not null && _currentUrl == url)
        {
            _suppressEnd = true;
            StopInternal();
            onStopped();
            return;
        }

        StopInternal();

        var player = new MediaPlayer { Volume = 0.8 };
        _player = player;
        _currentUrl = url;
        _suppressEnd = false;

        player.MediaOpened += (_, _) =>
        {
            player.Play();
            Changed?.Invoke();
        };
        player.MediaEnded += (_, _) =>
        {
            var suppressed = _suppressEnd;
            StopInternal();
            if (!suppressed) onStopped();
            Changed?.Invoke();
        };
        player.MediaFailed += (_, _) =>
        {
            StopInternal();
            onStopped();
            Changed?.Invoke();
        };
        player.Open(new Uri(url, UriKind.Absolute));
        onStarted();
        Changed?.Invoke();
    }

    public void Seek(TimeSpan position)
    {
        if (_player is null) return;
        _player.Position = position;
        Changed?.Invoke();
    }

    public void Stop()
    {
        _suppressEnd = true;
        StopInternal();
        Changed?.Invoke();
    }

    private void StopInternal()
    {
        if (_player is null) return;
        try
        {
            _player.Stop();
            _player.Close();
        }
        catch { /* already closed */ }
        _player = null;
        _currentUrl = null;
    }

    public void Dispose() => StopInternal();
}
