namespace MusicEngine.Downloads;

using Models;

/// <summary>
/// Shared HTTP-to-file downloader with real byte progress, cancellation, a stall
/// watchdog — and multi-segment parallel transfer: when the server advertises
/// Accept-Ranges, the file is fetched in 4 concurrent segments (roughly 3-4×
/// faster on throttled-per-connection CDNs) and stitched together.
/// </summary>
public static class HttpDownloader
{
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(45);
    private const int Segments = 4;

    public static async Task DownloadToFileAsync(
        HttpClient http,
        string url,
        string finalPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct,
        string? resolvingMessage = null)
    {
        progress?.Report(new DownloadProgress(DownloadPhase.Downloading, 0, null, resolvingMessage ?? "Downloading"));

        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength;
        var acceptsRanges = resp.Headers.AcceptRanges.Contains("bytes");

        var temp = finalPath + ".part";
        try
        {
            if (total is > 2_000_000 && acceptsRanges)
            {
                await DownloadSegmentedAsync(http, url, temp, total.Value, progress, ct).ConfigureAwait(false);
            }
            else
            {
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await CopyWithProgressAsync(src, temp, total, progress, ct).ConfigureAwait(false);
            }
            File.Move(temp, finalPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }

        progress?.Report(new DownloadProgress(DownloadPhase.Downloading,
            new FileInfo(finalPath).Length, new FileInfo(finalPath).Length));
    }

    /// <summary>Fetch <paramref name="total"/> bytes in N parallel Range requests.</summary>
    private static async Task DownloadSegmentedAsync(
        HttpClient http, string url, string temp, long total,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var segmentSize = total / Segments;
        var ranges = new (long From, long To)[Segments];
        for (var i = 0; i < Segments; i++)
            ranges[i] = (i * segmentSize, i == Segments - 1 ? total - 1 : (i + 1) * segmentSize - 1);

        var done = new long[Segments];
        var lastReport = DateTime.UtcNow;
        var reportLock = new object();

        // Pre-size the file, then let every segment write through its OWN
        // FileStream (a single stream is not safe for concurrent positioned writes).
        using (var preallocate = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.Write, 81920, useAsync: true))
            preallocate.SetLength(total);

        await Task.WhenAll(ranges.Select(async (range, i) =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(range.From, range.To);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var dst = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.Write, 81920, useAsync: true)
            {
                Position = range.From,
            };

            var buffer = new byte[81920];
            long written = 0;
            int read;
            while ((read = await ReadWithWatchdogAsync(src, buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                written += read;
                done[i] = written;
                lock (reportLock)
                {
                    if (progress is not null && (DateTime.UtcNow - lastReport).TotalMilliseconds >= 120)
                    {
                        lastReport = DateTime.UtcNow;
                        progress.Report(new DownloadProgress(DownloadPhase.Downloading, done.Sum(), total));
                    }
                }
            }
            if (written != range.To - range.From + 1)
                throw new IOException($"Segment {i} incomplete ({written}/{range.To - range.From + 1} bytes)");
        })).ConfigureAwait(false);
    }

    private static async Task CopyWithProgressAsync(
        System.IO.Stream src, string temp, long? total,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        await using var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        long done = 0;
        var last = DateTime.UtcNow;
        int read;
        while ((read = await ReadWithWatchdogAsync(src, buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            if (progress is not null && (DateTime.UtcNow - last).TotalMilliseconds >= 120)
            {
                progress.Report(new DownloadProgress(DownloadPhase.Downloading, done, total));
                last = DateTime.UtcNow;
            }
        }
    }

    /// <summary>Read that aborts when the stream produces no data for 45s.</summary>
    private static async Task<int> ReadWithWatchdogAsync(System.IO.Stream src, byte[] buffer, CancellationToken ct)
    {
        var readTask = src.ReadAsync(buffer, ct).AsTask();
        var done = await Task.WhenAny(readTask, Task.Delay(StallTimeout, ct)).ConfigureAwait(false);
        if (done != readTask)
            throw new IOException("Download stalled (no data for 45s).");
        return await readTask.ConfigureAwait(false);
    }
}
