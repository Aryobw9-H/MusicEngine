namespace MusicEngine.Downloads;

using System.Text.Json;
using Models;

/// <summary>
/// Advanced multi-segment downloader inspired by AB Download Manager.
/// Features persistent state tracking, robust parallel chunking, and reliable resume capabilities.
/// </summary>
public static class HttpDownloader
{
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(45);
    private const int Segments = 8;

    private sealed class DownloadState
    {
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

        // Bug 6 Fix: initial progress reports existing downloaded bytes
        progress?.Report(new DownloadProgress(DownloadPhase.Downloading, existing, null, resolvingMessage ?? "Downloading"));

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        
        if (existing > 0 && !hasState)
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
        else if (existing > 0 && !hasState)
        {
            existing = 0; // Server ignored range, restart
        }

        try
        {
            if (total is > 2_000_000 && acceptsRanges)
            {
                resp.Dispose(); // Free initial connection for parallel chunks
                await DownloadSegmentedStatefulAsync(http, url, temp, statePath, total.Value, progress, ct).ConfigureAwait(false);
            }
            else
            {
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await DownloadSingleStreamAsync(src, temp, total, existing, progress, ct).ConfigureAwait(false);
            }
            
            // Bug 5 Fix: Atomic Move with overwrite: true instead of Delete -> Move
            File.Move(temp, finalPath, overwrite: true);
            if (File.Exists(statePath)) File.Delete(statePath);
        }
        catch (OperationCanceledException)
        {
            throw; // Preserve state and .part files for reliable resume
        }
        catch
        {
            // On hard errors (not cancellation), clean up to avoid corrupted state
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
            try { if (File.Exists(statePath)) File.Delete(statePath); } catch { /* best effort */ }
            throw;
        }

        progress?.Report(new DownloadProgress(DownloadPhase.Downloading,
            new FileInfo(finalPath).Length, new FileInfo(finalPath).Length));
    }

    private static async Task DownloadSegmentedStatefulAsync(
        HttpClient http, string url, string temp, string statePath, long total,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        DownloadState? state = null;
        if (File.Exists(statePath) && File.Exists(temp))
        {
            // Bug 4 Fix: Ensure .part size perfectly matches the originally requested state size
            if (new FileInfo(temp).Length == total)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(statePath, ct).ConfigureAwait(false);
                    state = JsonSerializer.Deserialize<DownloadState>(json);
                    if (state?.TotalBytes != total) state = null; // Target file changed on server
                }
                catch { state = null; }
            }
        }

        // Bug 1 Fix: Per-download semaphore instead of a static lock
        using var stateSemaphore = new SemaphoreSlim(1, 1);

        if (state == null)
        {
            state = new DownloadState { TotalBytes = total };
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

        // Bug 3 Note: Summing Downloaded on resume provides correct baseline for later incremental additions
        long globalDownloaded = state.Chunks.Sum(c => c.Downloaded);
        var reportLock = new object();
        var lastReport = DateTime.UtcNow;

        var tasks = state.Chunks.Where(c => c.Downloaded < (c.End - c.Start + 1)).Select(async chunk =>
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

                // Throttle disk I/O for state saving
                if ((DateTime.UtcNow - lastStateSave).TotalSeconds >= 2)
                {
                    lastStateSave = DateTime.UtcNow;
                    if (stateSemaphore.Wait(0)) // Non-blocking attempt to save state; drop if busy
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
                await SaveStateAsync(statePath, state, ct).ConfigureAwait(false); // Final chunk save
            }
            finally
            {
                stateSemaphore.Release();
            }
            
            if (chunk.Start + chunk.Downloaded - 1 != chunk.End)
                throw new IOException($"Chunk {chunk.Id} incomplete ({chunk.Downloaded}/{chunk.End - chunk.Start + 1} bytes).");
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    // Bug 2 Fix: state is saved fully asynchronously without thread pooling sync waits
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
