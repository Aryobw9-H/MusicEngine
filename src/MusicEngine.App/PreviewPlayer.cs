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
    private int _generation;   // bumped on every Toggle; stale player callbacks bail out (BUG-07)

    /// <summary>Raised whenever playback state, position, or duration changes materially.</summary>
    public event Action? Changed;

    /// <summary>Raised when the current stream fails to open, with the error message.</summary>
    public event Action<string>? Failed;

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
        var gen = ++_generation;

        var player = new MediaPlayer { Volume = 0.8 };
        _player = player;
        _currentUrl = url;
        _suppressEnd = false;

        player.MediaOpened += (_, _) =>
        {
            if (gen != _generation) return; // a newer preview replaced this one
            player.Play();
            Changed?.Invoke();
        };
        player.MediaEnded += (_, _) =>
        {
            if (gen != _generation) return;
            var suppressed = _suppressEnd;
            StopInternal(player);
            if (!suppressed) onStopped();
            Changed?.Invoke();
        };
        player.MediaFailed += (_, ex) =>
        {
            if (gen != _generation) return;
            StopInternal(player);
            onStopped();
            Failed?.Invoke(ex?.ErrorException?.Message ?? "Preview failed to load.");
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

    /// <summary>
    /// Stops the current player. When <paramref name="expected"/> is supplied,
    /// only stops if it is still the live player — a stale callback cannot kill
    /// the newer preview (BUG-07).
    /// </summary>
    private void StopInternal(MediaPlayer? expected = null)
    {
        if (_player is null) return;
        if (expected is not null && !ReferenceEquals(expected, _player)) return;
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
