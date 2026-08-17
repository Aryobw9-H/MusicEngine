# Download System — Critical Issues Analysis

## Executive Summary
The download system has **fundamental architectural flaws** in pause/resume, cancellation, and progress reporting. The "fixes" applied earlier were surface-level; the core pipeline doesn't support resume semantics.

---

## 1. DownloadManager.cs — Core Issues

### 1.1 Resume is a Lie (Lines 99-111)
```csharp
public bool Resume(string jobId)
{
    if (_jobs.TryGetValue(jobId, out var job) && !_active.ContainsKey(jobId))
    {
        _paused.TryRemove(jobId, out _);
        var tcs = new TaskCompletionSource<DownloadProgress>(...);
        var cts = new CancellationTokenSource();
        _active[jobId] = cts;
        _queue.Writer.TryWrite((job, tcs, cts));  // ← RE-QUEUES FROM SCRATCH
        return true;
    }
    return false;
}
```
**Problem**: Creates NEW `TaskCompletionSource`, NEW `CancellationTokenSource`, re-queues the job. The `RunJobAsync` runs from the beginning — re-resolves candidates, re-checks existing files, re-starts provider chain. Only `HttpDownloader` accidentally resumes because it sees `.part` file.

**Required**: Resume must pass "resume context" (which provider, which candidate, byte offset) so `RunJobAsync` can skip to the download phase.

### 1.2 Pause = Cancel + Flag (Lines 89-97)
```csharp
public bool Pause(string jobId)
{
    if (_active.TryGetValue(jobId, out var cts))
    {
        _paused[jobId] = true;
        return CancelCore(cts);  // ← Just cancels token
    }
    return false;
}
```
**Problem**: No pause semantics — just kills the token. Provider's `DownloadAsync` sees cancellation, throws `OperationCanceledException`, worker catches it and reports `Paused` phase. But all progress state (which provider, which segment, byte offset) is lost.

### 1.3 Watchdog Race Condition (Lines 197-211)
```csharp
using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
var resolveTask = provider.DownloadAsync(candidate, options, progress, watchdogCts.Token);

var watchdog = Task.Run(async () =>  // ← NOT AWAITED!
{
    while (!resolveTask.IsCompleted)
    {
        await Task.Delay(5000, watchdogCts.Token);
        if (progress.TimeSinceLastProgress > TimeSpan.FromSeconds(60))
        {
            watchdogCts.Cancel();
            break;
        }
    }
});
```
**Problems**:
- `watchdog` task is fire-and-forget — if download completes, watchdog keeps running until next 5s tick
- `watchdogCts` cancellation propagates to provider, but provider may not respect linked token properly
- No cleanup of watchdog on early exit (exception, cancel)

### 1.4 ProgressProxy Throttle Drops Updates (Lines 397-412)
```csharp
public void Report(DownloadProgress value)
{
    _last = value;
    _lastProgress = DateTime.UtcNow;
    if (value.Phase is terminal) { _event?.Invoke(...); return; }
    if ((DateTime.UtcNow - _lastEmit).TotalMilliseconds >= 120)  // ← 120ms throttle
    {
        _lastEmit = DateTime.UtcNow;
        _event?.Invoke(_jobId, value);
    }
}
```
**Problem**: During segmented download (4 parallel segments), each segment reports progress but 120ms throttle means ~83% of updates dropped. UI sees jerky progress.

### 1.5 No Resume Context in Job/Provider Chain
- `EnqueuedJob` record has no "resume state" field
- `IDownloadProvider.DownloadAsync` signature: `(SearchResult, DownloadOptions, IProgress, CancellationToken)` — no offset/resume parameter
- Providers (`YtDlpProvider`, `PersianIndexProvider`, `SoundCloudProvider`, etc.) don't implement resume

### 1.6 _jobs Dictionary Never Cleared on Success (Line 126-127)
```csharp
var final = await RunJobAsync(job, tcs, linked.Token).ConfigureAwait(false);
tcs.TrySetResult(final);
// _jobs.TryRemove(job.Id, out _)  ← MISSING!
```
Completed jobs stay in `_jobs` forever → memory leak, resume logic gets confused.

### 1.7 Cancel Doesn't Handle Already-Paused Jobs (Line 82-87)
```csharp
public bool Cancel(string jobId)
{
    _jobs.TryRemove(jobId, out _);
    _paused.TryRemove(jobId, out _);
    return _active.TryGetValue(jobId, out var cts) && CancelCore(cts);
}
```
If job is paused, it's NOT in `_active` (removed in worker finally block). Cancel returns `false` silently.

---

## 2. HttpDownloader.cs — Resume Only Works for Single-Stream

### 2.1 Segmented Download Ignores Existing .part (Lines 67-117)
```csharp
private static async Task DownloadSegmentedAsync(...)
{
    using (var preallocate = new FileStream(temp, FileMode.Create, ...))  // ← OVERWRITES!
        preallocate.SetLength(total);
    // Spawns 4 segments, each writes to its range
}
```
**Problem**: `FileMode.Create` truncates existing `.part` file. No logic to:
- Read existing segments to determine what's downloaded
- Only request missing ranges
- Resume partial segments

### 2.2 Single-Stream Resume Works (Lines 24-64)
```csharp
long existing = File.Exists(temp) ? new FileInfo(temp).Length : 0;
if (existing > 0) req.Headers.Range = new RangeHeaderValue(existing, null);
// ...
if (resp.StatusCode != HttpStatusCode.PartialContent) existing = 0;
```
This path correctly resumes. But segmented path (for files >2MB with Accept-Ranges) doesn't.

---

## 3. MainViewModel.cs — UI/State Sync Issues

### 3.1 JobProgress Handler Ignores Paused Phase (Lines 192-221)
```csharp
_downloads.JobProgress += (id, p) => _ui.Run(() =>
{
    // ...
    if (p.Phase is DownloadPhase.Completed or DownloadPhase.AlreadyOwned
        or DownloadPhase.Failed or DownloadPhase.Cancelled)
    {
        // Handles terminal phases
    }
    // NO handling for DownloadPhase.Paused!
});
```
UI never updates to show "Paused" state, pause/resume buttons don't toggle.

### 3.2 _queuedWorks Blocks Re-download After Pause (Line 407-409)
```csharp
public void Download(TrackItemViewModel track)
{
    var key = track.Work.Representative.DedupKey;
    if (!_queuedWorks.Add(key)) return;  // ← Blocks if already queued
    _ = _downloads.EnqueueAsync(track.Work);
}
```
When paused, `_queuedWorks` still has the key. User can't click "Download" again to retry, and Resume button is broken anyway.

### 3.3 RecordHistory Key Mismatch (Lines 470-472)
```csharp
var key = $"{title}|{artist}".Trim();
var match = Results.FirstOrDefault(r => r.Work.Representative.DedupKey == key);
```
`DedupKey` format in `SearchResult` might not be `title|artist`. Could be `provider::id` or URL-based.

### 3.4 Duplicate Command Declarations (Lines 120-132 vs 167-183)
Commands declared as properties AND initialized in constructor — works but messy.

---

## 4. TrackItemViewModel.cs — Minor

### 4.1 Apply Handles Paused But No Visual Feedback
```csharp
public bool IsPaused => Phase == DownloadPhase.Paused;
// Apply() calls OnPropertyChanged(nameof(IsPaused)) — OK
```
But XAML binds `Visibility="{Binding IsPaused, Converter={StaticResource BoolToVis}}"` for Resume button — this works IF JobProgress fires for Paused phase (it doesn't, see 3.1).

---

## 5. Provider Implementations — No Resume Support

| Provider | Resume Support | Cancel Support |
|----------|---------------|----------------|
| `YtDlpProvider` | ❌ No (yt-dlp doesn't support resume) | ✅ Process.Kill on cancel |
| `PersianIndexProvider` | ❌ No (python script) | ✅ Process.Kill added |
| `SoundCloudProvider` | ❌ No | ? Uses HttpDownloader (single-stream only) |
| `RadioJavanProvider` | ❌ No | ? Uses HttpDownloader |
| `Nex1MusicProvider` | ❌ No | ? |
| `PersianSitesProvider` | ❌ No | ? Uses HttpDownloader |

**Fundamental**: `IDownloadProvider.DownloadAsync` interface has no resume parameter. Adding resume requires interface change + all provider implementations.

---

## 6. Architecture-Level Problems

### 6.1 Two Different "Download" Concepts Conflated
1. **Provider-level download**: `IDownloadProvider.DownloadAsync` — fetches from source to file
2. **Manager-level download**: `DownloadManager.EnqueueAsync` — orchestrates fallback chain, retries, tagging

Pause/resume only makes sense at Manager level, but Manager doesn't track which provider/candidate was active.

### 6.2 No Persistent Job State
Job state lives only in memory (`_jobs`, `_active`, `_paused`). App restart loses all pause/resume capability. For true resume, job state (provider, candidate, offset, temp file path) must be persisted.

### 6.3 Segmented Download Incompatible with Resume
Parallel segments write to different file ranges concurrently. On resume:
- Need to know which segments completed
- Need to verify segment integrity (checksums?)
- Need to re-request only missing ranges
- Current architecture: "fire 4 tasks, wait all" — no segment-level tracking

---

## 7. Minimal Fixes vs Proper Rewrite

### Minimal Fixes (Band-aids)
1. Fix `ProgressProxy` to not throttle `Downloading` phase (or throttle less aggressively)
2. Make `JobProgress` handler in `MainViewModel` handle `Paused` phase
3. Clear `_queuedWorks` on Pause so user can re-download
4. Fix `Cancel` to work on paused jobs (check `_paused` dict)
5. Remove completed jobs from `_jobs` dictionary
6. Await watchdog task or use `Task.WhenAny` properly

### Proper Rewrite Required For Real Pause/Resume
1. **New `IDownloadProvider` interface** with `DownloadAsync(..., long resumeOffset = 0)`
2. **Job state persistence** (SQLite/JSON): jobId, provider, candidate, tempPath, downloadedBytes, phase
3. **Resume-aware `RunJobAsync`**: accepts `resumeContext`, skips resolving if already has provider+offset
4. **Segmented download rewrite**: track per-segment state, support partial segment resume
5. **Provider implementations**: yt-dlp can't resume; Persian providers can't resume; only HTTP providers can

---

## 8. Immediate Action Items

| Priority | Issue | File | Effort |
|----------|-------|------|--------|
| P0 | JobProgress ignores Paused phase | MainViewModel.cs | 5 min |
| P0 | Cancel doesn't work on paused jobs | DownloadManager.cs | 10 min |
| P0 | _queuedWorks blocks re-download after pause | MainViewModel.cs | 5 min |
| P0 | Completed jobs leak in _jobs | DownloadManager.cs | 5 min |
| P1 | ProgressProxy throttle too aggressive for Downloading | DownloadManager.cs | 10 min |
| P1 | Watchdog task not awaited | DownloadManager.cs | 15 min |
| P2 | Segmented download doesn't resume | HttpDownloader.cs | 2-4 hrs |
| P3 | Provider resume interface + implementations | All providers | 1-2 days |

---

## 9. Recommendation

**Don't build pause/resume on current architecture.** It requires:
1. Interface changes across 6+ providers
2. Job state persistence layer
3. Segmented download rewrite
4. Manager-level resume orchestration

**Instead**: 
- Fix the P0/P1 issues above (make cancel reliable, UI responsive, no leaks)
- Add "Restart Download" button (clears state, re-queues fresh) — user-visible, works reliably
- Document that pause/resume is not supported; yt-dlp and Persian providers fundamentally can't do it
- If resume is critical, design v2 with proper job state machine + persistent storage