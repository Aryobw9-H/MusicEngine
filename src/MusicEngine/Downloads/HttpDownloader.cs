namespace MusicEngine.Downloads;

using System.Text.Json;
using Models;

/// <summary>
/// Advanced multi-segment downloader inspired by AB Download Manager (parallel
/// chunks with persistent per-chunk offsets) and aria2/Motrix (state file bound
/// to the exact source URL, chunk retries, resume authority).
///
/// Layout on disk while a download is in flight:
///   final.mp3.part    — preallocated file, chunks write into their byte ranges
///   final.mp3.state   — JSON: { Url, TotalBytes, Chunks:[{Id,Start,End,Downloaded}] }
///
/// Resume: the state file is the authority. If it exists, is valid and its Url
/// matches the requested URL, chunks continue from their saved offsets — no
/// preflight request needed. A mismatched URL (server file changed, or the
/// resolver picked a different source) discards the state and restarts clean,
/// never mixing bytes from two files.
/// </summary>
public static class HttpDownloader
{
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(45);
    private const int Segments = 8;
    private const int ChunkMaxAttempts = 3;

    private sealed class DownloadState
    {
        /// <summary>The exact URL this state belongs to. Resuming against a
        /// different URL must NOT reuse these offsets (aria2 control-file rule).</summary>
        public string Url { get; set; } = "";
        public long TotalBytes { get; set; }
        public List<ChunkState> Chunks { get; set; } = new();
    }

    private sealed class ChunkState
    {
        public int Id { get; set; }
        public long Start { get; set; }
        public long End { get; set; }
        public long Downloaded { get; set; }
    }

    public static async Task DownloadToFileAsync(
        HttpClient http, string url, string finalPath,
        IProgress<DownloadProgress>? progress, CancellationToken ct,
        string? resolvingMessage = null)
    {
        var temp = finalPath + ".part";
        var statePath = finalPath + ".state";

        long existing = File.Exists(temp) ? new FileInfo(temp).Length : 0;
        bool hasState = File.Exists(statePath);

        // Report the already-downloaded bytes up front so the UI's progress bar
        // starts where the file actually is, not at zero.
        progress?.Report(new DownloadProgress(DownloadPhase.Downloading, existing, null, resolvingMessage ?? "Downloading"));

        // Resume authority: a valid state file already knows the exact total and
        // URL, so skip the preflight request entirely (like aria2/ABDM resume).
        if (existing > 0 && hasState && TryReadStateTotal(statePath) is { } stateTotal)
        {
            await DownloadSegmentedStatefulAsync(http, url, temp, statePath, stateTotal, progress, ct).ConfigureAwait(false);
            Finish(temp, finalPath, statePath, progress);
            return;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);

        if (existing > 0)
        {
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
        }

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        bool acceptsRanges = resp.Headers.AcceptRanges.Contains("bytes") || resp.StatusCode == System.Net.HttpStatusCode.PartialContent;
        long? total = resp.Content.Headers.ContentLength;

        if (resp.StatusCode == System.Net.HttpStatusCode.PartialContent)
        {
            total += existing;
        }
        else if (existing > 0)
        {
            existing = 0; // Server ignored the Range header — restart the file
        }

        try
        {
            // Segmented mode ONLY from a clean start or with a matching state file.
            // A partial single-stream file has no per-chunk bookkeeping; switching
            // strategies here would truncate the .part file and throw away progress.
            if (total is > 2_000_000 && acceptsRanges && (existing == 0 || hasState))
            {
                resp.Dispose(); // Free the initial connection for the parallel chunks
                await DownloadSegmentedStatefulAsync(http, url, temp, statePath, total.Value, progress, ct).ConfigureAwait(false);
            }
            else
            {
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await DownloadSingleStreamAsync(src, temp, total, existing, progress, ct).ConfigureAwait(false);
            }

            Finish(temp, finalPath, statePath, progress);
        }
        catch (OperationCanceledException)
        {
            throw; // Preserve .part and .state so a later resume continues
        }
        catch
        {
            // On hard errors (not cancellation), clean up to avoid corrupted state
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
            try { if (File.Exists(statePath)) File.Delete(statePath); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>Atomic rename of the finished .part into place + final progress report.</summary>
    private static void Finish(string temp, string finalPath, string statePath, IProgress<DownloadProgress>? progress)
    {
        File.Move(temp, finalPath, overwrite: true);
        if (File.Exists(statePath)) File.Delete(statePath);
        progress?.Report(new DownloadProgress(DownloadPhase.Downloading,
            new FileInfo(finalPath).Length, new FileInfo(finalPath).Length));
    }

    /// <summary>Peek at the persisted total so resume can skip the preflight request.</summary>
    private static long? TryReadStateTotal(string statePath)
    {
        try
        {
            return JsonSerializer.Deserialize<DownloadState>(File.ReadAllText(statePath))?.TotalBytes;
        }
        catch { return null; }
    }

    private static async Task DownloadSegmentedStatefulAsync(
        HttpClient http, string url, string temp, string statePath, long total,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        DownloadState? state = null;
        if (File.Exists(statePath) && File.Exists(temp))
        {
            // Only trust the state when the .part file matches the recorded size
            // exactly (preallocated file) — anything else is a torn/interrupted write.
            if (new FileInfo(temp).Length == total)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(statePath, ct).ConfigureAwait(false);
                    state = JsonSerializer.Deserialize<DownloadState>(json);
                    if (state?.TotalBytes != total) state = null; // target file changed on the server
                    else if (state.Url != url) state = null;      // different URL — never mix files
                }
                catch { state = null; }
            }
        }

        // Per-download semaphore so state saves never interleave.
        using var stateSemaphore = new SemaphoreSlim(1, 1);

        if (state == null)
        {
            state = new DownloadState { TotalBytes = total, Url = url };
            long chunkSize = total / Segments;
            for (int i = 0; i < Segments; i++)
            {
                long start = i * chunkSize;
                long end = (i == Segments - 1) ? total - 1 : (i + 1) * chunkSize - 1;
                state.Chunks.Add(new ChunkState { Id = i, Start = start, End = end, Downloaded = 0 });
            }

            using (var preallocate = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.Write, 81920, useAsync: true))
            {
                preallocate.SetLength(total);
            }
            await SaveStateAsync(statePath, state, ct).ConfigureAwait(false);
        }

        // Baseline for progress: everything already written across all chunks.
        long globalDownloaded = state.Chunks.Sum(c => c.Downloaded);
        var reportLock = new object();
        var lastReport = DateTime.UtcNow;

        // One chunk = one Range request. Transient failures (5xx, dropped
        // connection, premature EOF) retry the REMAINING range with backoff —
        // the same per-piece resilience aria2/ABDM give you.
        async Task DownloadChunkAsync(ChunkState chunk)
        {
            long currentStart = chunk.Start + chunk.Downloaded;
            if (currentStart > chunk.End) return;

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(currentStart, chunk.End);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.Write, 81920, useAsync: true)
            {
                Position = currentStart
            };

            var buffer = new byte[81920];
            int read;
            DateTime lastStateSave = DateTime.UtcNow;

            while ((read = await ReadWithWatchdogAsync(src, buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);

                lock (reportLock)
                {
                    chunk.Downloaded += read;
                    globalDownloaded += read;

                    if (progress != null && (DateTime.UtcNow - lastReport).TotalMilliseconds >= 120)
                    {
                        lastReport = DateTime.UtcNow;
                        progress.Report(new DownloadProgress(DownloadPhase.Downloading, globalDownloaded, total));
                    }
                }

                // Persist offsets periodically (non-blocking; drop the write if busy).
                if ((DateTime.UtcNow - lastStateSave).TotalSeconds >= 2)
                {
                    lastStateSave = DateTime.UtcNow;
                    if (stateSemaphore.Wait(0))
                    {
                        try
                        {
                            await SaveStateAsync(statePath, state, ct).ConfigureAwait(false);
                        }
                        finally
                        {
                            stateSemaphore.Release();
                        }
                    }
                }
            }

            await stateSemaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await SaveStateAsync(statePath, state, ct).ConfigureAwait(false); // final offsets for this chunk
            }
            finally
            {
                stateSemaphore.Release();
            }

            if (chunk.Start + chunk.Downloaded - 1 != chunk.End)
                throw new IOException($"Chunk {chunk.Id} incomplete ({chunk.Downloaded}/{chunk.End - chunk.Start + 1} bytes).");
        }

        var tasks = state.Chunks
            .Where(c => c.Downloaded < (c.End - c.Start + 1))
            .Select(async chunk =>
            {
                Exception? last = null;
                for (var attempt = 1; attempt <= ChunkMaxAttempts; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        await DownloadChunkAsync(chunk).ConfigureAwait(false);
                        return;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        last = ex;
                        if (attempt == ChunkMaxAttempts) break;
                        await Task.Delay(TimeSpan.FromSeconds(attempt), ct).ConfigureAwait(false);
                    }
                }
                throw last ?? new IOException($"Chunk {chunk.Id} failed after {ChunkMaxAttempts} attempts.");
            });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    // State is saved fully async; a failure here must never kill the download.
    private static async Task SaveStateAsync(string path, DownloadState state, CancellationToken ct)
    {
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(state), ct).ConfigureAwait(false);
        }
        catch { /* best effort */ }
    }

    private static async Task DownloadSingleStreamAsync(
        System.IO.Stream src, string temp, long? total, long existing,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var mode = existing > 0 ? FileMode.Append : FileMode.Create;
        await using var dst = new FileStream(temp, mode, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        long done = existing;
        var last = DateTime.UtcNow;
        int read;

        while ((read = await ReadWithWatchdogAsync(src, buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            if (progress != null && (DateTime.UtcNow - last).TotalMilliseconds >= 120)
            {
                progress.Report(new DownloadProgress(DownloadPhase.Downloading, done, total > 0 ? total : null));
                last = DateTime.UtcNow;
            }
        }

        // The server promised a size but the connection ended early (premature
        // EOF). Shipping the truncated bytes as a "completed" download is how
        // unplayable files get into the library — fail instead so the caller's
        // provider chain can fall back to another source.
        if (total is { } expected && done != expected)
            throw new IOException($"Download truncated ({done}/{expected} bytes).");
    }

    /// <summary>
    /// Read that aborts when the stream produces no data for <see cref="StallTimeout"/>.
    /// The watchdog uses a per-read cancellation token so the abandoned read is always
    /// cancelled (never left writing into a reused buffer), and a caller cancellation
    /// surfaces as cancellation — never as a bogus "stalled" error, or pause/cancel
    /// would be misreported.
    /// </summary>
    private static async Task<int> ReadWithWatchdogAsync(System.IO.Stream src, byte[] buffer, CancellationToken ct)
    {
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(StallTimeout);
        try
        {
            return await src.ReadAsync(buffer, readCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"Download stalled (no data for {StallTimeout.TotalSeconds:0}s).");
        }
    }
}
