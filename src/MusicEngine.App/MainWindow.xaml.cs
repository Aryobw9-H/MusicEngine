namespace MusicEngine.App;

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Configuration;
using ViewModels;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        vm.SettingsRequested += ShowSettings;
        vm.BatchRequested += ShowBatch;
    }

    // ---------------- search ----------------

    private void ClearQuery_Click(object sender, RoutedEventArgs e)
    {
        _vm.Query = "";
        SearchBox.Focus();
    }

    // ---------------- results ----------------

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TrackItemViewModel track)
            _vm.TogglePreview(track);
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TrackItemViewModel track)
            _vm.Download(track);
    }

    private void ResultsList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ResultsList.SelectedItem is not TrackItemViewModel track) return;
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                _vm.Download(track);
                break;
            case Key.Space:
                e.Handled = true;
                _vm.TogglePreview(track);
                break;
        }
    }

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedItem is TrackItemViewModel track)
            _vm.Download(track);
    }

    // ---------------- downloads / history ----------------

    private void CancelDownload_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadItemViewModel item)
            _vm.CancelDownload(item);
    }

    private void OpenDownload_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadItemViewModel item)
            _vm.OpenDownload(item);
    }

    private void OpenDownloadFolder_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadItemViewModel item)
            _vm.OpenDownloadFolder(item);
    }

    private void CancelAll_Click(object sender, RoutedEventArgs e) => _vm.CancelAll();

    private void OpenHistoryItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HistoryItemViewModel item)
            _vm.OpenHistoryItem(item);
    }

    private void TabDownloads_Click(object sender, RoutedEventArgs e) => SetTab(downloads: true);

    private void TabHistory_Click(object sender, RoutedEventArgs e) => SetTab(downloads: false);

    private void SetTab(bool downloads)
    {
        _vm.ShowDownloads = downloads;
        _vm.ShowHistory = !downloads;
        TabDownloads.Style = (Style)FindResource(downloads ? "BtnAccent" : "Btn");
        TabHistory.Style = (Style)FindResource(downloads ? "Btn" : "BtnAccent");
    }

    // ---------------- player ----------------

    private void Seek_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _vm.SeekPreview(SeekSlider.Value);
    }

    // ---------------- toasts ----------------

    private void Toast_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ToastViewModel toast)
        {
            _vm.OpenToastFile(toast);
            _vm.DismissToast(toast);
        }
    }

    // ---------------- window ----------------

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        // Read the tray policy from the injected config singleton (BUG-10) — a
        // fresh AppConfig.Load() here would see stale on-disk values.
        if (_vm.MinimizeToTray && _vm.ActiveDownloads > 0)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _vm.Dispose();
    }

    // ---------------- batch queue (FEAT-06) ----------------

    private void ShowBatch(BatchViewModel vm)
    {
        var dialog = new BatchWindow(vm) { Owner = this };
        dialog.ShowDialog();
    }

    // ---------------- settings ----------------

    private void ShowSettings()
    {
        var vm = new SettingsViewModel(_vm.Config, _vm.BuildDiagnosticsReport);
        var dialog = new SettingsWindow(vm) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        // FEAT-05: proxy + cookies are captured at construction (SharedHttpClient
        // and YtDlpProvider singletons), so changes need a restart to take effect.
        // Everything else in the dialog applies instantly.
        var proxyChanged = !string.Equals(vm.ProxyUrl?.Trim(), _vm.Config.ProxyUrl?.Trim(), StringComparison.Ordinal);
        var cookiesChanged =
            !string.Equals(vm.CookiesBrowser?.Trim(), _vm.Config.CookiesBrowser?.Trim(), StringComparison.Ordinal)
            || !string.Equals(vm.CookiesFile?.Trim(), _vm.Config.CookiesFile?.Trim(), StringComparison.Ordinal);

        _vm.SaveSettings(vm.ApplyTo);
        AccentTheme.Apply(vm.Accent);
        _vm.SetClipboardMonitor(vm.ClipboardMonitor);

        if (!proxyChanged && !cookiesChanged) return;
        var what = proxyChanged && cookiesChanged ? "Proxy and cookie"
            : proxyChanged ? "Proxy" : "Cookie";
        if (MessageBox.Show(this,
                $"{what} changes take effect after a restart.\n\nRestart MusicEngine now?",
                "Restart required", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            Environment.ProcessPath!) { UseShellExecute = true });
        Application.Current.Shutdown();
    }
}
