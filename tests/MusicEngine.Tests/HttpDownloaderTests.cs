namespace MusicEngine.Tests;

using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Downloads;
using Models;
using Xunit;

/// <summary>
/// Download-engine tests against a local Range-capable HTTP server — fully
/// offline. Ported verbatim from the former console harness (MODERN-02).
/// </summary>
public class HttpDownloaderTests
{
    /// <summary>Cancel mid-download once real progress is persisted (~2s in), then
    /// resume and verify the file is byte-perfect. Also proves cancellation leaves
    /// .part/.state on disk instead of deleting them.</summary>
    [Fact]
    public async Task CancelMidDownloadThenResumeIsBytePerfect()
    {
        var content = RandomBytes(6_291_456, seed: 42);
        using var server = new LocalRangeServer(content, RandomBytes(content.Length, seed: 7), throttleMs: 250);
        var (final, dir) = NewTempTarget();
        try
        {
            using var http = new HttpClient();
            var url = server.BaseUrl + "a.mp3";

            var phase = await StartAndCancelMidwayAsync(http, url, final);
            Assert.Equal(DownloadPhase.Cancelled, phase); // must actually interrupt
            Assert.True(File.Exists(final + ".part"));
            Assert.True(File.Exists(final + ".state"));
            Assert.True(await DownloadedFromStateAsync(final + ".state") > 0); // real partial progress

            await HttpDownloader.DownloadToFileAsync(http, url, final, null, CancellationToken.None);
            Assert.True(await FileMatchesAsync(final, content));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>Cancel a download of /a.mp3 with real progress, then resume a
    /// different URL of the same size. The state file is bound to its URL (like
    /// aria2's control file) — resuming must discard it and restart clean,
    /// otherwise the final file would mix bytes from both sources.</summary>
    [Fact]
    public async Task ResumeAfterUrlChangeNeverMixesFiles()
    {
        var contentA = RandomBytes(6_291_456, seed: 42);
        var contentB = RandomBytes(6_291_456, seed: 7);
        using var server = new LocalRangeServer(contentA, contentB, throttleMs: 250);
        var (final, dir) = NewTempTarget();
        try
        {
            using var http = new HttpClient();

            var phase = await StartAndCancelMidwayAsync(http, server.BaseUrl + "a.mp3", final);
            Assert.Equal(DownloadPhase.Cancelled, phase);
            Assert.True(await DownloadedFromStateAsync(final + ".state") > 0);

            await HttpDownloader.DownloadToFileAsync(http, server.BaseUrl + "other.mp3", final, null, CancellationToken.None);
            Assert.True(await FileMatchesAsync(final, contentB));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>The server promises a size but drops the connection halfway — the
    /// single-stream path must FAIL instead of shipping a truncated "complete" file.</summary>
    [Fact]
    public async Task TruncatedSingleStreamIsRejected()
    {
        var content = RandomBytes(1_048_576, seed: 42);
        using var server = new LocalRangeServer(content, RandomBytes(content.Length, seed: 7));
        var (final, dir) = NewTempTarget();
        try
        {
            using var http = new HttpClient();
            var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
                HttpDownloader.DownloadToFileAsync(http, server.BaseUrl + "trunc.mp3", final, null, CancellationToken.None));
            Assert.True(ex is IOException or HttpRequestException, $"expected IO/HTTP failure, got {ex.GetType().Name}");
            Assert.False(File.Exists(final));
            Assert.False(File.Exists(final + ".part"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>The first request for the last chunk returns 503; the per-chunk
    /// retry must re-request the remaining range and the file must stay intact.</summary>
    [Fact]
    public async Task TransientChunk503IsRetried()
    {
        var content = RandomBytes(3_670_016, seed: 42);
        using var server = new LocalRangeServer(content, RandomBytes(content.Length, seed: 7), failChunkRequests: 1);
        var (final, dir) = NewTempTarget();
        try
        {
            using var http = new HttpClient();
            await HttpDownloader.DownloadToFileAsync(http, server.BaseUrl + "a.mp3", final, null, CancellationToken.None);
            Assert.True(await FileMatchesAsync(final, content));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    // ---------------- helpers ----------------

    private static byte[] RandomBytes(int length, int seed)
    {
        var b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static (string Final, string Dir) NewTempTarget()
    {
        var dir = Path.Combine(Path.GetTempPath(), "musicengine-http-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return (Path.Combine(dir, "song.mp3"), dir);
    }

    private static async Task<bool> FileMatchesAsync(string path, byte[] expected)
    {
        if (!File.Exists(path)) return false;
        var actual = await File.ReadAllBytesAsync(path);
        return actual.Length == expected.Length
            && SHA256.HashData(actual).SequenceEqual(SHA256.HashData(expected));
    }

    private static async Task<long> DownloadedFromStateAsync(string statePath)
    {
        try
        {
            var json = await File.ReadAllTextAsync(statePath);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("Chunks").EnumerateArray()
                .Sum(c => c.GetProperty("Downloaded").GetInt64());
        }
        catch { return -1; }
    }

    /// <summary>Start a download and cancel it the moment the state file records real
    /// partial progress (i.e. after the first periodic save, ~2s in). Machine-speed
    /// independent: the watcher polls, the server's throttle keeps chunks from
    /// finishing before the save.</summary>
    private static async Task<DownloadPhase> StartAndCancelMidwayAsync(HttpClient http, string url, string final)
    {
        using var cts = new CancellationTokenSource();
        var downloadDone = new TaskCompletionSource();
        var watcher = Task.Run(async () =>
        {
            try
            {
                while (!downloadDone.Task.IsCompleted)
                {
                    if (await DownloadedFromStateAsync(final + ".state") > 0)
                    {
                        cts.Cancel();
                        return;
                    }
                    await Task.Delay(25, cts.Token);
                }
            }
            catch (OperationCanceledException) { }
        });
        try
        {
            await HttpDownloader.DownloadToFileAsync(http, url, final, null, cts.Token);
            return DownloadPhase.Completed; // finished before the watcher could cancel
        }
        catch (OperationCanceledException) { return DownloadPhase.Cancelled; }
        finally
        {
            downloadDone.TrySetResult();
            await watcher;
        }
    }

    /// <summary>Minimal Range-capable HTTP server for the offline download-engine tests.
    /// Serves two bodies: the default and an "other" variant of the same size.</summary>
    private sealed class LocalRangeServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly byte[] _content;
        private readonly byte[] _contentB;
        private readonly int _throttleMs;
        private int _chunkFailuresLeft;

        public string BaseUrl { get; }

        public LocalRangeServer(byte[] content, byte[] contentB, int throttleMs = 0, int failChunkRequests = 0)
        {
            _content = content;
            _contentB = contentB;
            _throttleMs = throttleMs;
            _chunkFailuresLeft = failChunkRequests;
            _listener.Prefixes.Add($"http://localhost:{FreePort()}/");
            _listener.Start();
            BaseUrl = _listener.Prefixes.First();
            _ = Task.Run(ServeLoopAsync);
        }

        private static int FreePort()
        {
            using var tcp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            tcp.Start();
            var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
            tcp.Stop();
            return port;
        }

        private async Task ServeLoopAsync()
        {
            while (true)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }
                _ = Task.Run(() => HandleAsync(ctx));
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            try
            {
                var body = ctx.Request.Url?.AbsolutePath.Contains("other") == true ? _contentB : _content;
                var range = ctx.Request.Headers["Range"];
                var lastChunkStart = body.Length / 8 * 7;

                // Fail (503) a limited number of requests for the LAST chunk only.
                if (range is not null
                    && long.TryParse(ParseStart(range), out var chunkStart) && chunkStart >= lastChunkStart
                    && Interlocked.Decrement(ref _chunkFailuresLeft) >= 0)
                {
                    ctx.Response.StatusCode = 503;
                    ctx.Response.Close();
                    return;
                }

                var truncate = ctx.Request.Url?.AbsolutePath.Contains("trunc") == true;

                ctx.Response.Headers["Accept-Ranges"] = "bytes";
                if (range is null)
                {
                    ctx.Response.StatusCode = 200;
                    ctx.Response.ContentLength64 = body.Length;
                    // Truncation mode: promise the full length, send half, drop the
                    // connection — simulates a CDN dying mid-transfer.
                    var send = truncate ? body.Length / 2 : body.Length;
                    await ctx.Response.OutputStream.WriteAsync(body.AsMemory(0, send));
                }
                else
                {
                    var m = System.Text.RegularExpressions.Regex.Match(range, @"bytes=(\d+)-(\d*)");
                    var start = long.Parse(m.Groups[1].Value);
                    var end = m.Groups[2].Value.Length > 0 ? long.Parse(m.Groups[2].Value) : body.Length - 1;
                    end = Math.Min(end, body.Length - 1);
                    ctx.Response.StatusCode = 206;
                    ctx.Response.ContentLength64 = end - start + 1;
                    await WriteRangeAsync(ctx.Response.OutputStream, body, start, end);
                }
                ctx.Response.Close();
            }
            catch { /* client aborted — expected on cancel */ }
        }

        private static string? ParseStart(string range)
        {
            var m = System.Text.RegularExpressions.Regex.Match(range, @"bytes=(\d+)-");
            return m.Success ? m.Groups[1].Value : null;
        }

        private async Task WriteRangeAsync(Stream output, byte[] body, long start, long end)
        {
            var buffer = new byte[64 * 1024];
            var pos = start;
            while (pos <= end)
            {
                var n = (int)Math.Min(buffer.Length, end - pos + 1);
                Buffer.BlockCopy(body, (int)pos, buffer, 0, n);
                await output.WriteAsync(buffer.AsMemory(0, n));
                pos += n;
                if (_throttleMs > 0) await Task.Delay(_throttleMs);
            }
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
        }
    }
}
