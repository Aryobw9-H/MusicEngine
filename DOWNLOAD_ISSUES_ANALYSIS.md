# Download System Issues — Analysis & Fix Plan

## Problems Identified

### 1. No Pause/Resume Button
- **Missing entirely**: `DownloadManager`, `HttpDownloader`, and all providers only support `Cancel`, not `Pause`.
- The `DownloadPhase` enum has `Queued, Resolving, Downloading, Tagging, Completed, Failed, Cancelled, AlreadyOwned` — no `Paused`.
- UI has no pause button in the downloads list template.

### 2. Cancel Doesn't Work Reliably
- `DownloadManager.CancelCore` just calls `cts.Cancel()` but:
  - `HttpDownloader.ReadWithWatchdogAsync` respects `CancellationToken` ✓
  - `HttpDownloader.DownloadSegmentedAsync` passes `ct` to `SendAsync` and `ReadAsync` ✓
  - **BUT** `YtDlpProvider` uses `CliWrap.ExecuteAsync(ct)` — CliWrap **does not reliably cancel** the child process on token cancellation. The yt-dlp process keeps running in background.
  - Other providers (`SoundCloud`, `RadioJavan`, `Nex1Music`, `PersianIndex`) may not check `ct` in all their async calls.
  - `DownloadManager.WorkerLoopAsync` creates a `linked` token but providers don't necessarily use it for all subprocesses.

### 3. Completed Downloads Don't Show as "Downloaded" in Results List
- `MainViewModel.RecordHistory` (lines 448-471) tries to find the matching result in `Results` by **exact string match** on Title + Artist:
  ```csharp
  var match = Results.FirstOrDefault(r =>
      string.Equals(r.Title, title, StringComparison.OrdinalIgnoreCase)
      && string.Equals(r.Artist, artist, StringComparison.OrdinalIgnoreCase));
  ```
  - If metadata differs slightly (e.g., "Tataloo" vs "تتلو"), match fails → `IsInLibrary` stays false.
- The `Results` collection is separate from `DownloadQueue` — no automatic sync.
- `_queuedWorks` `HashSet` tracks "already queued" by `DedupKey` (provider::id or URL). **Never cleared on failure**, so failed downloads can't be retried.

### 4. Downloads "Get Stuck"
Possible causes:
- `ProgressProxy` throttles UI events to 120ms — final `Completed` event **is emitted** (line 320-324), but if the UI thread is busy, it might not process.
- `DownloadItemViewModel.Apply` sets `IsFinished` based on `Phase`, but `OnPropertyChanged(nameof(IsFinished))` is raised (line 216). If the ListBox doesn't re-evaluate visibility bindings (`IsFinished → Visibility`), the row stays in "active" state.
- `YtDlpProvider.DownloadOnceAsync` creates a temp work dir but if the process crashes/hangs, the dir isn't cleaned up and no progress is reported for minutes.
- No stall detection for the **resolving phase** (provider metadata lookup can hang).

---

## Root Causes Summary

| Issue | Location | Cause |
|-------|----------|-------|
| No pause | `DownloadManager`, all providers, XAML | Not implemented |
| Cancel unreliable | `YtDlpProvider` (CliWrap), other providers | Cancellation not propagated to subprocesses |
| Results not marked downloaded | `MainViewModel.RecordHistory` | Fragile string match; `_queuedWorks` never cleared on failure |
| Stuck downloads | `ProgressProxy` throttling, `YtDlpProvider` hangs, no resolving timeout | Multiple |

---

## Fix Plan (Prioritized)

### Phase 1: Reliable Cancel + Retry Failed Downloads (High Impact, Low Effort)
1. **Clear `_queuedWorks` on failure/cancel** in `MainViewModel` so failed downloads can be retried.
2. **Fix `RecordHistory` matching** — use `DedupKey` (stable) instead of title/artist strings.
3. **Make `YtDlpProvider` actually kill the process on cancellation** — use `Process.Kill()` when `ct` fires.
4. **Add cancellation checks** to all provider `DownloadAsync` methods.

### Phase 2: Pause/Resume Support (Medium Effort)
1. Add `Paused` to `DownloadPhase`.
2. Add `Pause(string jobId)` / `Resume(string jobId)` to `DownloadManager`.
3. Implement pause in `HttpDownloader` (close streams, save position, resume with Range header).
4. Add pause button to XAML download row template.
5. Note: yt-dlp doesn't support pause/resume natively — would need to re-download or skip for yt-dlp jobs.

### Phase 3: Robust Progress + Stuck Detection (Low Effort)
1. **Emit final progress immediately** (bypass throttle) in `ProgressProxy`.
2. Add **resolving timeout** (e.g., 30s) in `DownloadManager.RunJobAsync`.
3. Ensure `DownloadItemViewModel` visibility bindings work correctly.

---

## Recommended Immediate Fixes (Do These First)

```csharp
// 1. In MainViewModel.JobProgress handler: clear _queuedWorks on failure/cancel
if (p.Phase is DownloadPhase.Failed or DownloadPhase.Cancelled)
{
    _queuedWorks.Remove(key);  // Allow retry
}

// 2. In RecordHistory: match by DedupKey instead of title/artist
var match = Results.FirstOrDefault(r => r.Work.Representative.DedupKey == key);

// 3. In YtDlpProvider: kill process on cancellation
// 4. Add resolving timeout in DownloadManager
```

---

## Files to Modify

1. `src/MusicEngine/Downloads/DownloadManager.cs` — cancel reliability, resolving timeout
2. `src/MusicEngine/Providers/YtDlpProvider.cs` — process kill on cancel
3. `src/MusicEngine/Providers/*.cs` — cancellation token propagation
4. `src/MusicEngine/App/ViewModels/MainViewModel.cs` — `_queuedWorks` clearing, better matching
5. `src/MusicEngine/Models/DownloadModels.cs` — add `Paused` phase (for Phase 2)
6. `src/MusicEngine.App/MainWindow.xaml` — pause button (Phase 2)
7. `src/MusicEngine/App/ViewModels/TrackItemViewModel.cs` — `IsInLibrary` logic

---

## Quick Wins to Ship Today

1. **Allow retry on failed downloads** (2 lines in MainViewModel)
2. **Fix "already downloaded" detection** (use DedupKey in RecordHistory)
3. **Kill yt-dlp process on cancel** (reliable cancel)
4. **Add resolving timeout** (prevent stuck "Resolving…")