namespace MusicEngine.App.Ui;

using System.Collections.ObjectModel;
using System.Windows.Threading;
using ViewModels;

/// <summary>
/// Toast notifications: owns the collection, the cap and the expiry timer
/// (MVVM-06). A <see cref="DispatcherTimer"/> delivers expiry on the UI thread
/// directly — no marshalling hop, no disposal race.
/// </summary>
public sealed class ToastService : IDisposable
{
    private readonly DispatcherTimer _timer;

    public ObservableCollection<ToastViewModel> Toasts { get; } = new();

    public ToastService()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => AgeToasts();
        _timer.Start();
    }

    public void Show(ToastViewModel toast)
    {
        Toasts.Add(toast);
        if (Toasts.Count > 4) Toasts.RemoveAt(0);
    }

    public void Dismiss(ToastViewModel toast) => Toasts.Remove(toast);

    private void AgeToasts()
    {
        foreach (var t in Toasts.ToList())
        {
            var age = DateTime.UtcNow - t.CreatedAt;
            if (age.TotalSeconds > 4.2 && !t.Closing) t.Closing = true;
            else if (t.Closing) Toasts.Remove(t);
        }
    }

    public void Dispose() => _timer.Stop();
}
