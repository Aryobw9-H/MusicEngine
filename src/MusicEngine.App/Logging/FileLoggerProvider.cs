namespace MusicEngine.App.Logging;

using System.Collections.Concurrent;
using System.IO;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

/// <summary>
/// Rolling file logger (FEAT-01): writes structured engine messages to
/// %APPDATA%\MusicEngine\logs\app-yyyy-MM-dd.log so Release builds are
/// diagnosable without a debugger. Log() never blocks the caller — lines go to
/// an unbounded channel and a single background task flushes them in batches.
/// Files roll at 5 MB (app-….log → -2, -3, …) and files older than 7 days are
/// deleted at startup.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;
    private readonly LogLevel _minLevel;
    private readonly CancellationTokenSource _shutdown = new();

    public static string LogsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MusicEngine", "logs");

    public FileLoggerProvider(LogLevel minLevel = LogLevel.Information)
    {
        _minLevel = minLevel;
        try { Directory.CreateDirectory(LogsDirectory); } catch { /* best effort */ }
        CleanupOldLogs();
        _writer = Task.Run(async () =>
        {
            var lines = new List<string>(64);
            try
            {
                await foreach (var line in _queue.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
                {
                    lines.Add(line);
                    if (lines.Count >= 64)
                    {
                        Flush(lines);
                        lines.Clear();
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (lines.Count > 0) Flush(lines); // drain on shutdown
            }
        });
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= owner._minLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var ts = DateTime.Now.ToString("HH:mm:ss.fff");
            var line = $"{ts} {logLevel.ToString().PadRight(5)} {category}: {formatter(state, exception)}";
            if (exception is not null)
                line += Environment.NewLine + exception;
            owner._queue.Writer.TryWrite(line);
        }
    }

    private static void Flush(List<string> lines)
    {
        try
        {
            var path = CurrentLogPath();
            var fi = new FileInfo(path);
            // Roll when the current file is at/over 5 MB: app-x.log → app-x-2.log, shifting older ones.
            if (fi.Exists && fi.Length > 5 * 1024 * 1024)
            {
                for (var i = 8; i >= 2; i--)
                {
                    var from = Path.Combine(LogsDirectory, $"app-{DateTime.Now:yyyy-MM-dd}-{i}.log");
                    var to = Path.Combine(LogsDirectory, $"app-{DateTime.Now:yyyy-MM-dd}-{i + 1}.log");
                    if (File.Exists(from)) File.Move(from, to, overwrite: true);
                }
                File.Move(path, Path.Combine(LogsDirectory, $"app-{DateTime.Now:yyyy-MM-dd}-2.log"), overwrite: true);
            }
            File.AppendAllLines(path, lines);
        }
        catch { /* logging must never throw into the caller */ }
    }

    private static string CurrentLogPath() =>
        Path.Combine(LogsDirectory, $"app-{DateTime.Now:yyyy-MM-dd}.log");

    private static void CleanupOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-7);
            foreach (var f in Directory.EnumerateFiles(LogsDirectory, "app-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
                }
                catch { /* best effort */ }
            }
        }
        catch { /* best effort */ }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try { _writer.Wait(TimeSpan.FromSeconds(2)); } catch { /* drain is best-effort */ }
        _shutdown.Dispose();
    }
}
