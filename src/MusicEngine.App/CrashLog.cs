namespace MusicEngine.App;

using System.IO;
using System.Runtime.InteropServices;

/// <summary>
/// Appends crash details to %APPDATA%\MusicEngine\crash.log so "it just
/// closed" reports can be diagnosed after the fact (FEAT-04: one session
/// header per launch + full exception chains).
/// </summary>
public static class CrashLog
{
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MusicEngine", "crash.log");

    /// <summary>App version, runtime and OS — written once per session at startup.</summary>
    public static void WriteSessionHeader()
    {
        try
        {
            var version = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.AppendAllText(Path,
                $"\n===== session {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC =====\n" +
                $"app {version} · {RuntimeInformation.FrameworkDescription} · {RuntimeInformation.OSDescription}\n");
        }
        catch { /* nothing sensible to do when even logging fails */ }
    }

    public static void Write(string kind, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.AppendAllText(Path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {kind}: {Describe(ex)}\n\n");
        }
        catch { /* nothing sensible to do when even logging fails */ }
    }

    /// <summary>Full exception chain: message + stack + inner exceptions.</summary>
    private static string Describe(Exception? ex)
    {
        if (ex is null) return "null";
        var sb = new System.Text.StringBuilder();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (sb.Length > 0) sb.Append("\n  --- inner ---\n");
            sb.Append(e.GetType().FullName).Append(": ").Append(e.Message).Append('\n').Append(e.StackTrace ?? "");
        }
        return sb.ToString();
    }
}
