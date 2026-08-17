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
    private bool _seeking;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        vm.SettingsRequested += ShowSettings;
        vm.PropertyChanged += VmOnPropertyChanged;
        UpdateEmptyState();
    }

    private void VmOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.HasResults):
            case nameof(MainViewModel.IsSearching):
                UpdateEmptyState();
                ResultsCount.Text = _vm.ResultsView.Count > 0 ? $"({_vm.ResultsView.Count})" : "";
                break;
            case nameof(MainViewModel.PlayerPosition) when !_seeking:
                SeekSlider.Value = _vm.PlayerPosition;
                break;
            case nameof(MainViewModel.PlayerDuration):
                SeekSlider.Maximum = Math.Max(1, _vm.PlayerDuration);
                break;
        }
    }

    private void UpdateEmptyState() =>
        EmptyState.Visibility = _vm.HasResults || _vm.IsSearching
            ? Visibility.Collapsed
            : Visibility.Visible;

    // ---------------- search ----------------

    private async void Search_Click(object sender, RoutedEventArgs e) => await RunSearch();

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await RunSearch();
    }

    private async Task RunSearch()
    {
        RecentsPopup.IsOpen = false;
        SearchSpinner.Visibility = Visibility.Visible;
        try { await _vm.SearchAsync(); }
        finally { SearchSpinner.Visibility = Visibility.Collapsed; }
    }

    private void ClearQuery_Click(object sender, RoutedEventArgs e)
    {
        _vm.Query = "";
        SearchBox.Focus();
    }

    private void Recents_Click(object sender, RoutedEventArgs e) =>
        RecentsPopup.IsOpen = _vm.RecentSearches.Count > 0 && !RecentsPopup.IsOpen;

    private async void Recent_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is string q)
        {
            _vm.Query = q;
            await RunSearch();
        }
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

    private void Sort_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if ((sender as ComboBox)?.SelectedItem is ComboBoxItem item && _vm is not null)
        {
            _vm.SortMode = item.Content switch
            {
                "Duration" => ResultSort.Duration,
                "Title" => ResultSort.Title,
                _ => ResultSort.Relevance,
            };
        }
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

    private void Seek_DragStarted(object sender, DragStartedEventArgs e) => _seeking = true;

    private void Seek_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _seeking = false;
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
        var config = AppConfig.Load();
        if (config.MinimizeToTray && _vm.ActiveDownloads > 0)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _vm.Dispose();
    }

    // ---------------- settings ----------------

    private void ShowSettings()
    {
        var dialog = new SettingsWindow { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _vm.SaveSettings(cfg =>
        {
            cfg.OutputDirectory = dialog.OutputDirectory;
            cfg.ProxyUrl = dialog.ProxyUrl;
            cfg.CookiesBrowser = dialog.CookiesBrowser;
            cfg.EnablePersianIndex = dialog.EnablePersianIndex;
            cfg.MaxParallelDownloads = dialog.ParallelDownloads;
            cfg.BitrateKbps = dialog.Bitrate;
            cfg.FilenameTemplate = dialog.Template;
            cfg.Accent = dialog.Accent;
            cfg.ClipboardMonitor = dialog.ClipboardMonitor;
            cfg.MinimizeToTray = dialog.MinimizeToTray;
            cfg.DownloadToasts = dialog.DownloadToasts;
            foreach (var disabled in dialog.DisabledSources)
                cfg.DisabledSources.Add(disabled);
            foreach (var enabled in dialog.EnabledSources)
                cfg.DisabledSources.Remove(enabled);
        });
        AccentTheme.Apply(dialog.Accent);
        _vm.SetClipboardMonitor(dialog.ClipboardMonitor);
    }
}
