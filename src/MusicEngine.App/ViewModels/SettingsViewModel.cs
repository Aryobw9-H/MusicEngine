namespace MusicEngine.App.ViewModels;

using System.Collections.ObjectModel;
using Configuration;
using Models;

/// <summary>One search-source toggle in the settings dialog.</summary>
public sealed class SourceToggleViewModel : ViewModelBase
{
    public required ProviderId Id { get; init; }
    public required string DisplayName { get; init; }

    private bool _isEnabled;
    public bool IsEnabled { get => _isEnabled; set => Set(ref _isEnabled, value); }
}

/// <summary>One accent swatch in the settings dialog.</summary>
public sealed class AccentOptionViewModel : ViewModelBase
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string Hex { get; init; }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

/// <summary>
/// Settings dialog state (MVVM-02): scalar settings plus an
/// <see cref="ObservableCollection{T}"/> of source toggles and accent swatches,
/// applied back to <see cref="AppConfig"/> via <see cref="ApplyTo"/>.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    public ObservableCollection<SourceToggleViewModel> Sources { get; } = new();
    public ObservableCollection<AccentOptionViewModel> Accents { get; } = new();

    public IReadOnlyList<int> ParallelOptions { get; } = Enumerable.Range(1, 6).ToArray();
    public IReadOnlyList<string> BitrateLabels { get; } = new[] { "128 kbps", "192 kbps", "320 kbps" };
    public IReadOnlyList<string> FilenameTemplateLabels { get; } = new[] { "Artist - Title", "Title", "Artist - Title (Source)" };

    private string _outputDirectory = "";
    public string OutputDirectory { get => _outputDirectory; set => Set(ref _outputDirectory, value); }

    private string _proxyUrl = "";
    public string ProxyUrl { get => _proxyUrl; set => Set(ref _proxyUrl, value); }

    private string _cookiesBrowser = "";
    public string CookiesBrowser { get => _cookiesBrowser; set => Set(ref _cookiesBrowser, value); }

    private string _cookiesFile = "";
    public string CookiesFile { get => _cookiesFile; set => Set(ref _cookiesFile, value); }

    private bool _enablePersianIndex = true;
    public bool EnablePersianIndex { get => _enablePersianIndex; set => Set(ref _enablePersianIndex, value); }

    private bool _downloadToasts = true;
    public bool DownloadToasts { get => _downloadToasts; set => Set(ref _downloadToasts, value); }

    private bool _minimizeToTray;
    public bool MinimizeToTray { get => _minimizeToTray; set => Set(ref _minimizeToTray, value); }

    private bool _clipboardMonitor;
    public bool ClipboardMonitor { get => _clipboardMonitor; set => Set(ref _clipboardMonitor, value); }

    private int _maxParallelDownloads = 2;
    public int MaxParallelDownloads { get => _maxParallelDownloads; set => Set(ref _maxParallelDownloads, value); }

    private int _bitrateIndex = 2;
    public int BitrateIndex { get => _bitrateIndex; set => Set(ref _bitrateIndex, value); }

    private int _templateIndex;
    public int FilenameTemplateIndex { get => _templateIndex; set => Set(ref _templateIndex, value); }

    private string _accent = "green";
    public string Accent { get => _accent; set => Set(ref _accent, value); }

    public RelayCommand BrowseCommand { get; }
    public RelayCommand SelectAccentCommand { get; }
    public RelayCommand OpenLogsFolderCommand { get; }
    public RelayCommand OpenCrashLogCommand { get; }
    public RelayCommand CopyDiagnosticsCommand { get; }

    /// <summary>Transient "Copied ✓" confirmation next to the diagnostics button (FEAT-04).</summary>
    private string _diagnosticsNotice = "";
    public string DiagnosticsNotice { get => _diagnosticsNotice; set => Set(ref _diagnosticsNotice, value); }

    private System.Windows.Threading.DispatcherTimer? _noticeTimer;

    public SettingsViewModel(AppConfig cfg, Func<string>? diagnosticsBuilder = null)
    {
        OutputDirectory = cfg.OutputDirectory;
        ProxyUrl = cfg.ProxyUrl ?? "";
        CookiesBrowser = cfg.CookiesBrowser ?? "";
        CookiesFile = cfg.CookiesFile ?? "";
        EnablePersianIndex = cfg.EnablePersianIndex;
        DownloadToasts = cfg.DownloadToasts;
        MinimizeToTray = cfg.MinimizeToTray;
        ClipboardMonitor = cfg.ClipboardMonitor;
        MaxParallelDownloads = Math.Clamp(cfg.MaxParallelDownloads, 1, 6);
        BitrateIndex = cfg.BitrateKbps switch { 128 => 0, 192 => 1, _ => 2 };
        FilenameTemplateIndex = (int)cfg.FilenameTemplate;
        Accent = cfg.Accent;

        foreach (var id in Enum.GetValues<ProviderId>())
        {
            if (id is ProviderId.Unknown or ProviderId.YtDlp) continue;
            Sources.Add(new SourceToggleViewModel
            {
                Id = id,
                DisplayName = DisplayNameFor(id),
                IsEnabled = cfg.IsSourceEnabled(id),
            });
        }

        foreach (var (key, label, hex) in AccentTheme.Presets)
            Accents.Add(new AccentOptionViewModel
            {
                Key = key, Label = label, Hex = hex,
                IsSelected = key == Accent,
            });

        BrowseCommand = new RelayCommand(_ =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Choose the music folder",
                InitialDirectory = System.IO.Directory.Exists(OutputDirectory) ? OutputDirectory : null,
            };
            if (dialog.ShowDialog() == true)
                OutputDirectory = dialog.FolderName;
        });

        SelectAccentCommand = new RelayCommand(p =>
        {
            if (p is not AccentOptionViewModel option) return;
            foreach (var a in Accents) a.IsSelected = ReferenceEquals(a, option);
            Accent = option.Key;
        });

        // FEAT-01: the log files are the support contract.
        OpenLogsFolderCommand = new RelayCommand(_ =>
        {
            var dir = Logging.FileLoggerProvider.LogsDirectory;
            System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
        });
        OpenCrashLogCommand = new RelayCommand(_ =>
        {
            var path = CrashLog.Path;
            if (System.IO.File.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        });

        // FEAT-04: assemble the redacted bundle locally and put it on the
        // clipboard — nothing is transmitted anywhere. The production window
        // always passes the DI-backed builder via MainWindow; the null fallback
        // only serves the designer convenience ctor and reports config alone.
        CopyDiagnosticsCommand = new RelayCommand(_ =>
        {
            var report = diagnosticsBuilder?.Invoke() ?? ConfigOnlyReport(cfg);
            System.Windows.Clipboard.SetText(report);
            DiagnosticsNotice = "Copied to clipboard — paste it into your support message";
            _noticeTimer?.Stop();
            _noticeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            _noticeTimer.Tick += (_, _) => { _noticeTimer!.Stop(); DiagnosticsNotice = ""; };
            _noticeTimer.Start();
        });
    }

    /// <summary>Designer-path fallback: proxy scheme/port + enabled sources, nothing more.</summary>
    private static string ConfigOnlyReport(AppConfig cfg)
    {
        var sb = new System.Text.StringBuilder("MusicEngine diagnostics (config only)\n");
        if (string.IsNullOrWhiteSpace(cfg.ProxyUrl))
            sb.AppendLine("proxy: none");
        else if (Uri.TryCreate(cfg.ProxyUrl, UriKind.Absolute, out var proxy))
            sb.AppendLine($"proxy: {proxy.Scheme}://<redacted>:{proxy.Port}");
        else
            sb.AppendLine("proxy: configured but unparseable");
        sb.Append("enabled sources: ");
        var enabled = Enum.GetValues<ProviderId>()
            .Where(id => id is not ProviderId.Unknown and not ProviderId.YtDlp && cfg.IsSourceEnabled(id));
        sb.AppendLine(string.Join(", ", enabled));
        return sb.ToString();
    }

    /// <summary>Designer convenience; production windows construct with the DI config.</summary>
    public SettingsViewModel() : this(AppConfig.Load()) { }

    /// <summary>Write every value back to the config singleton (MVVM-02).</summary>
    public void ApplyTo(AppConfig cfg)
    {
        cfg.OutputDirectory = OutputDirectory;
        cfg.ProxyUrl = ProxyUrl;
        cfg.CookiesBrowser = CookiesBrowser;
        cfg.CookiesFile = CookiesFile;
        cfg.EnablePersianIndex = EnablePersianIndex;
        cfg.MaxParallelDownloads = Math.Clamp(MaxParallelDownloads, 1, 8);
        cfg.BitrateKbps = BitrateIndex switch { 0 => 128, 1 => 192, _ => 320 };
        cfg.FilenameTemplate = (FilenameTemplate)Math.Clamp(FilenameTemplateIndex, 0, 2);
        cfg.Accent = Accent;
        cfg.ClipboardMonitor = ClipboardMonitor;
        cfg.MinimizeToTray = MinimizeToTray;
        cfg.DownloadToasts = DownloadToasts;
        cfg.DisabledSources.Clear();
        foreach (var s in Sources)
            if (!s.IsEnabled)
                cfg.DisabledSources.Add(s.Id.ToString());
    }

    private static string DisplayNameFor(ProviderId id) =>
        Providers.ProviderCatalog.Get(id).DisplayName;
}
