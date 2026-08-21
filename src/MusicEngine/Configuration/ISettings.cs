namespace MusicEngine.Configuration;

using Models;

/// <summary>
/// Read-only settings surface for the engine (providers, download manager,
/// registry). Implemented by <see cref="AppConfig"/>; lets tests construct
/// providers with an inline settings object instead of touching the real
/// appsettings.json (MODERN-01). Write access stays on the concrete
/// <see cref="AppConfig"/> in the WPF layer.
/// </summary>
public interface ISettings
{
    string OutputDirectory { get; }
    string? ProxyUrl { get; }
    int MaxParallelDownloads { get; }
    string? YtDlpPath { get; }
    string? FfmpegPath { get; }
    string? PythonPath { get; }
    string? CookiesBrowser { get; }
    string? CookiesFile { get; }
    bool EnablePersianIndex { get; }
    int SearchTimeoutSeconds { get; }
    int BitrateKbps { get; }
    FilenameTemplate FilenameTemplate { get; }
    string Accent { get; }
    bool ClipboardMonitor { get; }
    bool MinimizeToTray { get; }
    bool DownloadToasts { get; }
    IReadOnlyCollection<string> DisabledSources { get; }
    bool IsSourceEnabled(ProviderId id);
}
