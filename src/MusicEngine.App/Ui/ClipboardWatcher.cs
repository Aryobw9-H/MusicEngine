namespace MusicEngine.App.Ui;

using System.Windows.Threading;

/// <summary>
/// Polls the clipboard for music links (Spotify / YouTube / SoundCloud /
/// Apple Music) on a <see cref="DispatcherTimer"/> — clipboard access requires
/// the STA thread, so the UI-thread timer avoids both the thread hop and the
/// teardown race of the old System.Threading.Timer (MVVM-06/07). Raises
/// <see cref="UrlDetected"/> once per distinct URL; the ViewModel owns the pill UI.
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    private readonly DispatcherTimer _timer;
    private string _last = "";

    public ClipboardWatcher()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _timer.Tick += (_, _) => Check();
    }

    /// <summary>True while actively polling.</summary>
    public bool Enabled { get; private set; }

    public event Action<string>? UrlDetected;

    public void Start()
    {
        Enabled = true;
        _timer.Start();
    }

    public void Stop()
    {
        Enabled = false;
        _timer.Stop();
    }

    private void Check()
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsText()) return;
            var text = System.Windows.Clipboard.GetText()?.Trim() ?? "";
            if (text.Length == 0 || text == _last || text.Length > 300) return;
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
            if (uri.Host.Contains("spotify.") || uri.Host.Contains("youtube.") || uri.Host.Contains("youtu.be")
                || uri.Host.Contains("soundcloud.") || uri.Host.Contains("music.apple."))
            {
                _last = text;
                UrlDetected?.Invoke(text);
            }
        }
        catch { /* clipboard locked by another process */ }
    }

    public void Dispose() => _timer.Stop();
}
