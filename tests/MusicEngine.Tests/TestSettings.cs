namespace MusicEngine.Tests;

using Configuration;
using Models;

/// <summary>In-memory <see cref="ISettings"/> for offline tests.</summary>
public sealed class TestSettings : ISettings
{
    public string OutputDirectory { get; set; } = "";
    public string? ProxyUrl { get; set; }
    public int MaxParallelDownloads { get; set; } = 2;
    public string? YtDlpPath { get; set; }
    public string? FfmpegPath { get; set; }
    public string? PythonPath { get; set; }
    public string? CookiesBrowser { get; set; }
    public string? CookiesFile { get; set; }
    public bool EnablePersianIndex { get; set; } = true;
    public int SearchTimeoutSeconds { get; set; } = 15;
    public int BitrateKbps { get; set; } = 320;
    public FilenameTemplate FilenameTemplate { get; set; }
    public string Accent { get; set; } = "green";
    public bool ClipboardMonitor { get; set; }
    public bool MinimizeToTray { get; set; }
    public bool DownloadToasts { get; set; } = true;
    public IReadOnlyCollection<string> DisabledSources { get; set; } = Array.Empty<string>();
    public bool IsSourceEnabled(ProviderId id) => true;
}
