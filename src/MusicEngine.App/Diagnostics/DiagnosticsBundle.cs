namespace MusicEngine.App.Diagnostics;

using System.IO;
using System.Runtime.InteropServices;
using Configuration;
using Providers;

/// <summary>
/// User-initiated, fully local diagnostics report (FEAT-04). Assembled only when
/// the user clicks "Copy diagnostics" — nothing is transmitted anywhere. The
/// report is deliberately redacted: no output-directory path, no filenames, no
/// search history, no URLs, and the proxy is reported as scheme+port only.
/// </summary>
public static class DiagnosticsBundle
{
    public static string Build(AppConfig cfg, ProviderRegistry registry, YtDlpProvider ytDlp, PersianIndexProvider persian)
    {
        var sb = new System.Text.StringBuilder();
        var app = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
        sb.AppendLine("MusicEngine diagnostics");
        sb.AppendLine("=======================");
        sb.AppendLine($"app: {app}");
        sb.AppendLine($"runtime: {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"os: {RuntimeInformation.OSDescription}");
        sb.AppendLine($"utc: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        // Proxy: scheme + port only — never the host or any credentials.
        if (string.IsNullOrWhiteSpace(cfg.ProxyUrl))
            sb.AppendLine("proxy: none");
        else if (Uri.TryCreate(cfg.ProxyUrl, UriKind.Absolute, out var proxy))
            sb.AppendLine($"proxy: {proxy.Scheme}://<redacted>:{proxy.Port} (configured)");
        else
            sb.AppendLine("proxy: configured but unparseable");

        sb.AppendLine($"download toasts: {cfg.DownloadToasts}");
        sb.AppendLine($"clipboard monitor: {cfg.ClipboardMonitor}");
        sb.AppendLine($"max parallel downloads: {cfg.MaxParallelDownloads}");
        sb.AppendLine();

        sb.AppendLine("sources:");
        foreach (var p in registry.EnabledSearchProviders())
            sb.AppendLine($"  {p.DisplayName} ({(registry.OfflineSources.Contains(p.DisplayName) ? "offline" : "online")})");
        sb.AppendLine($"offline sources: {string.Join(", ", registry.OfflineSources)}");
        sb.AppendLine();

        sb.AppendLine($"yt-dlp resolved: {ytDlp.IsAvailable}");
        sb.AppendLine($"persian index sidecar: {persian.IsAvailable}");

        // Output directory: existence + free space only, never the path itself.
        try
        {
            var dir = new System.IO.DirectoryInfo(cfg.OutputDirectory);
            if (!dir.Exists)
                sb.AppendLine("output directory: missing");
            else
            {
                var free = new DriveInfo(System.IO.Path.GetPathRoot(dir.FullName)!);
                sb.AppendLine($"output directory: ok · free {free.AvailableFreeSpace / 1024 / 1024 / 1024.0:0.0} GiB");
            }
        }
        catch
        {
            sb.AppendLine("output directory: unreadable");
        }
        sb.AppendLine();

        // Tail of today's log — the engine messages that make "no results for X"
        // diagnosable without a debugger.
        try
        {
            var logPath = System.IO.Path.Combine(Logging.FileLoggerProvider.LogsDirectory,
                $"app-{DateTime.Now:yyyy-MM-dd}.log");
            if (System.IO.File.Exists(logPath))
            {
                var tail = System.IO.File.ReadAllLines(logPath).TakeLast(100);
                sb.AppendLine("--- last log lines ---");
                foreach (var line in tail) sb.AppendLine(line);
            }
            else
            {
                sb.AppendLine("log: (no log file yet)");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"log: unavailable ({ex.Message})");
        }
        return sb.ToString();
    }
}
