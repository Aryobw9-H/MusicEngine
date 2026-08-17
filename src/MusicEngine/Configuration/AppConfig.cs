namespace MusicEngine.Configuration;

using System.Text.Json;
using Models;

/// <summary>Filename templates for saved MP3s.</summary>
public enum FilenameTemplate
{
    ArtistTitle,       // "Artist - Title.mp3"
    Title,             // "Title.mp3"
    ArtistTitleSource, // "Artist - Title (Source).mp3"
}

/// <summary>
/// App configuration, persisted as appsettings.json next to the executable.
/// No hard-coded paths anywhere else — everything tunable lives here.
/// </summary>
public sealed class AppConfig
{
    public string OutputDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "MusicEngine");

    /// <summary>HTTP or SOCKS5 proxy for providers that need it (YouTube, Deezer…). Null = direct.</summary>
    public string? ProxyUrl { get; set; } = "socks5://127.0.0.1:10808";

    public int MaxParallelDownloads { get; set; } = 2;

    public string? YtDlpPath { get; set; }
    public string? FfmpegPath { get; set; }
    public string? PythonPath { get; set; } = "python";

    /// <summary>Browser whose cookies yt-dlp may use for YouTube bot checks ("chrome", "firefox"…). Null = none.</summary>
    public string? CookiesBrowser { get; set; }

    /// <summary>Enable the Python curl_cffi sidecar for music-fa/upmusics/taksong. Auto-disabled when python/curl_cffi is missing.</summary>
    public bool EnablePersianIndex { get; set; } = true;

    /// <summary>Seconds a search provider may take before its results are ignored.</summary>
    public int SearchTimeoutSeconds { get; set; } = 6;

    /// <summary>Target MP3 bitrate for yt-dlp conversion.</summary>
    public int BitrateKbps { get; set; } = 320;

    public FilenameTemplate FilenameTemplate { get; set; } = FilenameTemplate.ArtistTitle;

    /// <summary>Search sources disabled by the user (ProviderId names). All enabled by default.</summary>
    public HashSet<string> DisabledSources { get; set; } = new();

    /// <summary>UI accent: green / violet / blue / amber / rose.</summary>
    public string Accent { get; set; } = "green";

    /// <summary>Offer to search when a music link is copied to the clipboard.</summary>
    public bool ClipboardMonitor { get; set; }

    /// <summary>Close to tray instead of exiting (download keep running).</summary>
    public bool MinimizeToTray { get; set; }

    /// <summary>Toast a notification when a download finishes.</summary>
    public bool DownloadToasts { get; set; } = true;

    public string ConfigPath { get; private set; } = "";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static AppConfig Load(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        AppConfig cfg;
        try
        {
            if (File.Exists(path))
            {
                cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), JsonOpts) ?? new AppConfig();
            }
            else
            {
                cfg = new AppConfig();
                try { File.WriteAllText(path, JsonSerializer.Serialize(cfg, JsonOpts)); } catch { /* read-only dir */ }
            }
        }
        catch
        {
            cfg = new AppConfig();
        }
        cfg.ConfigPath = path;
        if (string.IsNullOrWhiteSpace(cfg.OutputDirectory))
            cfg.OutputDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "MusicEngine");
        if (string.IsNullOrWhiteSpace(cfg.ProxyUrl)) cfg.ProxyUrl = null;
        if (string.IsNullOrWhiteSpace(cfg.PythonPath)) cfg.PythonPath = "python";
        cfg.BitrateKbps = cfg.BitrateKbps is >= 64 and <= 320 ? cfg.BitrateKbps : 320;
        cfg.MaxParallelDownloads = Math.Clamp(cfg.MaxParallelDownloads, 1, 8);
        return cfg;
    }

    public void Save()
    {
        try { File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts)); }
        catch { /* best effort */ }
    }

    public bool IsSourceEnabled(ProviderId id) => !DisabledSources.Contains(id.ToString());
}
