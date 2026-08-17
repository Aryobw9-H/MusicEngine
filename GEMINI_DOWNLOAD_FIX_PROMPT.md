<System_Directives>
You are an expert reasoning and coding model.
1. If the request is ambiguous, lacks crucial information, or requires more file context, DO NOT GUESS. Output direct, clarifying questions.
2. If you have enough context, output ONLY the code using strict SEARCH/REPLACE blocks.
3. Omit all conversational text, pleasantries, and step-by-step explanations.
</System_Directives>

<Environment>
.NET 8 WPF application with two-project architecture:
- **MusicEngine** (net8.0, class library) — Core engine: providers, search, downloads, models, network. Zero WPF/System.Windows dependencies.
- **MusicEngine.App** (net8.0-windows, WPF) — UI layer: MainWindow, ViewModels, DI composition, WPF implementations of engine abstractions (IDispatcher, IArtworkLoader).

Key types:
- `DownloadManager` — Orchestrates download jobs, fallback chains, progress reporting via `IProgress<DownloadProgress>`
- `DownloadProgress` — Phase (Queued, Resolving, Downloading, Tagging, Completed, Failed, Cancelled, AlreadyOwned), bytes, total, message, filePath
- `IDownloadProvider.DownloadAsync(SearchResult, DownloadOptions, IProgress<DownloadProgress>, CancellationToken)`
- `HttpDownloader` — Segmented HTTP downloader with Range headers, cancellation support
- `YtDlpProvider` — Universal downloader via yt-dlp subprocess (replaced CliWrap with direct Process for reliable kill)
- `MainViewModel` — Coordinates search, downloads, queue, history; uses `_ui.Run(Action)` for UI marshaling
- `TrackItemViewModel` — Result row; `IsInLibrary` flag for "already downloaded" badge
- `DownloadItemViewModel` — Queue row; binds Percent, Status, SpeedText, EtaText, IsFinished, IsFailed

DI registrations in `App.xaml.cs`:
```csharp
services.AddSingleton<IDispatcher, Ui.WpfDispatcher>();
services.AddSingleton<IArtworkLoader, Http.ArtworkLoader>();
services.AddSingleton<MainViewModel>(sp => new MainViewModel(..., sp.GetRequiredService<IDispatcher>(), sp.GetRequiredService<IArtworkLoader>()));
```
</Environment>

<Hermes_Analysis>
The previous "quick wins" were incomplete. User reports:
1. **No pause button** — Never implemented. Need `Paused` phase, Pause/Resume in DownloadManager, Range-header resume in HttpDownloader, XAML button.
2. **Cancel buttons don't work** — YtDlpProvider now kills process, but other providers (SoundCloud, RadioJavan, Nex1Music, PersianIndex, PersianSites) may not propagate cancellation to all async calls. DownloadManager.CancelCore only cancels token; subprocesses may survive.
3. **Downloads don't show as finished** — RecordHistory uses DedupKey matching, but Results collection may not have the work yet (search replaced), or DownloadItemViewModel.IsFinished binding doesn't update visibility. ProgressProxy throttles to 120ms; final Completed event may not reach UI.
4. **Downloads get stuck** — 30s resolving timeout added, but no timeout on Downloading phase. Yt-dlp can hang on fragment retries. No stall detection.

Root cause: The download pipeline treats cancellation as cooperative but providers use external processes (yt-dlp, python for PersianIndex) or long-running HTTP streams that don't reliably abort on token.

Priority fixes:
1. Add `Paused` phase + Pause/Resume in DownloadManager + HttpDownloader resume (Range header) + XAML pause button
2. Make ALL providers respect cancellation: yt-dlp (done), PersianIndex (python subprocess), SoundCloud/RadioJavan/Nex1Music (HTTP streams), HttpDownloader (already respects)
3. Ensure Completed/Failed events ALWAYS reach UI (bypass ProgressProxy throttle for terminal phases)
4. Fix IsFinished/IsInLibrary binding: DownloadItemViewModel.Apply must raise PropertyChanged for IsFinished; MainWindow.xaml visibility converters must work
5. Add Downloading stall timeout (e.g., 60s no progress → fail and fallback)
</Hermes_Analysis>

<Objective>
Fix the download system completely:

1. **Add Pause/Resume support**
   - Add `Paused` to `DownloadPhase` enum
   - Add `Pause(string jobId)` and `Resume(string jobId)` to `DownloadManager`
   - Implement pause/resume in `HttpDownloader` using Range headers (save position, resume from byte offset)
   - Add pause button (⏸/▶) to download row in `MainWindow.xaml` (bind to new `PauseDownloadCommand`/`ResumeDownloadCommand`)
   - Note: yt-dlp jobs cannot pause/resume — show pause button disabled for yt-dlp, or skip to next provider on resume

2. **Make Cancel work reliably for ALL providers**
   - `YtDlpProvider` — already uses Process.Kill on cancel ✓
   - `PersianIndexProvider` — runs python subprocess; must kill process on cancel
   - `SoundCloudProvider`, `RadioJavanProvider`, `Nex1MusicProvider`, `PersianSitesProvider` — ensure all `HttpClient` calls pass `ct` and `HttpDownloader` calls pass `ct`
   - `DownloadManager.CancelCore` — after token cancel, wait briefly for graceful exit, then force-cleanup job state

3. **Ensure downloads show as finished in Results list**
   - `DownloadItemViewModel.Apply` — when Phase is Completed/Failed/Cancelled, raise `PropertyChanged(nameof(IsFinished))` and `PropertyChanged(nameof(IsFailed))`
   - `MainViewModel.RecordHistory` — after setting `match.IsInLibrary = true`, also update any `DownloadItemViewModel` in queue with same DedupKey
   - `ProgressProxy` — for terminal phases (Completed, Failed, Cancelled, AlreadyOwned), bypass throttle and report immediately
   - Verify `MainWindow.xaml` visibility converters: `IsFinished → Visibility` for "Open"/"Folder" buttons, `IsFailed → Foreground` for red status

4. **Prevent stuck downloads**
   - Add Downloading stall timeout: if no progress for 60s → report Failed, trigger fallback provider
   - Keep 30s Resolving timeout (already added)
   - Clean up temp directories on any failure/cancel

Files to modify:
- `src/MusicEngine/Models/DownloadModels.cs` — add Paused phase
- `src/MusicEngine/Downloads/DownloadManager.cs` — Pause/Resume, stall timeout, terminal-phase bypass
- `src/MusicEngine/Downloads/HttpDownloader.cs` — pause/resume with Range headers
- `src/MusicEngine/Providers/YtDlpProvider.cs` — verify cancel works (already done)
- `src/MusicEngine/Providers/PersianIndexProvider.cs` — kill python process on cancel
- `src/MusicEngine/Providers/SoundCloudProvider.cs`, `RadioJavanProvider.cs`, `Nex1MusicProvider.cs`, `PersianSitesProvider.cs` — verify ct propagation
- `src/MusicEngine.App/ViewModels/MainViewModel.cs` — Pause/Resume commands, RecordHistory sync, ProgressProxy bypass
- `src/MusicEngine.App/ViewModels/DownloadItemViewModel.cs` — IsFinished/IsFailed PropertyChanged
- `src/MusicEngine.App/MainWindow.xaml` — pause button in download row template
</Objective>

<Code_State>
[src/MusicEngine/Models/DownloadModels.cs]
```csharp
public enum DownloadPhase
{
    Queued,
    Resolving,
    Downloading,
    Tagging,
    Completed,
    Failed,
    Cancelled,
    AlreadyOwned,
    // ADD: Paused
}
```

[src/MusicEngine/Downloads/DownloadManager.cs] — key sections
```csharp
// CancelCore (line ~400)
public void CancelCore(string jobId)
{
    if (_jobs.TryGetValue(jobId, out var job))
    {
        job.Cts.Cancel();
    }
}

// WorkerLoopAsync — processes queue, calls RunJobAsync
// RunJobAsync — has 30s resolving timeout, needs downloading stall timeout
// ProgressProxy — throttles to 120ms, needs bypass for terminal phases
```

[src/MusicEngine/Downloads/HttpDownloader.cs] — segmented download
```csharp
public static async Task DownloadToFileAsync(HttpClient http, string url, string path, IProgress<DownloadProgress>? progress, CancellationToken ct)
{
    // Uses Range headers for segments; supports resume if given start offset
}
```

[src/MusicEngine/Providers/YtDlpProvider.cs] — DownloadOnceAsync uses Process with cancelReg.Kill()

[src/MusicEngine/Providers/PersianIndexProvider.cs] — RunPyAsync starts python process, no kill on cancel

[src/MusicEngine/App/ViewModels/MainViewModel.cs] — JobProgress handler (lines ~187-209)
```csharp
_downloads.JobProgress += (id, p) => _ui.Run(() =>
{
    var item = DownloadQueue.FirstOrDefault(d => d.JobId == id);
    if (item is null) return;
    item.Apply(p, _jobProvider.TryGetValue(id, out var prov) ? prov : "");
    if (p.Phase is DownloadPhase.Completed or DownloadPhase.AlreadyOwned or DownloadPhase.Failed or DownloadPhase.Cancelled)
    {
        // ... RecordHistory called here
    }
});
```

[src/MusicEngine/App/ViewModels/DownloadItemViewModel.cs] — Apply method
```csharp
public void Apply(DownloadProgress p, string provider)
{
    Phase = p.Phase;
    Percent = p.Total > 0 ? (int)(p.Bytes * 100 / p.Total) : 0;
    // ... sets Status, SpeedText, EtaText
    // MISSING: OnPropertyChanged(nameof(IsFinished)), OnPropertyChanged(nameof(IsFailed))
}
public bool IsFinished => Phase is DownloadPhase.Completed or DownloadPhase.AlreadyOwned or DownloadPhase.Failed or DownloadPhase.Cancelled;
public bool IsFailed => Phase == DownloadPhase.Failed;
```

[src/MusicEngine/App/MainWindow.xaml] — download row template (lines ~409-455)
```xml
<StackPanel Grid.Column="3" Orientation="Horizontal" VerticalAlignment="Center">
    <Button Content="Open" ... Visibility="{Binding IsFinished, Converter={StaticResource BoolToVis}}"/>
    <Button Content="📁" ... Visibility="{Binding IsFinished, Converter={StaticResource BoolToVis}}"/>
    <Button Content="✕" Click="CancelDownload_Click" ToolTip="Cancel / remove"/>
    <!-- NEED: Pause/Resume button here -->
</StackPanel>
```
</Code_State>

<Output_Formatting_Rules>
CRITICAL: You must format your code changes exactly like the example below. 
- The search block MUST be a literal, exact copy of the existing code, including all spaces, indentation, and newlines. 
- Do not use `...` to skip lines. 
- The file path must be on a single line above the markdown fence.

[path/to/exact/file.ext]
```[language]
<<<<<<< SEARCH
[Exact, literal lines of the original code]
=======
[New, modified code lines]
>>>>>>> REPLACE
```
</Output_Formatting_Rules>