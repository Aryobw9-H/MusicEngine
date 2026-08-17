namespace MusicEngine.App;

using System.IO;

/// <summary>
/// Appends crash details to %APPDATA%\MusicEngine\crash.log so "it just
/// closed" reports can be diagnosed after the fact.
/// </summary>
public static class CrashLog
{
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MusicEngine", "crash.log");

    public static void Write(string kind, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.AppendAllText(Path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {kind}: {ex}\n\n");
        }
        catch { /* nothing sensible to do when even logging fails */ }
    }
}
