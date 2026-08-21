# 📋 WPF Codebase Audit & Improvement Roadmap

> **Project:** MusicEngine v2 — WPF (.NET 8) music search & downloader
> **Audit date:** 2026-08-18
> **Scope:** `src/MusicEngine` (engine, net8.0), `src/MusicEngine.App` (WPF, net8.0-windows), `tests/MusicEngine.Tests`
> **Mode:** read-only static audit. No source file was modified in producing this document.

---

## 1. Executive Summary & Health Matrix

### 1.1 System overview

MusicEngine is a two-project solution with a genuinely good separation at the assembly boundary: `MusicEngine` is a UI-free engine (no WPF references) and `MusicEngine.App` is a hand-rolled MVVM WPF shell with a DI composition root in `App.xaml.cs`. The engine's core is a streaming search pipeline (`SearchService`) that fans out to nine providers across three tiers (catalog / display / download-only), identifies the *intended* song via iTunes+Deezer ("goal resolution"), gates every other provider's rows against that identity, groups survivors into `TrackWork` clusters, and streams them to the UI as `IAsyncEnumerable<TrackWork>`. Downloads run through an unbounded `Channel<T>` worker pool (`DownloadManager`) onto a resumable segmented HTTP downloader (`HttpDownloader`, 8 segments + `.part`/`.state` sidecars), then through TagLib# tagging. A per-host reachability layer (`Reachability` + `RoutingHandler`) decides direct/proxy/dead per host, which is the right architecture for the Iranian network conditions this app targets.

The **architecture is sound**; the defects are concentrated in three places: (a) cancellation and lifetime discipline in the async fan-out and probe layers, (b) synchronous I/O and blocking probes executed on the UI thread or during DI construction, and (c) a WPF layer where the View owns behaviour that belongs to the ViewModel, plus per-item visual effects that fight virtualization.

### 1.2 Health matrix

| Dimension | 🔴 Critical | 🟠 High | 🟡 Medium | 🔵 Low | Total |
|---|:--:|:--:|:--:|:--:|:--:|
| Bugs & concurrency (`BUG-*`) | 4 | 6 | 4 | 1 | **15** |
| WPF / MVVM anti-patterns (`MVVM-*`) | 0 | 3 | 4 | 3 | **10** |
| XAML & rendering (`XAML-*`) | 0 | 2 | 3 | 2 | **7** |
| Performance & UX (`PERF-*`) | 1 | 3 | 3 | 0 | **7** |
| Code quality & modernization (`MODERN-*`) | 0 | 3 | 2 | 4 | **9** |
| Features & enhancements (`FEAT-*`) | 0 | 1 | 3 | 2 | **6** |
| **Total** | **5** | **18** | **19** | **12** | **54** |

One of the four critical bugs (`BUG-13`, unconditional TLS bypass) is a security finding rather than a correctness one; it is catalogued under `BUG-*` because it lives in the same file family as the other HTTP defects.

### 1.3 Component health

| Component | Grade | Note |
|---|:--:|---|
| `Search/SearchService.cs` | C+ | Excellent design, one critical leak in the fan-out deadline; 660 lines in one class |
| `Network/Reachability.cs` | C | Cancellation swallowed → false `Dead` verdicts; probe Task bound to first caller's token |
| `Downloads/HttpDownloader.cs` | B− | Well-tested resume logic; watchdog abandons a read that still owns the buffer |
| `Downloads/DownloadManager.cs` | B | Solid channel/worker design; watchdog CTS lifetime is delicate |
| `Providers/*` | B− | Consistent shape; two providers block during construction; non-static regexes |
| `Text/*` | B | Good algorithms, hot-path linear dictionary scans, unbounded cache |
| `Configuration/*` | D+ | Synchronous, non-atomic writes; no abstraction; loaded three times from disk |
| `ViewModels/MainViewModel.cs` | C | 658-line god object doing search, playback, queue, clipboard, toasts, persistence |
| `MainWindow.xaml.cs` | C− | 240 lines of code-behind mutating controls directly; `async void` handlers |
| `MainWindow.xaml` | C+ | Virtualization enabled, but per-item shadows/masks/storyboards undo the win |
| `SettingsWindow.xaml.cs` | D | No ViewModel; runtime `FrameworkElementFactory` templates; re-reads config from disk |
| `tests/MusicEngine.Tests` | C | Real, valuable download-engine tests — but a hand-rolled console harness, not a test framework |

### 1.4 Top 3 highest-priority risks

1. **`SearchService.cs:643-659` — abandoned provider tasks.** The hard deadline uses `Task.WhenAll(tasks).WaitAsync(grace, CancellationToken.None)`. When the grace expires the search returns, but every straggling provider task keeps running: HTTP sockets stay open, `yt-dlp`/python child processes keep working, and `onBatch` can still fire into a `results` list the caller has already consumed. Every slow search leaks work that accumulates for the process lifetime.
2. **`HttpDownloader.cs:331-345` — watchdog abandons a read that still owns the buffer.** `ReadWithWatchdogAsync` races `src.ReadAsync(buffer)` against `Task.Delay(stall)`. On stall it throws, but the abandoned `ReadAsync` retains a reference to `buffer` and may complete afterwards, writing into a buffer the caller no longer considers valid. Combined with 8 concurrent writers into a preallocated `.part` and a `.state` file that is only validated by total-size + URL, this is the app's most plausible path to silent file corruption.
3. **UI-thread synchronous, non-atomic persistence.** `MainViewModel.cs:274` (`_state.PushSearch`) and `:519` (`_state.PushHistory`) each perform a blocking `File.WriteAllText` on the dispatcher thread, as does `AppConfig.Save()` at `:591`/`:599`. Every search and every completed download stalls the UI for a disk write, and because the writes are not temp-file+rename, a crash or power loss mid-write leaves a truncated `state.json`/`appsettings.json`.

---

## 2. Detailed Findings Catalog

### 2.1 Bugs & Concurrency (`BUG-*`)

---

#### `BUG-01: Fan-out grace deadline abandons provider tasks without cancelling them`

**Severity:** 🔴 Critical — resource leak, ghost callbacks, unbounded socket/process growth.

**File & location:** `src/MusicEngine/Search/SearchService.cs:643-659` (also the task body at `:600-641`).

**Root cause & diagnosis:**
`CollectAsync` builds one `Task.Run` per provider plan, then enforces a hard deadline:

```csharp
var grace = timeout + timeout + TimeSpan.FromSeconds(4);
try { await Task.WhenAll(tasks).WaitAsync(grace, CancellationToken.None).ConfigureAwait(false); }
catch (TimeoutException) { _logger.LogWarning("Fan-out grace deadline hit…"); }
```

`WaitAsync` only stops *waiting*; it does not stop the work. Passing `CancellationToken.None` explicitly means even the caller's cancellation cannot shorten the wait. Three consequences:

1. Straggling providers keep holding HTTP connections, python sidecar processes (`PersianIndexProvider`) and `yt-dlp` processes (`YtDlpProvider`) after the search "finished". Repeat 20 searches in a session and the leaked work compounds.
2. The abandoned task body still executes `lock (results) { results.AddRange(itemResults); onBatch(results.ToArray()); }`. `onBatch` marshals to the UI via `MainViewModel`, so a *previous* search can push rows into the UI while a *new* search is running.
3. `_health.RecordFailure/RecordSuccess` is recorded late, corrupting the health monitor's quiesce decisions.

The comment above the code is correct about *why* a hard deadline is needed (YoutubeExplode ignoring its token) — the fix is not to remove the deadline but to cancel and detach on expiry.

**Recommended solution:**
Introduce a fan-out-scoped `CancellationTokenSource` linked to `ct`, pass its token into every provider task, and `Cancel()` it in the `TimeoutException` handler so abandoned work tears itself down. Guard the batch callback with a `volatile bool _closed` (or capture a generation counter) so post-deadline tasks cannot invoke `onBatch`. Do not dispose the linked CTS until the abandoned tasks are observed — attach a continuation (`Task.WhenAll(tasks).ContinueWith(_ => cts.Dispose(), TaskScheduler.Default)`) instead of a `using`. Consider `Task.WhenAll(tasks).WaitAsync(grace, ct)` so caller cancellation also short-circuits.

**Ready-to-use agent prompt:**
> In `src/MusicEngine/Search/SearchService.cs`, fix the fan-out hard deadline in `CollectAsync` (around lines 600-659) so abandoned provider tasks are cancelled and can no longer publish results. Steps: (1) create `var fanOutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);` before building the tasks and pass `fanOutCts.Token` (not `ct`) into each provider's `SearchAsync` call and into the `Task.Run(..., token)` overload; (2) add a `var closed = false;` flag captured by the tasks, and inside `lock (results)` only call `onBatch` when `!closed`; (3) change the deadline to `await Task.WhenAll(tasks).WaitAsync(grace, ct)`; (4) in the `catch (TimeoutException)` block set `closed = true` inside a `lock (results)` and call `fanOutCts.Cancel()`; (5) do NOT wrap `fanOutCts` in `using` — dispose it via `_ = Task.WhenAll(tasks).ContinueWith(_ => fanOutCts.Dispose(), TaskScheduler.Default);` so the still-running tasks keep a valid token. Preserve the existing per-provider `catch (OperationCanceledException) when (!ct.IsCancellationRequested)` health-recording behaviour. Do not change public method signatures. Then run `D:\dotnet-sdk\dotnet.exe build MusicEngine.sln -c Release` and `cd tests\MusicEngine.Tests && D:\dotnet-sdk\dotnet.exe run` and confirm all offline tests still pass.

#### `BUG-02: Stall watchdog abandons a ReadAsync that still owns the shared buffer`

**Severity:** 🔴 Critical — potential silent file corruption.

**File & location:** `src/MusicEngine/Downloads/HttpDownloader.cs:331-345`; consumers at `:211` (segmented chunk loop) and `:307` (single-stream loop).

**Root cause & diagnosis:**

```csharp
private static async Task<int> ReadWithWatchdogAsync(Stream src, byte[] buffer, CancellationToken ct)
{
    var readTask = src.ReadAsync(buffer, 0, buffer.Length, ct);
    var delayTask = Task.Delay(StallTimeout, ct);
    var done = await Task.WhenAny(readTask, delayTask).ConfigureAwait(false);
    …
}
```

When `delayTask` wins, the method throws to signal a stall — but `readTask` is still pending and still holds a live reference to `buffer`. Two hazards follow:

- **Buffer reuse.** The caller's `catch`/retry path may reallocate or reuse `buffer` for the retried range while the orphaned read completes into it, mixing bytes from two ranges.
- **Unobserved faults.** The abandoned `readTask` faults on socket teardown; nothing awaits it, so it surfaces later via `TaskScheduler.UnobservedTaskException` (which `App.xaml.cs:42` swallows into the crash log, hiding the real failure).

This matters more here than in a typical downloader because eight chunk tasks write concurrently into one preallocated `.part` file (`:176-179`, `:202`) with `FileShare.Write`, and resume trust is established only by "`.part` length == `TotalBytes`" plus a URL match (`:145-159`). Neither check would detect a range written with the wrong bytes. There is no per-chunk or whole-file checksum, so corruption survives to the tagged output.

**Recommended solution:**
Do not abandon the read — cancel it. Use a per-read `CancellationTokenSource` linked to `ct` with `CancelAfter(StallTimeout)`, pass its token to `ReadAsync`, and translate the resulting `OperationCanceledException` into the stall exception only when `ct` itself is not cancelled (the same pattern already used correctly in `Reachability.HttpAliveAsync`). This guarantees the read has completed (cancelled) before the buffer is reused. Separately, add integrity confidence: persist a rolling SHA-256 (or at minimum per-chunk byte counts *plus* a final `Content-Length` equality assert) and verify before `Finish()` promotes `.part` to the final path at `:122-128`.

**Ready-to-use agent prompt:**
> In `src/MusicEngine/Downloads/HttpDownloader.cs`, replace the `Task.WhenAny`-based `ReadWithWatchdogAsync` (around lines 331-345) with a cancellation-based watchdog so no orphaned read can write into a reused buffer. Implement it as: create `using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct); readCts.CancelAfter(StallTimeout);` then `try { return await src.ReadAsync(buffer.AsMemory(), readCts.Token).ConfigureAwait(false); } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException($"Read stalled for {StallTimeout.TotalSeconds:0}s"); }` and let a genuine caller cancellation propagate unchanged. Keep the method signature and both call sites (the segmented chunk loop and the single-stream loop) working as-is. Then run `cd tests\MusicEngine.Tests && D:\dotnet-sdk\dotnet.exe run` and confirm all four HttpDownloader tests (truncated, cancel/resume, URL-change, chunk-retry) still pass.

---

#### `BUG-03: Reachability swallows cancellation and reports hosts as Dead`

**Severity:** 🔴 Critical — wrongly disables working providers for the rest of the session.

**File & location:** `src/MusicEngine/Network/Reachability.cs:82-93` (`HttpAliveAsync`), consumed by `ProbeUncachedAsync` at `:68-80`.

**Root cause & diagnosis:**
`HttpAliveAsync` ends with a bare `catch { return false; }`. That catch cannot distinguish:

- a real connection failure (correctly `false`),
- the internal 7-second probe timeout (correctly `false`),
- **the caller's cancellation** (`ct` fired) — incorrectly `false`.

When the caller's token is cancelled, `ProbeUncachedAsync` walks the whole ladder (direct → proxy → proxy retry), gets `false` three times and returns `HostRoute.Dead`. That verdict is then **cached** in `_cache` (see `BUG-04`) and `ProviderRegistry` auto-disables every provider on that host until the local IP set changes. A user who cancels a search (Esc) or closes the window mid-probe can permanently poison routing for the session.

**Recommended solution:**
Rethrow caller cancellation and only convert genuine failures to `false`:

```csharp
catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
catch { return false; }
```

Then in `ProbeUncachedAsync`, let `OperationCanceledException` propagate and make sure the faulted/cancelled probe Task is *removed* from `_cache` rather than cached (see `BUG-04`). Also narrow the remaining bare catch to `HttpRequestException`, `IOException`, `SocketException`, `TaskCanceledException` and `OperationCanceledException` so unexpected exception types are not silently absorbed.

**Ready-to-use agent prompt:**
> In `src/MusicEngine/Network/Reachability.cs`, fix `HttpAliveAsync` (around lines 82-93) so caller cancellation is never misreported as an unreachable host. Add `catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }` immediately before the existing bare catch, and narrow the bare catch to `catch (Exception ex) when (ex is HttpRequestException or IOException or System.Net.Sockets.SocketException or OperationCanceledException) { return false; }`. Then update `ProbeUncachedAsync` so an `OperationCanceledException` propagates instead of producing `HostRoute.Dead`. Keep the direct → proxy → proxy-retry ladder and the 7s per-probe `CancelAfter` exactly as they are. Build with `D:\dotnet-sdk\dotnet.exe build MusicEngine.sln -c Release`.

---

#### `BUG-04: Probe cache stores a Task bound to the first caller's CancellationToken`

**Severity:** 🟠 High — one cancelling caller corrupts the cached route for all callers.

**File & location:** `src/MusicEngine/Network/Reachability.cs:59-64`.

**Root cause & diagnosis:**

```csharp
return _cache.GetOrAdd(host.Trim(), h => ProbeUncachedAsync(h, ct));
```

The cached value is a `Task<HostRoute>` whose lifetime is tied to whichever caller happened to lose the `GetOrAdd` race. Every subsequent caller awaits *that* task. If the first caller's `ct` is cancelled, all later callers observe a cancelled/`Dead` result even though their own tokens are alive. Additionally a faulted or cancelled task stays in the dictionary permanently — nothing removes it, so the bad answer is sticky until `InvalidateIfNetworkChanged` clears the whole cache. `RoutingHandler.SendAsync:218` calls `ProbeAsync(host, ct)` on the hot request path, so this happens routinely with user-cancellable searches.

**Recommended solution:**
Decouple the cached probe from any caller token: start the probe with `CancellationToken.None` (it already has its own internal 7s timeouts and a 9s `HttpClient.Timeout`, so it cannot hang indefinitely) and let callers apply their own cancellation at the await site via `probe.WaitAsync(ct)`. Also self-heal the cache: attach a continuation that removes the entry when the task does not complete successfully, e.g. `t.ContinueWith(x => _cache.TryRemove(h, out _), TaskContinuationOptions.NotOnRanToCompletion)`.

**Ready-to-use agent prompt:**
> In `src/MusicEngine/Network/Reachability.cs`, make cached probes token-independent. Change `ProbeAsync` (around lines 59-64) so the factory passed to `_cache.GetOrAdd` starts the probe with `CancellationToken.None` instead of the caller's `ct`, and register a cleanup continuation that removes the cache entry when the probe does not run to completion. Then in `RoutingHandler.SendAsync` (same file, around line 218), keep caller-side cancellation by awaiting the returned task through the existing `Task.WhenAny(probe, Task.Delay(1500, ct))` race — no change needed there. Verify the `Peek` fast path still returns `HostRoute.Unknown` for entries that have not completed. Build with `D:\dotnet-sdk\dotnet.exe build MusicEngine.sln -c Release`.

---

#### `BUG-05: Blocking sub-process probes run inside DI construction (startup stall up to ~17s)`

**Severity:** 🟠 High — cold-start hang before the window appears; worst case a visible "not responding" window.

**File & location:** `src/MusicEngine/Providers/PersianIndexProvider.cs:38-67` (`ProbePython`, `p?.WaitForExit(15000)` at `:60`); `src/MusicEngine/Providers/YtDlpProvider.cs:43-61` and `ResolveBinary` at `:345-361` (`Process.Start("where", …)`); both resolved from `App.xaml.cs:73-75` → `ProviderRegistry` → `MainWindow`.

**Root cause & diagnosis:**
Both provider constructors perform synchronous process work. `PersianIndexProvider` launches `python -c "import curl_cffi"` and blocks up to **15 seconds**. `YtDlpProvider.ResolveBinary` shells out to `where` twice (yt-dlp, ffmpeg) and blocks on each. These constructors are invoked when `ProviderRegistry` is first resolved, which happens on the UI thread inside `OnStartup` (`App.xaml.cs:113`, then again transitively at `:127` when `MainWindow` is created). On a machine without Python on PATH, or with a slow antivirus filter driver hooking process creation, the app shows nothing for many seconds.

**Recommended solution:**
Make availability lazily and asynchronously determined. Replace the eager `bool _available` with `Task<bool>`/`Lazy<Task<bool>>` initialised on first use, or expose an `InitializeAsync()` that `App.OnStartup` fires with `_ = Task.Run(...)` alongside the existing SoundCloud warm-up (`App.xaml.cs:121-125`). Until it resolves, `IsAvailable` should report `false` and the provider simply does not participate — which is already the pipeline's graceful-degradation behaviour. Cut the Python probe timeout to ~3s. Replace `where` with an in-process PATH scan (`Environment.GetEnvironmentVariable("PATH").Split(Path.PathSeparator)` + `File.Exists`), which is both faster and process-free.

**Ready-to-use agent prompt:**
> Remove blocking startup probes from two provider constructors. (1) In `src/MusicEngine/Providers/PersianIndexProvider.cs`, replace the eager `_available = … && ProbePython();` in the constructor with a lazily-evaluated async probe: add `private readonly Lazy<Task<bool>> _availability;` initialised to `new(() => Task.Run(ProbePythonAsync))`, make `ProbePythonAsync` use `await p.WaitForExitAsync(cts.Token)` with a 3-second timeout instead of `WaitForExit(15000)`, and make `IsAvailable` return `_availability.Value is { IsCompletedSuccessfully: true, Result: true }` so it is non-blocking and false until known. Add a `public Task<bool> EnsureAvailableAsync()` that awaits `_availability.Value`. (2) In `src/MusicEngine/Providers/YtDlpProvider.cs`, rewrite `ResolveBinary` (around lines 345-361) to scan the `PATH` environment variable in-process with `File.Exists` instead of starting the `where` process; keep the explicit-path and `AppContext.BaseDirectory` checks first. (3) In `src/MusicEngine.App/App.xaml.cs`, next to the existing SoundCloud warm-up `Task.Run` (around line 121), add a fire-and-forget `_ = Task.Run(() => _services!.GetRequiredService<PersianIndexProvider>().EnsureAvailableAsync());` wrapped in try/catch. Do not change any public interface. Build and run the offline tests.

---

#### `BUG-06: Sync-over-async shutdown can deadlock or hang the exit path`

**Severity:** 🟠 High — hung process on exit, tray icon left behind in edge cases.

**File & location:** `src/MusicEngine.App/App.xaml.cs:169-181` (`OnExit`, line 174).

**Root cause & diagnosis:**

```csharp
try { sp.GetRequiredService<DownloadManager>().StopAsync().GetAwaiter().GetResult(); }
catch { /* draining */ }
```

`OnExit` runs on the dispatcher thread. `StopAsync` drains the `Channel` worker pool; any continuation inside that path that requires the dispatcher (progress marshalling through `IDispatcher`/`WpfDispatcher`, which uses `BeginInvoke` — safe — but third-party or future code may not) blocks forever because the dispatcher is blocked on `GetResult()`. Even without a true deadlock there is **no timeout**: a worker stuck inside a 45-second stall watchdog or a `yt-dlp` process that ignores `Kill()` holds the exit indefinitely with no window on screen.

**Recommended solution:**
Bound the drain and keep the dispatcher pumping. Simplest robust fix: `StopAsync().WaitAsync(TimeSpan.FromSeconds(3))` inside the try, so a stuck worker cannot block exit; the process is terminating, so abandoning the drain is acceptable. Better: move graceful shutdown earlier — trigger `StopAsync` from `MainWindow.Window_Closing` (or a `ShutdownRequested` VM signal) and `await` it there, leaving `OnExit` to do only disposal. Also ensure `_tray.Dispose()` happens in a `finally` so a throw in the drain cannot leave the tray icon orphaned.

**Ready-to-use agent prompt:**
> In `src/MusicEngine.App/App.xaml.cs`, make `OnExit` (around lines 169-181) unable to hang the process. Wrap the whole body in `try { … } finally { … }` so that `_tray` disposal and `sp.Dispose()` always run. Replace `sp.GetRequiredService<DownloadManager>().StopAsync().GetAwaiter().GetResult();` with a bounded wait: `try { sp.GetRequiredService<DownloadManager>().StopAsync().WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); } catch (TimeoutException) { CrashLog.Write("exit", new TimeoutException("Download drain exceeded 3s; exiting anyway")); } catch { }`. Keep the `Reachability` disposal and `base.OnExit(e)` call. Build with `D:\dotnet-sdk\dotnet.exe build MusicEngine.sln -c Release`.

---

#### `BUG-07: PreviewPlayer has no generation guard — a stale player's callback stops the new track`

**Severity:** 🟠 High — user-visible: preview stops itself immediately after starting.

**File & location:** `src/MusicEngine.App/PreviewPlayer.cs:33-71` (callbacks at `:50-67`), `StopInternal` at `:87-98`.

**Root cause & diagnosis:**
`Toggle` calls `StopInternal()` (which nulls `_player`) and then constructs a *new* `MediaPlayer`, capturing the `onStopped` callback of the *new* request. But the **old** player's `MediaFailed`/`MediaEnded` lambdas are still subscribed and still hold their own captured `onStopped` from the previous call. `_suppressEnd` is a single shared field that `Toggle` resets to `false` at `:48` right after the old player was stopped, so it cannot suppress a late event from the old player. Sequence:

1. User previews A → player A opens over the network.
2. User quickly previews B → `StopInternal()` closes A, `_suppressEnd = false`, player B opens.
3. Player A's pending `MediaFailed` (the close aborted its stream) fires → it calls `StopInternal()`, which now kills **player B**, and invokes A's `onStopped`, resetting the UI.

Result: the second preview dies instantly and the now-playing bar clears. Also `MediaFailed` gives the user no error feedback — the failure is indistinguishable from a normal stop.

**Recommended solution:**
Add a monotonically increasing `int _generation`. Capture `var gen = ++_generation;` in `Toggle` and have every handler start with `if (gen != _generation) return;`. Unsubscribe (or at minimum ignore) old handlers, and null-guard `StopInternal` against a player that is no longer `_player`. Surface `MediaFailed` to the ViewModel via a `Failed` event so the UI can toast "Preview unavailable" instead of silently resetting.

**Ready-to-use agent prompt:**
> In `src/MusicEngine.App/PreviewPlayer.cs`, add a generation guard so a stale `MediaPlayer` callback cannot stop the current playback. Add `private int _generation;`. In `Toggle`, after `StopInternal()`, capture `var gen = ++_generation;` and begin each of the three handlers (`MediaOpened`, `MediaEnded`, `MediaFailed`) with `if (gen != _generation) return;`. Change `StopInternal` to accept an optional `MediaPlayer? expected = null` and return immediately when `expected is not null && !ReferenceEquals(expected, _player)`. Additionally, add `public event Action<string>? Failed;` and raise it from the `MediaFailed` handler with the exception message so the UI can report preview failures; wire it in `src/MusicEngine.App/ViewModels/MainViewModel.cs` to the existing toast/status mechanism. Build the app project and verify rapid A→B preview switching keeps B playing.

---

#### `BUG-08: SearchResultCache eviction is O(n) and the FinglishConverter phrase cache is unbounded`

**Severity:** 🟠 High (memory) / 🟡 Medium (CPU).

**File & location:** `src/MusicEngine/Search/ProviderHealth.cs:95-108` (`Store`, `OrderBy(...).First()` at `:103`); `src/MusicEngine/Text/FinglishConverter.cs:36` (`PhraseCache`) and `:95`.

**Root cause & diagnosis:**
`SearchResultCache.Store` evicts by sorting the entire dictionary on every insert once it reaches 512 entries: `_cache.OrderBy(kv => kv.Value.StoredAt).First()` allocates and sorts 512 entries inside a `lock` — on the UI-adjacent search completion path. It is also *not* an LRU: it evicts by insertion time, so a repeatedly-hit hot query is discarded while cold entries survive.

`FinglishConverter.PhraseCache` is a `ConcurrentDictionary<string,string>` with **no bound and no eviction**. Every distinct phrase ever converted — including every provider-returned title normalised during gating, not just user queries — is retained for the process lifetime. In a long session with thousands of scraped titles this is a slow, permanent leak.

Note also that `SearchResultCache` holds `IReadOnlyList<TrackWork>`, and each `TrackWork` transitively retains all its `SearchResult` rows and metadata — 512 entries is a substantial retained graph, not 512 small objects.

**Recommended solution:**
Give `SearchResultCache` a real LRU: keep a `LinkedList<string>` recency list alongside the dictionary (or store a `LastAccess` timestamp and evict the minimum in a single O(n) pass without sorting/allocating). Update recency in `TryGet`. For `PhraseCache`, cap it — clear (or evict a slice) when `Count` exceeds a limit such as 4,096, or restrict caching to user-entered queries and skip caching for the high-cardinality title-normalisation path.

**Ready-to-use agent prompt:**
> Bound and correct two caches. (1) In `src/MusicEngine/Search/ProviderHealth.cs`, convert `SearchResultCache` (around lines 72-111) to a proper LRU: change `Entry` to `record Entry(IReadOnlyList<TrackWork> Works, DateTimeOffset StoredAt, long LastAccessTicks)`, update `LastAccessTicks` on every `TryGet` hit, and in `Store` replace `_cache.OrderBy(kv => kv.Value.StoredAt).First()` with a single allocation-free loop that finds the entry with the smallest `LastAccessTicks`. Keep the 512 cap, the 6-hour TTL, the `_lock`, and the public API (`TryGet`, `Store`, `Clear`) unchanged. (2) In `src/MusicEngine/Text/FinglishConverter.cs`, bound `PhraseCache` (declared around line 36, used at line 95): before `GetOrAdd`, if `PhraseCache.Count > 4096` call `PhraseCache.Clear()`. Add an XML doc comment explaining the bound. Build and run the offline tests — the Finglish conversion tests must still pass.

#### `BUG-09: Non-atomic configuration and state writes can corrupt JSON on crash`

**Severity:** 🟠 High — lost library history / unreadable settings.

**File & location:** `src/MusicEngine/Configuration/AppConfig.cs` (`Save()` writing directly to `ConfigPath`); `src/MusicEngine/Configuration/AppState.cs` (`Save()` called from `PushSearch`/`PushHistory`).

**Root cause & diagnosis:**
Both `Save()` implementations serialize and `File.WriteAllText` straight over the live file. `WriteAllText` truncates first, then writes: a crash, power loss, or an antivirus lock between the two leaves a zero-length or half-written JSON file. `AppConfig.Load()` and `AppState.Load()` fall back to defaults on parse failure, so the user silently loses their output directory, proxy, accent, source toggles, recent searches and full download history. `AppState.Save()` is called on *every* search and *every* completed download, so the exposure window recurs constantly.

**Recommended solution:**
Write to `path + ".tmp"`, `Flush`, then `File.Move(tmp, path, overwrite: true)` — the same atomic-rename discipline `HttpDownloader.Finish` already uses correctly at `HttpDownloader.cs:122-128`. Optionally keep a `.bak` of the previous file and fall back to it when the primary fails to parse.

**Ready-to-use agent prompt:**
> Make configuration and state writes atomic. In both `src/MusicEngine/Configuration/AppConfig.cs` and `src/MusicEngine/Configuration/AppState.cs`, change `Save()` to serialize into `<path>.tmp` first and then call `File.Move(tmpPath, path, overwrite: true)`, wrapping the whole operation in try/catch that logs and leaves the previous file intact on failure. Add a shared private static helper (duplicated in each file is acceptable to avoid a new dependency) named `WriteAtomic(string path, string json)`. Also make `Load()` tolerate a stray `<path>.tmp` by ignoring it. Do not change the public `Load`/`Save` signatures or the JSON shape. Build with `D:\dotnet-sdk\dotnet.exe build MusicEngine.sln -c Release`.

---

#### `BUG-10: Config is loaded from disk three times, so the DI singleton is not the source of truth`

**Severity:** 🟡 Medium — settings changes silently ignored or reverted.

**File & location:** `App.xaml.cs:48` (DI singleton), `MainWindow.xaml.cs:201` (`Window_Closing`), `SettingsWindow.xaml.cs:19` (constructor).

**Root cause & diagnosis:**
`AppConfig` is registered as a DI singleton and mutated through `MainViewModel.SaveSettings` (`MainViewModel.cs:596-599`). But `SettingsWindow` constructs its own instance via `AppConfig.Load()`, and `Window_Closing` loads a *third* instance just to read `MinimizeToTray`. Any in-memory change not yet flushed to disk is invisible to those two, and any field the settings dialog does not round-trip is silently reset from the on-disk copy. This is the mechanism by which "I changed a setting and it didn't stick" bugs appear.

**Recommended solution:**
Inject the singleton. `SettingsWindow` should take `AppConfig` (ideally via a `SettingsViewModel`) through its constructor and be resolved from the container — `MainWindow.ShowSettings` already has access to the VM, so pass the config through. `Window_Closing` should read `_vm.MinimizeToTray` (a new VM property backed by the injected config) instead of touching disk.

**Ready-to-use agent prompt:**
> Make the DI `AppConfig` singleton the single source of truth. (1) In `src/MusicEngine.App/SettingsWindow.xaml.cs`, add a constructor parameter `AppConfig cfg`, store it, and delete the `AppConfig.Load()` call at line 19 — populate all controls from the injected instance. Keep a parameterless constructor only if the XAML designer requires it, delegating to `AppConfig.Load()` with a comment. (2) In `src/MusicEngine.App/MainWindow.xaml.cs`, change `ShowSettings` to construct `new SettingsWindow(_vm.Config)` (add a `public AppConfig Config => _config;` property to `MainViewModel` if one does not exist), and change `Window_Closing` (line 201) to read `_vm.MinimizeToTray` instead of calling `AppConfig.Load()` — add that boolean property to `MainViewModel` backed by the injected config. Build the app project.

---

#### `BUG-11: PersianIndexProvider reads a stderr Task's .Result (sync-on-async)`

**Severity:** 🟡 Medium — thread-pool blocking; deadlock risk under starvation.

**File & location:** `src/MusicEngine/Providers/PersianIndexProvider.cs:176` (`stderrTask` created) and `:195` (`stderrTask.Result`).

**Root cause & diagnosis:**
`proc.StandardError.ReadToEndAsync(ct)` is started, `WaitForExitAsync` is awaited, and then the stderr result is retrieved with the blocking `.Result`. In practice `WaitForExitAsync` usually means stderr has completed, but not always — a redirected pipe can still have buffered data, in which case `.Result` blocks a thread-pool thread. Under the app's fan-out load (nine providers × three sites in parallel) thread-pool starvation is a realistic condition, and `.Result` also wraps any failure in `AggregateException`, defeating the surrounding `catch (Exception ex)` filters that expect the original type.

**Recommended solution:** `var stderr = await stderrTask.ConfigureAwait(false);`

**Ready-to-use agent prompt:**
> In `src/MusicEngine/Providers/PersianIndexProvider.cs` around line 195, replace `var stderr = stderrTask.Result;` with `var stderr = await stderrTask.ConfigureAwait(false);`. Confirm the enclosing method is `async` (it awaits `proc.WaitForExitAsync` just above). Then search the whole solution for other blocking calls with `grep -rn "\.Result\b\|\.Wait()\|GetAwaiter().GetResult()" src/` and list any remaining occurrences in your summary without changing them unless they are in `src/` production code (the test harness in `tests/` may keep its blocking calls). Build with `D:\dotnet-sdk\dotnet.exe build MusicEngine.sln -c Release`.

---

#### `BUG-12: Broad bare catches swallow diagnostics across the engine`

**Severity:** 🟡 Medium — failures become invisible; debugging relies on guesswork.

**File & location:** representative sites — `Http/ArtworkLoader.cs` (swallows everything), `Audio/TrackTagger.cs` (swallows to `Debug.WriteLine`), `App.xaml.cs:124` (`catch { }` on SoundCloud warm-up), `App.xaml.cs:135-137` (`RefreshRoutesAsync`), `Providers/DeezerProvider.cs` (`yield break` on error), `Reachability.cs:92`, `:130`, `:180`.

**Root cause & diagnosis:**
The codebase deliberately favours graceful degradation, which is the right instinct for a scraper-heavy app. The problem is that degradation is *silent*: `ILogger` is injected into providers but the majority of catch blocks either do nothing or write to `Debug.WriteLine`, which is invisible in a Release build. There is no structured log file — only `CrashLog` (`%APPDATA%\MusicEngine\crash.log`), which is written exclusively by the three global handlers. When a user reports "no results for X", there is no artifact to diagnose from.

**Recommended solution:**
Keep the catches, add observability. Every `catch` that is a deliberate degradation should call `_logger.LogDebug`/`LogWarning` with the provider id and the exception. Pair this with `FEAT-01` (rolling file logging) so those messages land somewhere readable in Release.

**Ready-to-use agent prompt:**
> Add diagnostics to silent catch blocks without changing control flow. In `src/MusicEngine/Http/ArtworkLoader.cs`, `src/MusicEngine/Audio/TrackTagger.cs`, and `src/MusicEngine/Network/Reachability.cs`, inject or use the existing `ILogger` (use `NullLogger<T>.Instance` as the default parameter, matching the pattern already used in `YtDlpProvider` and `PersianIndexProvider`) and add a `_logger.LogDebug(ex, "…")` call inside each currently-empty or `Debug.WriteLine`-only catch. Replace every `Debug.WriteLine` in `TrackTagger` with a logger call. Do not narrow or remove any catch, do not change any method signature other than adding optional trailing `ILogger<T>? logger = null` parameters, and do not change return values. Build with `D:\dotnet-sdk\dotnet.exe build MusicEngine.sln -c Release`.

---

#### `BUG-13: TLS certificate validation is unconditionally disabled`

**Severity:** 🔴 Critical (security) — the app accepts any certificate on every HTTPS request.

**File & location:** `src/MusicEngine/Http/SharedHttpClient.cs` — `ServerCertificateCustomValidationCallback = (_, _, _, _) => true`.

**Root cause & diagnosis:**
Every handler created by the shared client factory accepts *any* server certificate: expired, self-signed, wrong hostname, or attacker-supplied. This makes every request — including the SOCKS5-proxied ones — trivially interceptable by anyone on the network path, which is a meaningful threat in exactly the censored-network environment this app targets. The presumable motivation is that some scraped Iranian hosts have broken certificate chains, but the blanket callback removes protection from *all* hosts, including the iTunes/Deezer/SoundCloud APIs.

**Recommended solution:**
Scope the exemption. Keep default validation and allow overrides only for an explicit allow-list of hostnames known to have broken chains, and only for specific failure flags (`SslPolicyErrors.RemoteCertificateChainErrors`, not `RemoteCertificateNameMismatch`). Make the allow-list a `HashSet<string>` constant next to `ProviderHosts` so it is reviewable. Surface a config flag (default `false`) if a global escape hatch is genuinely needed for field debugging.

**Ready-to-use agent prompt:**
> In `src/MusicEngine/Http/SharedHttpClient.cs`, replace the unconditional `ServerCertificateCustomValidationCallback = (_, _, _, _) => true` with a scoped policy. Add `private static readonly HashSet<string> RelaxedTlsHosts = new(StringComparer.OrdinalIgnoreCase) { /* only hosts with genuinely broken chains, one per line with a comment explaining why */ };` and implement the callback as: return `true` when `errors == SslPolicyErrors.None`; return `true` when `request.RequestUri?.Host` is in `RelaxedTlsHosts` AND `errors` contains only `RemoteCertificateChainErrors`; otherwise return `false`. Add an XML doc comment on `RelaxedTlsHosts` stating that adding a host here disables chain validation for it. Populate the set initially with the Persian scraping hosts listed in `src/MusicEngine/Network/ProviderHosts.cs` that are NOT API hosts (leave iTunes, Deezer, SoundCloud, YouTube on strict validation). Build the solution, then run `cd tests\MusicEngine.Tests && D:\dotnet-sdk\dotnet.exe run -- live` and report which hosts, if any, now fail TLS so the list can be tuned.

#### `BUG-14: UrlQueryResolver creates a new SharedHttpClient per call`

**Severity:** 🟡 Medium — socket exhaustion pattern, wasted TLS handshakes.

**File & location:** `src/MusicEngine/Search/UrlQueryResolver.cs` (constructs `new SharedHttpClient(...)` inside the resolve path).

**Root cause & diagnosis:**
Every pasted Spotify/YouTube/SoundCloud link builds a brand-new `SharedHttpClient`, which builds new `SocketsHttpHandler`s (direct + proxied) and a new `RoutingHandler`. That is the classic `HttpClient`-per-request anti-pattern: connection pools are not shared, DNS and TLS are redone, and disposed handlers leave sockets in `TIME_WAIT`. It also bypasses the app's `Reachability` instance, so the routing decisions the rest of the app has already learned are not reused.

**Recommended solution:** Accept `SharedHttpClient` as a constructor dependency (the DI container already has a singleton at `App.xaml.cs:58`) and use `Create("UrlResolve", proxied: true)`. `SearchService` already receives its collaborators by injection, so threading one more through is consistent with the existing style.

**Ready-to-use agent prompt:**
> In `src/MusicEngine/Search/UrlQueryResolver.cs`, stop creating an `HttpClient`/`SharedHttpClient` per call. Add a constructor (or a required parameter on the existing entry point) that accepts the shared `MusicEngine.Http.SharedHttpClient` and obtain a named client once via `Create("UrlResolve", proxied: true)`. Update every construction site of `UrlQueryResolver` — check `src/MusicEngine/Search/SearchService.cs` and `src/MusicEngine.App/App.xaml.cs` — to pass the DI singleton. If `UrlQueryResolver` is currently a static class, keep the static API but add an optional `SharedHttpClient?` parameter that, when supplied, is used instead of constructing one. Build the solution and run the offline tests.

---

#### `BUG-15: Non-static Regex calls on hot paths`

**Severity:** 🔵 Low — avoidable allocation and re-parsing per call.

**File & location:** `src/MusicEngine/Providers/ITunesProvider.cs:104` and `:106`; `src/MusicEngine/Providers/Nex1MusicProvider.cs:161` (`ExtractFilename`).

**Root cause & diagnosis:**
These sites call the static `Regex.Match(input, pattern)` overload with literal patterns. That path consults the regex cache by string key and, past `Regex.CacheSize`, re-parses and re-compiles. The codebase elsewhere does this correctly — `Ranker.VersionLike`, `YtDlpProvider.ProgressRegex`, `PersianSitesProvider.Mp3Regex` and `JunkFilter`'s patterns are all `static readonly … RegexOptions.Compiled`. These three are simply inconsistent with the established convention.

**Recommended solution:** Promote to `[GeneratedRegex]` partial properties (available on .NET 8, zero-allocation, compile-time generated) or, minimally, `static readonly Regex` with `RegexOptions.Compiled`.

**Ready-to-use agent prompt:**
> Convert the remaining inline regexes to source-generated ones. In `src/MusicEngine/Providers/ITunesProvider.cs` (lines ~104 and ~106) and `src/MusicEngine/Providers/Nex1MusicProvider.cs` (line ~161), replace `System.Text.RegularExpressions.Regex.Match(input, "pattern")` calls with .NET 8 `[GeneratedRegex]` partial properties: mark the containing class `partial`, add e.g. `[GeneratedRegex("artist:\"([^\"]+)\"\\s+track:\"([^\"]+)\"")] private static partial Regex FieldedQueryRegex();` and call `FieldedQueryRegex().Match(query)`. Give each one a descriptive name. Do not change any matching semantics or group indices. Build the solution and run the offline tests to confirm the iTunes fielded-query parsing still works.

---

### 2.2 WPF & MVVM Anti-Patterns (`MVVM-*`)

---

#### `MVVM-01: MainWindow code-behind owns view state, control mutation and search orchestration`

**Severity:** 🟠 High — untestable UI logic, brittle coupling to named controls.

**File & location:** `src/MusicEngine.App/MainWindow.xaml.cs` — `VmOnPropertyChanged:27-43`, `UpdateEmptyState:45-48`, `RunSearch:61-67`, `SetTab:168-174`, `Sort_SelectionChanged:123-134`, `Window_Closing:199-209`.

**Root cause & diagnosis:**
The window subscribes to the ViewModel's `PropertyChanged` and then pushes values into named controls by hand:

```csharp
case nameof(MainViewModel.HasResults):
case nameof(MainViewModel.IsSearching):
    UpdateEmptyState();
    ResultsCount.Text = _vm.ResultsView.Count > 0 ? $"({_vm.ResultsView.Count})" : "";
```

Every one of these is a binding the framework should be doing: `EmptyState.Visibility` from a VM property + converter; `ResultsCount.Text` from a VM `ResultsCountLabel`; `SeekSlider.Value/Maximum` via two-way / one-way bindings; the spinner from `IsSearching`. `SetTab` goes further and swaps `Style` objects imperatively via `FindResource`, duplicating state that already lives in `ShowDownloads`/`ShowHistory`. `Sort_SelectionChanged` maps a `ComboBoxItem`'s *string content* back to an enum — a stringly-typed round-trip that breaks the moment a label is renamed or localised.

**Recommended solution:**
Move each of these to the ViewModel and bind:

- add `ResultsCountLabel`, `IsEmptyStateVisible` (or bind `EmptyState.Visibility` to `HasResults` with the existing inverse converter plus a `MultiBinding` for `IsSearching`);
- bind the spinner's `Visibility` to `IsSearching` and delete `RunSearch`'s manual show/hide;
- expose `ObservableCollection<ResultSort>`/`SelectedSort` and bind the ComboBox with `SelectedValue`, deleting the string switch;
- replace `SetTab` with a `SelectedTab` enum property on the VM and drive the button styles from `DataTrigger`s in XAML;
- move `Window_Closing`'s decision into a VM method (`TryClose()` returning bool) so the tray/minimise policy is testable.

**Ready-to-use agent prompt:**
> Move view logic out of `src/MusicEngine.App/MainWindow.xaml.cs` into `MainViewModel` and XAML bindings, deleting the `PropertyChanged` handler. Step by step: (1) In `src/MusicEngine.App/ViewModels/MainViewModel.cs` add notifying properties `string ResultsCountLabel` (raised whenever `ResultsView` changes or `HasResults`/`IsSearching` change), `bool ShowEmptyState => !HasResults && !IsSearching`, and `ResultSort SelectedSort` that wraps the existing `SortMode`. Raise `OnPropertyChanged` for the derived properties from the same places that currently raise `HasResults`/`IsSearching`. (2) In `MainWindow.xaml`, bind `EmptyState`'s `Visibility` to `ShowEmptyState` with the `BoolToVis` converter, bind `ResultsCount.Text` to `ResultsCountLabel`, bind `SearchSpinner`'s `Visibility` to `IsSearching`, bind `SeekSlider`'s `Maximum` to `PlayerDuration` and `Value` to `PlayerPosition` (Mode=OneWay; keep the existing drag handlers for seeking), and change the sort `ComboBox` to `ItemsSource`/`SelectedValue` bound to the VM instead of hard-coded `ComboBoxItem`s. (3) Delete `VmOnPropertyChanged`, `UpdateEmptyState`, `Sort_SelectionChanged`, and the spinner show/hide inside `RunSearch` from the code-behind, plus the `vm.PropertyChanged += …` subscription in the constructor. Keep `_seeking` drag handling. Build the app and confirm the empty state, result count, spinner and sorting all still work.

---

#### `MVVM-02: SettingsWindow has no ViewModel and builds templates in C# at runtime`

**Severity:** 🟠 High — 150 lines of untestable dialog logic; duplicated source lists.

**File & location:** `src/MusicEngine.App/SettingsWindow.xaml.cs:15-153` — notably `BuildAccentPicker:45-75`, `MakeAccentTemplate:77-91`, and the twin `DisabledSources`/`EnabledSources` properties at `:106-138`.

**Root cause & diagnosis:**
The dialog reads config in its constructor, exposes 12 computed properties that the *caller* then copies into config (`MainWindow.xaml.cs:218-236`), and builds accent-swatch `ControlTemplate`s with `FrameworkElementFactory` — an API Microsoft documents as deprecated in favour of XAML templates. `DisabledSources` and `EnabledSources` are near-identical 8-line blocks that enumerate the same eight providers a second and third time (they are also enumerated in the constructor at `:32-39` and in `ProviderHosts`/`ProviderId`). Adding a tenth provider requires edits in four places in this file alone, with no compiler assistance.

**Recommended solution:**
Introduce `SettingsViewModel` holding an `ObservableCollection<SourceToggleViewModel>` projected from `Enum.GetValues<ProviderId>()`, plus scalar properties for the rest, and an `ApplyTo(AppConfig)` method. Replace the runtime template factory with a XAML `DataTemplate` + `ItemsControl` bound to `AccentTheme.Presets`, using a `DataTrigger` for the selected checkmark. The dialog's code-behind then shrinks to `DataContext = vm` plus the two dialog-result handlers.

**Ready-to-use agent prompt:**
> Refactor the settings dialog to MVVM. Create `src/MusicEngine.App/ViewModels/SettingsViewModel.cs` with: a constructor taking `AppConfig`; scalar properties for OutputDirectory, ProxyUrl, CookiesBrowser, CookiesFile, EnablePersianIndex, DownloadToasts, MinimizeToTray, ClipboardMonitor, MaxParallelDownloads, BitrateKbps, FilenameTemplate and Accent (all using the existing `ViewModelBase.Set<T>` pattern from `ViewModels/Mvvm.cs`); an `ObservableCollection<SourceToggleViewModel> Sources` built by iterating `Enum.GetValues<ProviderId>()`, excluding `Unknown` and `YtDlp`, with `Id`, `DisplayName` and `IsEnabled` seeded from `cfg.IsSourceEnabled(id)`; an `ObservableCollection<AccentOptionViewModel> Accents` built from `AccentTheme.Presets` with an `IsSelected` flag; a `BrowseCommand`; and an `ApplyTo(AppConfig cfg)` method that writes every value back including rebuilding `cfg.DisabledSources` from the toggles. Then rewrite `SettingsWindow.xaml` to bind to it — replace the eight hard-coded source `CheckBox`es with an `ItemsControl` over `Sources` using the existing `Switch` style, and replace `AccentPanel` with an `ItemsControl` over `Accents` using a XAML `DataTemplate` (a `Border` with `CornerRadius="15"` and a `DataTrigger` on `IsSelected` for the white border and checkmark). Reduce `SettingsWindow.xaml.cs` to a constructor taking `SettingsViewModel`, `DataContext = vm`, and the `Save_Click`/`Cancel_Click` handlers — delete `BuildAccentPicker`, `MakeAccentTemplate`, `DisabledSources`, `EnabledSources` and all the computed properties. Update `MainWindow.ShowSettings` to construct the VM from the injected `AppConfig` and call `vm.ApplyTo(cfg)` inside the existing `_vm.SaveSettings(...)` callback. Build the app and verify every setting round-trips.

---

#### `MVVM-03: async void event handlers swallow exceptions`

**Severity:** 🟠 High — an exception inside a search escapes to the global handler as an opaque message box.

**File & location:** `src/MusicEngine.App/MainWindow.xaml.cs:52` (`Search_Click`), `:54` (`SearchBox_KeyDown`), `:78` (`Recent_Click`).

**Root cause & diagnosis:**
`async void` handlers cannot be awaited and their exceptions are re-thrown on the dispatcher's synchronization context. Here they all delegate to `RunSearch`, whose `try/finally` only restores the spinner — it does not catch. Any exception from the search pipeline (a provider bug, a JSON deserialization failure, an `InvalidOperationException` from touching a collection off-thread) therefore reaches `App.DispatcherUnhandledException:33-39`, which shows a generic "Something went wrong" `MessageBox`. The user gets a modal dialog instead of an inline error, and the actual failure is only visible in `crash.log`.

**Recommended solution:**
Replace the handlers with `ICommand` bindings (`SearchCommand` already fits the `RelayCommand` pattern in `Mvvm.cs`) and let the VM own the try/catch, surfacing failure through the existing status/toast mechanism. Where an `async void` handler is unavoidable at a framework boundary, wrap the entire body in try/catch and route to a shared `HandleUiError(ex)`.

**Ready-to-use agent prompt:**
> Eliminate `async void` handlers from `src/MusicEngine.App/MainWindow.xaml.cs`. (1) In `MainViewModel`, ensure there is a `SearchCommand` (a `RelayCommand` whose execute body calls `SearchAsync` inside `try { … } catch (Exception ex) { Status = $"Search failed: {ex.Message}"; CrashLog.Write("search", ex); }` and which sets `IsSearching` in a `finally`), and add a `RelayCommand SearchRecentCommand` taking the recent query string as its parameter. (2) In `MainWindow.xaml`, bind the search button's `Command` to `SearchCommand`, add an `InputBinding` for Enter on the search `TextBox` (or a `KeyBinding` on the window) bound to `SearchCommand`, and bind the recents `Popup` items' buttons to `SearchRecentCommand` with `CommandParameter="{Binding}"`. (3) Delete `Search_Click`, `SearchBox_KeyDown`, `Recent_Click` and `RunSearch` from the code-behind along with their XAML event attributes. Build the app and verify Enter, the button and a recent-search click all trigger a search and that a thrown provider error shows in the status line rather than a message box.

#### `MVVM-04: ApplyResults clears and refills the results collection on every batch`

**Severity:** 🟡 Medium — visible flicker, lost selection, O(n) UI churn per batch.

**File & location:** `src/MusicEngine.App/ViewModels/MainViewModel.cs` — the `Batch` callback and `ApplyResults` (around `:290-350`), bound to `ResultsList` in `MainWindow.xaml:195`.

**Root cause & diagnosis:**
Each streamed batch calls `ApplyResults`, which clears the `ObservableCollection` and re-adds every work. `ObservableCollection.Clear()` raises a `Reset` notification: WPF discards all containers, loses `SelectedItem`, resets scroll position, and — because the item template has a per-item `Loaded` entrance storyboard (`XAML-01`) — replays the animation for the whole list. Since providers stream in over several seconds, the user watches the list blink repeatedly while they are trying to click a row. It is also needlessly O(n) per batch: the pipeline delivers cumulative results, so most items are unchanged.

**Recommended solution:**
Diff instead of reset. Key rows by `TrackWork.BaseKey`/`DedupKey`, insert only new works at their sorted position, update mutated ones in place, and remove only those genuinely gone. Because `SearchResult.DedupKey` already exists, a `Dictionary<string, TrackItemViewModel>` index makes this straightforward. Consider exposing a sorted `ICollectionView` (`CollectionViewSource` with a `SortDescription`) so re-sorting does not require rebuilding the collection either.

**Ready-to-use agent prompt:**
> In `src/MusicEngine.App/ViewModels/MainViewModel.cs`, replace the clear-and-refill behaviour in `ApplyResults` with an incremental merge. Add a `private readonly Dictionary<string, TrackItemViewModel> _resultIndex = new();` keyed by the work's dedup/base key. In `ApplyResults`: build the desired ordered list as today, then (a) for each desired item, if the key exists in `_resultIndex` update that existing `TrackItemViewModel` in place (add an `Update(TrackWork work)` method to `TrackItemViewModel` in `ViewModels/TrackItemViewModel.cs` that reassigns the work and raises `OnPropertyChanged(null)`), otherwise create it and insert it at the correct index in the `ObservableCollection`; (b) remove from the collection and the index any entries whose keys are no longer present; (c) move existing items only when their index actually changed, using `ObservableCollection<T>.Move`. Clear `_resultIndex` alongside the collection when a new search starts. Do not call `Clear()` during batch application. Build the app and verify that streaming batches no longer reset scroll position or selection.

---

#### `MVVM-05: TrackItemViewModel.IsInLibrary does not raise PropertyChanged`

**Severity:** 🟡 Medium — stale badge in the UI.

**File & location:** `src/MusicEngine.App/ViewModels/TrackItemViewModel.cs` (`IsInLibrary` is a plain auto-property; `LibraryBadge` is derived from it), consumed in `MainWindow.xaml`; set at `MainViewModel.cs:348`.

**Root cause & diagnosis:**
`IsInLibrary` is a plain `{ get; set; }` on a class deriving from `ViewModelBase`, so assigning it raises nothing. The derived `LibraryBadge` only refreshes because `MainViewModel` manually raises the badge's change notification in some paths. Any code path that sets `IsInLibrary` without that manual raise — for instance marking a row as owned after a download completes — leaves the badge stale until the list is rebuilt (which currently happens by accident via `MVVM-04`'s `Clear()`; fixing that bug will *expose* this one).

**Recommended solution:**
Convert to `Set(ref _isInLibrary, value)` and raise `LibraryBadge` from its setter. Audit the other computed properties on the same class (`Title`, `Artist`, `QualityLabel`, `SourcesLabel`, `HasPreview`, `DurationSeconds`) for the same pattern.

**Ready-to-use agent prompt:**
> In `src/MusicEngine.App/ViewModels/TrackItemViewModel.cs`, convert `IsInLibrary` from a plain auto-property to a backing-field property using the inherited `Set<T>` helper from `ViewModels/Mvvm.cs`, and in its setter also raise `OnPropertyChanged(nameof(LibraryBadge))`. Then audit every other public property on `TrackItemViewModel`, `DownloadItemViewModel`, `HistoryItemViewModel` and `ToastViewModel` in that file: any settable property must go through `Set`, and any computed property must have its change raised from whichever setter it depends on. Remove any now-redundant manual `OnPropertyChanged` calls for `LibraryBadge` in `src/MusicEngine.App/ViewModels/MainViewModel.cs`. Build the app project.

---

#### `MVVM-06: MainViewModel is a 658-line god object`

**Severity:** 🟡 Medium — the primary obstacle to testing and to safely changing the UI.

**File & location:** `src/MusicEngine.App/ViewModels/MainViewModel.cs` (whole file).

**Root cause & diagnosis:**
One class currently owns: search orchestration and cancellation, result projection and sorting, preview playback and the 250 ms position timer, the download queue with key-based dedup and delayed removal, history recording and persistence, toast lifecycle with its own timer, clipboard polling on a third timer, settings mutation, artwork decoding, and `IDisposable` teardown for all of it. Three `System.Threading.Timer`s live in one object with three different cadences (250/1000/1200 ms). There are no unit tests for any of it, and there cannot easily be — constructing it requires nine dependencies including a `DownloadManager` that owns a channel worker pool.

**Recommended solution:**
Extract cohesive collaborators, each independently testable:

| Extract | Responsibility |
|---|---|
| `SearchCoordinator` | query → pipeline, cancellation, batch projection |
| `PlaybackViewModel` | preview state, position timer, seek, volume |
| `DownloadQueueViewModel` | queue collection, dedup keys, delayed removal, cancel/pause/resume |
| `ToastService` | toast collection + expiry timer |
| `ClipboardWatcher` | clipboard polling, URL detection (also removes STA concerns from the VM) |
| `IHistoryStore` | history recording, wrapping `AppState` behind an interface |

`MainViewModel` then composes them and exposes them as properties for binding. This is a mechanical, low-risk refactor if done one collaborator at a time with a build after each.

**Ready-to-use agent prompt:**
> Decompose `src/MusicEngine.App/ViewModels/MainViewModel.cs` one collaborator at a time; after EACH extraction, build with `D:\dotnet-sdk\dotnet.exe build MusicEngine.sln -c Release` and stop if it fails. Order: (1) Extract `ClipboardWatcher` into `src/MusicEngine.App/Ui/ClipboardWatcher.cs` — move the clipboard `System.Threading.Timer`, the STA clipboard read and the URL-detection logic there; expose `event Action<string> UrlDetected`, `void Start()`, `void Stop()`, and `IDisposable`. (2) Extract `ToastService` into `src/MusicEngine.App/Ui/ToastService.cs` — move the toast `ObservableCollection`, its timer and the dismiss logic; expose `ObservableCollection<ToastViewModel> Toasts`, `void Show(...)`, `void Dismiss(ToastViewModel)`. (3) Extract `PlaybackViewModel` into `src/MusicEngine.App/ViewModels/PlaybackViewModel.cs` — move `PreviewPlayer` interaction, the 250ms position timer, `PlayerPosition`, `PlayerDuration`, `IsPreviewPlaying`, `TogglePreview`, `SeekPreview`, `StopPreview`. (4) Extract `DownloadQueueViewModel` into `src/MusicEngine.App/ViewModels/DownloadQueueViewModel.cs` — move the `Downloads` collection, `SongKey` dedup set, `ReleaseQueueKey`, `ScheduleQueueRemoval`, and the `DownloadManager` event subscriptions. In each step, keep `MainViewModel` as the composition point exposing the new object as a public property, update `MainWindow.xaml` bindings to the new nested paths (e.g. `Playback.PlayerPosition`), register the new types in `src/MusicEngine.App/App.xaml.cs` DI where they need injected dependencies, and make sure `MainViewModel.Dispose` disposes each extracted collaborator. Do not change any user-visible behaviour.

---

#### `MVVM-07: Three System.Threading.Timers marshal through IDispatcher instead of using DispatcherTimer`

**Severity:** 🔵 Low — extra thread hops and a subtle disposal race.

**File & location:** `src/MusicEngine.App/ViewModels/MainViewModel.cs` — `_playerTimer` (250 ms), `_toastTimer` (1000 ms), `_clipboardTimer` (1200 ms); disposal in `Dispose` around `:635-650`.

**Root cause & diagnosis:**
All three timers fire on thread-pool threads and immediately marshal back to the UI via `_ui.Run(...)`. Since every callback's work is purely UI state, `DispatcherTimer` would deliver it on the right thread directly. The current arrangement also has a teardown race: a `System.Threading.Timer` callback already in flight can execute after `Dispose()` returns, invoking `_ui.Run` against a disposed VM. `Timer.Dispose(WaitHandle)` exists to close that window but is not used.

**Recommended solution:** Replace with `DispatcherTimer` (which stops synchronously on the UI thread) for the player and toast timers. The clipboard timer should move into `ClipboardWatcher` (`MVVM-06`), also as a `DispatcherTimer` since `Clipboard` access requires the STA thread anyway.

**Ready-to-use agent prompt:**
> In `src/MusicEngine.App/ViewModels/MainViewModel.cs`, replace the three `System.Threading.Timer` fields with `System.Windows.Threading.DispatcherTimer` instances. For each: set `Interval` to the current cadence (player 250ms, toast 1000ms, clipboard 1200ms), attach the existing callback body to `Tick` with the `_ui.Run(...)` marshalling wrapper REMOVED (Tick already fires on the UI thread), and call `Start()` where the timer is currently created. In `Dispose`, call `Stop()` on each instead of `Dispose()`. Keep `SetClipboardMonitor` working by calling `Start()`/`Stop()` on the clipboard timer. Note in your summary that this makes the VM UI-thread-bound by design, which is already true given its `Clipboard` and `BitmapImage` usage. Build the app project.

#### `MVVM-08: Views are resolved from the container as singletons, and App owns the tray`

**Severity:** 🔵 Low — architectural smell; blocks multi-window and complicates testing.

**File & location:** `src/MusicEngine.App/App.xaml.cs:102` (`services.AddSingleton<MainWindow>()`), `:140-160` (`InitTray`), `:151` (tray reaches into `MainViewModel.CancelAll`).

**Root cause & diagnosis:**
Registering a `Window` as a DI singleton means the container owns UI lifetime; a closed-and-reopened window cannot be recreated, and `sp.Dispose()` in `OnExit` disposes it. The tray menu is built in `App` and calls `_services?.GetRequiredService<MainViewModel>().CancelAll()` — a service-locator call from the application object into a ViewModel method, bypassing the command layer. `OnStartup` is also doing a great deal: exception wiring, DI registration for 20+ services, accent theming, reachability priming with a `DispatcherTimer`, a warm-up task, window creation and tray setup, all in one 100-line method.

**Recommended solution:**
Register `MainWindow` as transient (or construct it directly after `BuildServiceProvider`). Extract a `TrayIconService : IDisposable` that takes `MainViewModel` (or an `IAppCommands` abstraction) by injection and owns the `NotifyIcon`. Split `OnStartup` into `ConfigureServices(ServiceCollection)`, `WireCrashHandlers()` and `StartShell()` for readability.

**Ready-to-use agent prompt:**
> Tidy the composition root in `src/MusicEngine.App/App.xaml.cs`. (1) Change `services.AddSingleton<MainWindow>()` to `services.AddTransient<MainWindow>()`. (2) Create `src/MusicEngine.App/Ui/TrayIconService.cs` — a `sealed class TrayIconService : IDisposable` whose constructor takes `MainViewModel vm`, exposes `void Attach(Window window)` that creates the `NotifyIcon` and context menu (Open / Cancel all downloads / Exit) using `vm.CancelAllCommand` and an injected `Action` for shutdown, and whose `Dispose` hides and disposes the icon. Register it as a singleton and replace `InitTray`/`RestoreWindow` in `App.xaml.cs` with a call to it. (3) Split the remaining `OnStartup` body into three private methods — `WireCrashHandlers()`, `ServiceProvider BuildServices(AppConfig config, AppState state)` and `void StartShell(ServiceProvider sp, AppConfig config)` — moving code verbatim without behavioural change. Also fix the mangled indentation and the two statements sharing line 77 (`services.AddSingleton<ProviderRegistry>(); services.AddSingleton<TrackTagger>();`). Build and run the app to confirm the tray still works and exit is clean.

---

#### `MVVM-09: Converters return raw strings instead of Brushes`

**Severity:** 🔵 Low — hidden per-binding parsing cost, no design-time type safety.

**File & location:** `src/MusicEngine.App/Converters.cs` — `BoolToRedConverter` returns `"#FF6B6B"` / `"#98A0B3"`.

**Root cause & diagnosis:**
Returning a hex *string* where a `Brush` is expected makes WPF invoke `BrushConverter` on every evaluation, allocating a new unfrozen `SolidColorBrush` each time. The palette already defines `DangerBrush` (`#FF6B6B`) and `SubtleTextBrush` (`#98A0B3`) in `App.xaml:26` and `:22`, so this hard-codes a duplicate of the theme and will silently drift if the palette changes.

**Recommended solution:** Return the frozen brushes resolved from `Application.Current.Resources`, or better, delete the converter and express the state with a `DataTrigger` in XAML referencing `{StaticResource DangerBrush}`.

**Ready-to-use agent prompt:**
> In `src/MusicEngine.App/Converters.cs`, change `BoolToRedConverter.Convert` to return `Brush` objects instead of hex strings: resolve `Application.Current?.TryFindResource("DangerBrush") as Brush` and `"SubtleTextBrush"` respectively, falling back to two `static readonly` frozen `SolidColorBrush` instances with the same colours if the resource lookup returns null. Update the `Convert` return type usage accordingly and verify the bindings in `src/MusicEngine.App/MainWindow.xaml` that use `BoolToRed` still render. Build the app project.

---

#### `MVVM-10: No loading, empty or error affordance for downloads and history`

**Severity:** 🟡 Medium — UX gap.

**File & location:** `MainWindow.xaml:406-474` (downloads and history lists), `MainWindow.xaml.cs:45-48` (only the results list has an empty state).

**Root cause & diagnosis:**
Only the results list has an `EmptyState`. A user on the Downloads tab with nothing queued, or the History tab on first run, sees a blank panel with no explanation. Failed downloads surface as a red status string inside the row (`IsFailed` at `:461`) with no retry affordance, so the only recovery is to search and download again from scratch. The search path is similar: when the pipeline returns zero works there is no distinction between "nothing matched" and "every provider was offline", even though `ProviderRegistry.OfflineSources` knows the answer.

**Recommended solution:**
Add bound empty-state placeholders to both lists, a `Retry` command on `DownloadItemViewModel` when `IsFailed` (re-enqueueing the original `TrackWork`, which `DownloadManager` already accepts), and enrich the zero-result message with `OfflineSources` so the user learns "no results — YouTube, SoundCloud and Deezer are unreachable; check your proxy".

**Ready-to-use agent prompt:**
> Add missing UX affordances. (1) In `src/MusicEngine.App/MainWindow.xaml`, add an empty-state `TextBlock` overlay for the downloads list ("No downloads yet — search for a song and press Download") and for the history list ("Nothing downloaded yet"), each bound to a new VM boolean (`HasDownloads`/`HasHistory`, inverted with the existing converter) and styled with `{StaticResource Subtle}`. (2) In `src/MusicEngine.App/ViewModels/TrackItemViewModel.cs`, add a `RelayCommand RetryCommand` to `DownloadItemViewModel` that is enabled only when `IsFailed`, and wire it in `MainViewModel` to re-enqueue the item's original `TrackWork` through `DownloadManager`; add a retry button to the download row template visible when `IsFailed`. (3) In `src/MusicEngine.App/ViewModels/MainViewModel.cs`, when a search completes with zero works, set the status message to include the offline provider list from `ProviderRegistry.OfflineSources` (e.g. `"No results. Offline sources: YouTube, SoundCloud"`) when that list is non-empty. Build the app and verify each state renders.

---

### 2.3 XAML & Rendering (`XAML-*`)

---

#### `XAML-01: Per-item entrance storyboard fights VirtualizationMode=Recycling`

**Severity:** 🟠 High — animation replays on every scroll; visible flicker; wasted composition work.

**File & location:** `src/MusicEngine.App/MainWindow.xaml:195-196` (virtualization), `:221-223` (`EventTrigger RoutedEvent="Loaded"` → `CardIn`), storyboard defined at `:19`. Same pattern again at `:572-574`.

**Root cause & diagnosis:**
The results `ListBox` correctly enables `IsVirtualizing="True"` with `VirtualizationMode="Recycling"`, then attaches an entrance animation to each item's `Loaded` event. With recycling, containers are *reused* for different data as the user scrolls, and `Loaded` fires again on reuse. The consequence is that scrolling replays the fade/slide animation on rows that were already visible, producing flicker exactly during the interaction where smoothness matters most. The animation also runs for all items in the initial viewport simultaneously, and because `ApplyResults` resets the collection per batch (`MVVM-04`), it replays for the entire list on every streamed batch.

**Recommended solution:**
Animate the *list*, not each row: apply one entrance animation to the `ItemsControl` when a search's first batch arrives. If per-row staggering is desired, drive it from a one-shot VM flag (`IsNew`) that the row clears after animating, so recycled containers do not re-trigger. Alternatively switch to `VirtualizationMode="Standard"` and accept higher memory — but the animation is the cheaper thing to give up.

**Ready-to-use agent prompt:**
> In `src/MusicEngine.App/MainWindow.xaml`, stop the per-item entrance animation from replaying during virtualized scrolling. Remove the `<EventTrigger RoutedEvent="Loaded"><BeginStoryboard Storyboard="{StaticResource CardIn}"/></EventTrigger>` from the results `ItemContainerStyle` (around lines 221-223). Instead, apply a single `CardIn`-style fade/slide to the results `ListBox` itself, triggered by a `DataTrigger` on the VM's `HasResults` becoming `True` (add the `Storyboard` to the ListBox's `Style.Triggers` with an `EnterActions`). Keep `VirtualizingPanel.IsVirtualizing="True"` and `VirtualizationMode="Recycling"` unchanged. Do the same for the second occurrence around line 572 if it is also inside a virtualized items panel. Build and run the app, scroll a long result list, and confirm rows no longer fade in repeatedly.

---

#### `XAML-02: Per-row DropShadowEffect and VisualBrush OpacityMask`

**Severity:** 🟠 High — render-thread cost scales with visible rows; both are known WPF slow paths.

**File & location:** `src/MusicEngine.App/MainWindow.xaml:244` (`DropShadowEffect BlurRadius="10"`), `:256-262` (`Border.OpacityMask` → `VisualBrush`), plus `:233` (`EqLoop` storyboard with `RepeatBehavior="Forever"`).

**Root cause & diagnosis:**
Three separate costs stack on every visible result row:

- **`DropShadowEffect`** forces the row into an intermediate render surface and runs a blur pass. WPF's bitmap effects are the single most common cause of sluggish scrolling in list-heavy apps.
- **`VisualBrush` as `OpacityMask`** for the rounded artwork corners is worse: a `VisualBrush` re-renders its visual tree into an intermediate texture, and using it as a mask means it is evaluated during composition for every row, every frame it changes.
- **`EqLoop` with `RepeatBehavior="Forever"`** keeps an animation clock alive per row, so the render thread never goes idle even when nothing is happening.

**Recommended solution:**
Replace the shadow with a static 1px border plus a subtle background delta (visually near-identical at `Opacity="0.25"`, free to render). Replace the `OpacityMask` with the standard rounded-corner idiom: a `Border` with `CornerRadius` and `ClipToBounds`, or an `Image` inside a `Border` whose `Background` is an `ImageBrush` — no mask needed. Bound `EqLoop` to only run while that row is actually the playing track (`DataTrigger` on `IsPlaying`) so at most one clock is live.

**Ready-to-use agent prompt:**
> Remove the three per-row rendering hot spots from the results `DataTemplate` in `src/MusicEngine.App/MainWindow.xaml`. (1) Delete the `<DropShadowEffect BlurRadius="10" ShadowDepth="1" Opacity="0.25" Color="#000000"/>` (around line 244) and compensate visually by setting that `Border`'s `BorderThickness="1"` with `BorderBrush="{StaticResource BorderSoftBrush}"`. (2) Replace the `Border.OpacityMask` + `VisualBrush` block (around lines 256-262) with a plain rounded `Border` that has `CornerRadius` matching the current mask and `ClipToBounds="True"` wrapping the artwork `Image` — delete the `VisualBrush` entirely. (3) Change the `EqLoop` storyboard (around line 233) so it only runs for the currently-playing row: move its `BeginStoryboard` into a `DataTrigger` on the row's `IsPlaying` (add that boolean to `TrackItemViewModel`, set by `MainViewModel` when preview playback starts/stops) with a matching `StopStoryboard` in the trigger's `ExitActions`. Build, run the app, scroll a 200-row result list and confirm scrolling is smooth.

#### `XAML-03: "BoolToVis" means opposite things in App scope and Window scope`

**Severity:** 🟡 Medium — a latent inverted-visibility bug waiting for the next refactor.

**File & location:** `src/MusicEngine.App/App.xaml:10` (`<local:InverseBoolToVisibility x:Key="BoolToVis"/>`) versus `src/MusicEngine.App/MainWindow.xaml:14` (`<BooleanToVisibilityConverter x:Key="BoolToVis"/>`).

**Root cause & diagnosis:**
The application-level resource named `BoolToVis` is an **inverse** converter (`true` → `Collapsed`), while the window-level resource with the *same key* is the framework's **normal** converter (`true` → `Visible`). Window scope shadows application scope, so the ~12 bindings in `MainWindow.xaml` that reference `BoolToVis` (lines 380, 392, 395, 398, 407, 446, 449, 453, 457, 461, 474, 505) get the normal behaviour, while `SettingsWindow.xaml` — which has no local override — would get the inverted one for the same key name. Any future control moved between the two files, or a `BoolToVis` reference added to a `ControlTemplate` defined in `App.xaml`, silently inverts. This is a genuine trap rather than a style nit.

**Recommended solution:**
Rename by behaviour, not by convention: `BoolToVisibility` for the normal converter and `InvertedBoolToVisibility` for the inverse, declared **once** in `App.xaml`, and delete the window-level duplicates. Then update all references.

**Ready-to-use agent prompt:**
> Eliminate the converter key collision. In `src/MusicEngine.App/App.xaml`, change the resource declarations to `<BooleanToVisibilityConverter x:Key="BoolToVisibility"/>` and `<local:InverseBoolToVisibility x:Key="InvertedBoolToVisibility"/>` (keep `StringToVis` and `BoolToRed` as-is). In `src/MusicEngine.App/MainWindow.xaml`, DELETE the local `<BooleanToVisibilityConverter x:Key="BoolToVis"/>` and `<local:InverseBoolToVisibility x:Key="InverseBoolToVis"/>` declarations (lines 14-15), then update every `{StaticResource BoolToVis}` reference to `{StaticResource BoolToVisibility}` and every `{StaticResource InverseBoolToVis}` to `{StaticResource InvertedBoolToVisibility}`. Do the same sweep in `src/MusicEngine.App/SettingsWindow.xaml`. Grep the whole `src/MusicEngine.App` folder for `BoolToVis` afterwards to confirm zero stale references remain. Build the app and visually verify every conditional element (download row buttons, tab panels, the now-playing bar, the empty state) shows in the correct state.

---

#### `XAML-04: Toast layer spans all rows and steals hit-testing`

**Severity:** 🟡 Medium — invisible overlay can block clicks.

**File & location:** `src/MusicEngine.App/MainWindow.xaml` — the toasts `ItemsControl` at `Grid.Row="0"` with `Grid.RowSpan="6"` and `Panel.ZIndex="100"`.

**Root cause & diagnosis:**
The toast container covers the entire window at the highest Z order. Even with no toasts present, an `ItemsControl` with a non-`Transparent`-but-non-null background, or any layout panel inside it that stretches, participates in hit-testing and can intercept mouse input over the content beneath. The safe pattern is to set `IsHitTestVisible="False"` on the container and re-enable it only on the individual toast borders (which need clicks for the "open file" behaviour in `MainWindow.xaml.cs:188-195`).

**Recommended solution:** `IsHitTestVisible="False"` on the `ItemsControl`, `IsHitTestVisible="True"` on the toast item `Border`, and `Background="Transparent"` only where a click target is needed.

**Ready-to-use agent prompt:**
> In `src/MusicEngine.App/MainWindow.xaml`, find the toasts `ItemsControl` (the one with `Grid.RowSpan="6"` and `Panel.ZIndex="100"`) and set `IsHitTestVisible="False"` on the `ItemsControl` itself and on its `ItemsPanel`. Then set `IsHitTestVisible="True"` and `Background="Transparent"` on the root `Border` inside the toast `ItemTemplate` so the existing `Toast_Click` handler still receives mouse clicks. Verify the container has no explicit non-transparent `Background`. Build and run the app, trigger a toast, confirm it is clickable, and confirm that with no toast showing you can click controls anywhere in the window.

---

#### `XAML-05: History list is not virtualized`

**Severity:** 🟡 Medium — grows unbounded with library size.

**File & location:** `src/MusicEngine.App/MainWindow.xaml:474` (history `ListBox` — `Visibility` bound but no `VirtualizingPanel` attributes, unlike the results list at `:195-196` and the downloads list at `:406`).

**Root cause & diagnosis:**
`AppState.History` accumulates every completed download for the lifetime of the install with no cap. The history `ListBox` realises a container per entry, so a user with a few thousand downloads pays full realisation cost every time they open the History tab. The other two lists already set `IsVirtualizing`; this one was missed. Note also that neither `AppState` nor the UI caps history length, so the underlying `state.json` also grows without bound (and is rewritten synchronously on every download — see `PERF-01`).

**Recommended solution:** Add `VirtualizingPanel.IsVirtualizing="True"` and `VirtualizationMode="Recycling"` to the history and downloads lists, and cap `AppState.History` at a sensible maximum (e.g. 1,000 entries, trimming oldest) so the state file stays small.

**Ready-to-use agent prompt:**
> In `src/MusicEngine.App/MainWindow.xaml`, add `VirtualizingPanel.IsVirtualizing="True"`, `VirtualizingPanel.VirtualizationMode="Recycling"` and `ScrollViewer.CanContentScroll="True"` to the history `ListBox` (around line 474), and add the missing `VirtualizationMode="Recycling"` to the downloads `ListBox` (around line 406). Separately, in `src/MusicEngine/Configuration/AppState.cs`, cap the history list in `PushHistory` so it retains at most the newest 1000 entries (trim from the oldest end before saving), and cap `RecentSearches` at 50. Build and run the offline tests.

---

#### `XAML-06: Nine levels of nested layout panels in the download row template`

**Severity:** 🔵 Low — measure/arrange cost per row, hard to maintain.

**File & location:** `src/MusicEngine.App/MainWindow.xaml:406-470` (the download `ItemTemplate`; the indentation at lines 446-461 reaches ~120 columns).

**Root cause & diagnosis:**
The template nests `Border` → `Grid` → `StackPanel` → `Grid` → `StackPanel` → … deeply enough that the XAML indentation itself signals the problem. Each level costs a measure and arrange pass per row per layout invalidation, and download rows invalidate frequently because progress updates arrive continuously. Several `StackPanel`s wrap a single child, and some `Grid`s exist only to position two elements that a single `Grid` with columns could handle.

**Recommended solution:** Flatten to one `Grid` with explicit rows/columns per row template. Where a `StackPanel` has one child, remove it. This is a mechanical simplification with no visual change if done carefully.

**Ready-to-use agent prompt:**
> Flatten the download row `ItemTemplate` in `src/MusicEngine.App/MainWindow.xaml` (roughly lines 406-470). Restructure it as a single root `Border` containing ONE `Grid` with explicit `ColumnDefinitions` and `RowDefinitions`, placing the title, subtitle, progress bar, status text and action buttons via `Grid.Row`/`Grid.Column` instead of nested `StackPanel`s and inner `Grid`s. Remove every panel that wraps a single child. Preserve all existing bindings, converters, styles and `Visibility` conditions exactly, and keep the visual result identical (same spacing, alignment and sizes). Build and run the app, start two downloads, and visually compare the rows against the previous layout.

---

#### `XAML-07: Static palette brushes prevent full accent theming`

**Severity:** 🔵 Low — partial theme application.

**File & location:** `src/MusicEngine.App/App.xaml:19-20` (`AccentBrush`, `AccentSoftBrush` declared as `SolidColorBrush`), swapped at runtime by `AccentTheme.Apply`; consumed via `DynamicResource` in most places but via `StaticResource` in others.

**Root cause & diagnosis:**
`AccentTheme.Apply` replaces `Application.Resources["AccentBrush"]`, which only propagates to `DynamicResource` references. The audit shows most accent consumers correctly use `DynamicResource` (App.xaml lines 85, 113, 130, 237-238, 265, 283, 368; MainWindow lines 68, 134, 154, 378, 428; SettingsWindow lines 17, 70, 88, 104) — but any `StaticResource AccentBrush` reference would be baked at load time and never update. The pattern is currently correct by discipline rather than by construction, so a future `StaticResource AccentBrush` will introduce a silent theming bug. The brushes are also not frozen, so each is a mutable object shared across the whole visual tree.

**Recommended solution:** Keep `DynamicResource` for accent and add a build-time guard: a small unit test (or a `dotnet` script) that greps the XAML for `StaticResource Accent` and fails if found. Freeze the non-accent palette brushes (`PresentationOptions:Freeze="True"` with the `xmlns:po` namespace) to eliminate change-tracking overhead on the ~14 static brushes.

**Ready-to-use agent prompt:**
> Harden accent theming in `src/MusicEngine.App/App.xaml`. (1) Add the freeze namespace `xmlns:po="http://schemas.microsoft.com/winfx/2006/xaml/presentation/options"` to the `Application` element and add `po:Freeze="True"` to every `SolidColorBrush` in the palette EXCEPT `AccentBrush` and `AccentSoftBrush` (those are swapped at runtime by `AccentTheme.Apply` and must stay mutable/replaceable). (2) Grep all `.xaml` files under `src/MusicEngine.App` for `StaticResource Accent` and convert any hits to `DynamicResource`. (3) Add an XML comment above the `AccentBrush` declaration stating that all consumers must use `DynamicResource` because `AccentTheme.Apply` replaces this resource at runtime. Build the app and switch accents in Settings to confirm every accented element updates live.

### 2.4 Performance & UX (`PERF-*`)

---

#### `PERF-01: Synchronous, blocking persistence on the UI thread`

**Severity:** 🔴 Critical (UX) — measurable jank on every search and every completed download.

**File & location:** `src/MusicEngine.App/ViewModels/MainViewModel.cs:274` (`_state.PushSearch(query)`), `:519` (`_state.PushHistory(...)`), `:591` and `:599` (`_config.Save()`); implementations in `src/MusicEngine/Configuration/AppState.cs` and `AppConfig.cs` (both `File.WriteAllText`).

**Root cause & diagnosis:**
`AppState.PushSearch` and `PushHistory` each call `Save()`, which serializes the entire state (all recent searches **and the full download history**) and writes it synchronously. Both are invoked from `MainViewModel` on the dispatcher thread — `PushSearch` at the top of `SearchAsync` before any await, and `PushHistory` from the download-completed handler which is explicitly marshalled to the UI via `_ui.Run`. The write cost grows with history size (see `XAML-05`: history is uncapped), so the stall gets worse the longer the app is used. `SearchAsync` also uses `.ConfigureAwait(true)` at `:302`, which is correct for a VM but means the whole pipeline resumption happens on the UI thread.

The user-visible symptom is a hitch at the exact moment they press Enter — the worst possible time, because it makes the app feel slow precisely when they expect responsiveness.

**Recommended solution:**
Two changes, both small:

1. **Debounced async writes.** Add `SaveAsync()` using `File.WriteAllTextAsync` plus atomic rename (`BUG-09`), and have `PushSearch`/`PushHistory` mutate the in-memory list immediately and schedule a debounced background flush (e.g. coalesce writes over 500 ms) rather than writing inline.
2. **Off-thread invocation.** Where a synchronous save must remain, call it via `Task.Run` and do not await it on the UI path.

Combine with capping history (`XAML-05`) so the payload stays small regardless.

**Ready-to-use agent prompt:**
> Remove synchronous disk writes from the UI thread. (1) In `src/MusicEngine/Configuration/AppState.cs`, add `public async Task SaveAsync()` that serializes and writes atomically (temp file + `File.Move(overwrite: true)`) using `File.WriteAllTextAsync`, and add a private debounced scheduler: a `CancellationTokenSource? _pendingSave` field and a `private void ScheduleSave()` method that cancels any pending save and starts `Task.Run(async () => { await Task.Delay(500, token); await SaveAsync(); })` swallowing `OperationCanceledException`. Change `PushSearch` and `PushHistory` to mutate the in-memory lists and call `ScheduleSave()` instead of `Save()`. Keep the synchronous `Save()` public method for shutdown flushing. (2) In `src/MusicEngine.App/ViewModels/MainViewModel.cs`, change the `_config.Save()` calls (around lines 591 and 599) to `_ = Task.Run(() => _config.Save());`. (3) In `src/MusicEngine.App/App.xaml.cs` `OnExit`, add a final synchronous `sp.GetRequiredService<AppState>().Save();` inside the try so pending debounced changes are flushed before exit. Build the solution and run the offline tests.

---

#### `PERF-02: FinglishConverter.ScoreAlternatives scans the whole dictionary per candidate`

**Severity:** 🟠 High — O(candidates × dictionary) on the hottest text path.

**File & location:** `src/MusicEngine/Text/FinglishConverter.cs:285-287` (`Dict.Value.Values.Contains(persian, StringComparer.Ordinal)`), called from the candidate loop at `:274-275`; also `FindLatinForPersian` at `:113-123` (linear scan over `Dict.Value`).

**Root cause & diagnosis:**
`ScoreAlternatives` asks "is this Persian string a known dictionary value?" using `Enumerable.Contains` over `Dictionary.Values` — a linear scan of the entire embedded f2p dictionary. It is invoked **once per candidate**, and the candidate cross-product is capped at `MaxCombinations = 512` (`:236`, `:262`). So a single unknown word can cost up to 512 × |dictionary| string comparisons. This runs during query expansion *and* during title normalisation for gating, i.e. once per provider row across nine providers. `FindLatinForPersian` has the same shape: a `foreach` over every dictionary entry to find one reverse match.

**Recommended solution:**
Build the reverse index once. Add a `Lazy<Dictionary<string, string>> ReverseDict` (Persian → Latin, first-wins) and a `Lazy<HashSet<string>> KnownPersianValues` alongside the existing lazy tables. `ScoreAlternatives` then becomes an O(1) `HashSet.Contains`, and `FindLatinForPersian` an O(1) dictionary lookup. Memory cost is one extra reference per entry; the tables are already fully materialised.

**Ready-to-use agent prompt:**
> Add reverse indexes to `src/MusicEngine/Text/FinglishConverter.cs` to remove linear dictionary scans. (1) Alongside the existing `Lazy<Dictionary<string,string>>` fields, add `private static readonly Lazy<HashSet<string>> KnownPersian = new(() => new HashSet<string>(Dict.Value.Values, StringComparer.Ordinal));` and `private static readonly Lazy<Dictionary<string,string>> ReverseDict = new(() => { var d = new Dictionary<string,string>(StringComparer.Ordinal); foreach (var (latin, persian) in Dict.Value) d.TryAdd(persian, latin); return d; });`. (2) In `ScoreAlternatives` (around line 287), replace `Dict.Value.Values.Contains(persian, StringComparer.Ordinal)` with `KnownPersian.Value.Contains(persian)`. (3) Rewrite `FindLatinForPersian` (around lines 113-123) to `return ReverseDict.Value.TryGetValue(persianWord.Trim(), out var latin) ? latin : null;`, preserving the empty-string guard. Do not change any scoring weights or return values. Build and run the offline tests — every Finglish conversion and cross-script overlap test must still pass, since this is a pure performance change.

---

#### `PERF-03: SearchService re-runs the full pipeline for identical repeated queries within a session`

**Severity:** 🟠 High — several seconds of avoidable latency and network traffic.

**File & location:** `src/MusicEngine/Search/SearchService.cs` (cache consulted early in the pipeline), `src/MusicEngine/Search/ProviderHealth.cs:72-111` (`SearchResultCache`), registered at `App.xaml.cs:79`.

**Root cause & diagnosis:**
The cache exists and is wired, but its key is `TrackTextNormalizer.Normalize(rawQuery)` on the **raw user query only**. The pipeline immediately expands the query through `FinglishQueryExpander` into multiple variants; a user who searches `tataloo behesht` and then `تتلو بهشت` — semantically the same search, and the expander knows it — gets two complete nine-provider fan-outs. There is also no caching at the *provider* layer, so a rescue round that re-queries the same provider with a slightly different variant repeats the same HTTP request.

**Recommended solution:**
Key the cache on the canonical expanded form (the sorted set of expansion variants, or the resolved `GoalSong` identity) so cross-script duplicates hit. Add a short-TTL (30-60 s) per-provider response cache keyed by `(ProviderId, normalizedQuery)` so rescue rounds and re-searches within a session are free.

**Ready-to-use agent prompt:**
> Improve search cache hit rate in `src/MusicEngine/Search/SearchService.cs` and `src/MusicEngine/Search/ProviderHealth.cs`. (1) Change the cache key used when consulting and storing `SearchResultCache` from the normalized raw query to a canonical key derived from the expansion set: compute `string.Join("|", FinglishQueryExpander.Expand(query).Select(TrackTextNormalizer.Normalize).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal))`. Extract this into a `internal static string CanonicalCacheKey(string query)` helper on `SearchService` so it can be unit-tested. Keep `SearchResultCache`'s public API unchanged — it should receive the already-canonical key. (2) Add a new `sealed class ProviderResponseCache` in `src/MusicEngine/Search/ProviderHealth.cs` with a 45-second TTL, a `ConcurrentDictionary<(ProviderId, string), (DateTimeOffset At, IReadOnlyList<SearchResult> Rows)>` store, a 256-entry cap, and `TryGet`/`Store` methods; consult it in `CollectAsync` before invoking a provider and store the rows after a successful collection. Register it as a DI singleton in `src/MusicEngine.App/App.xaml.cs` next to `SearchResultCache` and pass it into `SearchService`'s constructor as an optional parameter defaulting to null (no caching) so the test harness keeps working. Build and add two offline tests in `tests/MusicEngine.Tests/Program.cs` asserting that `CanonicalCacheKey("tataloo behesht")` equals `CanonicalCacheKey("تتلو بهشت")`, then run the offline tests.

---

#### `PERF-04: Startup does sequential blocking work before the window appears`

**Severity:** 🟠 High — slow perceived cold start.

**File & location:** `src/MusicEngine.App/App.xaml.cs:48-131` — `AppConfig.Load()` at `:48`, `Directory.CreateDirectory` at `:49`, `AppState.Load()` at `:50`, provider construction triggered at `:112-113`, window creation at `:127`.

**Root cause & diagnosis:**
`OnStartup` performs, in order on the UI thread: two synchronous JSON reads, a directory creation, DI graph construction that instantiates all nine providers (two of which spawn blocking processes — `BUG-05`), accent theme application, reachability priming, and only then `window.Show()`. Nothing is shown to the user until all of it completes. `AppState.Load()` in particular deserializes the entire uncapped download history.

**Recommended solution:**
Show the window first, then warm up. Concretely: load config synchronously (it's small and needed for theming), but defer `AppState.Load()` and provider instantiation until after `window.Show()`. Register providers with factory lambdas so DI creates them lazily on first resolve rather than eagerly, and move the first `RefreshRoutesAsync` and the SoundCloud warm-up (already `Task.Run`) to after the window is visible. Combined with `BUG-05`, cold start should drop to near-instant.

**Ready-to-use agent prompt:**
> Restructure `OnStartup` in `src/MusicEngine.App/App.xaml.cs` so the window appears before warm-up work. Move `window.Show()` to immediately after `_services = services.BuildServiceProvider();` and `AccentTheme.Apply(config.Accent);`, and move everything currently between DI construction and `window.Show()` — the `Reachability`/`ProviderRegistry` resolution, the first `RefreshRoutesAsync`, the `RoutesChanged` subscription, the 10-minute `DispatcherTimer`, and the SoundCloud warm-up `Task.Run` — into a new `private void BeginWarmup()` called AFTER `window.Show()` and `InitTray(window)`. Wrap the whole `BeginWarmup` body in try/catch that writes to `CrashLog`. Additionally, change `services.AddSingleton(state)` so `AppState.Load()` runs inside a factory lambda (`services.AddSingleton(_ => AppState.Load())`) instead of eagerly before DI construction, and confirm nothing before `window.Show()` resolves `AppState`. Build and run the app, and report the observed time from launch to visible window.

---

#### `PERF-05: Artwork is decoded and cached per row with no shared image cache`

**Severity:** 🟡 Medium — repeated network fetches and decodes for the same album art.

**File & location:** `src/MusicEngine.App/ViewModels/MainViewModel.cs:605-620` (`_artwork.LoadAsync` + `BitmapImageFromBytes` with `DecodePixelWidth = 128` and `Freeze()`); `src/MusicEngine/Http/ArtworkLoader.cs`.

**Root cause & diagnosis:**
The decode side is done well — `DecodePixelWidth = 128` avoids full-resolution decoding and `Freeze()` makes the bitmap cross-thread safe and render-cacheable. What is missing is a cache: `ArtworkLoader.LoadAsync` performs a fresh `GetAsync` + `ReadAsByteArray` for every URL, every time. Because a `TrackWork` groups multiple versions of the same song, and because `ApplyResults` currently rebuilds the collection per batch (`MVVM-04`), the same artwork URL is fetched and decoded repeatedly within a single search. `ArtworkLoader` also swallows all exceptions, so a failing image is silently retried on each rebuild.

**Recommended solution:**
Add a bounded `ConcurrentDictionary<string, Task<byte[]?>>` (or `BitmapImage`) cache in `ArtworkLoader` keyed by URL, so concurrent requests for the same URL share one fetch and later requests are free. Cache frozen `BitmapImage`s rather than bytes to skip re-decoding. Cap at a few hundred entries with simple eviction. Add an on-disk cache under `%LOCALAPPDATA%\MusicEngine\artwork` if persistence across sessions is wanted.

**Ready-to-use agent prompt:**
> Add an in-memory artwork cache. In `src/MusicEngine/Http/ArtworkLoader.cs`, add `private readonly ConcurrentDictionary<string, Task<byte[]?>> _cache = new();` and change `LoadAsync(string uri)` to return `_cache.GetOrAdd(uri, u => FetchAsync(u))`, moving the current body into a private `FetchAsync`. Add eviction: when `_cache.Count > 256`, clear it (document the simple strategy in a comment). Register a cleanup continuation so a failed fetch removes its entry, allowing a later retry. Then in `src/MusicEngine.App/ViewModels/MainViewModel.cs`, add a second-level cache for decoded images: `private readonly Dictionary<string, System.Windows.Media.Imaging.BitmapImage> _decodedArtwork = new();` consulted before calling `BitmapImageFromBytes`, storing the frozen result. Cap it at 256 entries, clearing when exceeded. Keep `DecodePixelWidth = 128` and the `Freeze()` call. Build the solution.

---

#### `PERF-06: No cancellation of in-flight artwork loads when results are replaced`

**Severity:** 🟡 Medium — wasted bandwidth during rapid searching.

**File & location:** `src/MusicEngine.App/ViewModels/MainViewModel.cs:605-620` (artwork load fired per item without the search token).

**Root cause & diagnosis:**
Artwork loads are started per row and awaited with `ConfigureAwait(false)` but are not linked to `_searchCts`. When the user starts a new search, the previous search's artwork downloads continue to completion and then assign images to `TrackItemViewModel`s that may already have been discarded. On a slow proxy — the app's normal condition — a user typing several searches in a row can have dozens of orphaned image downloads competing for the same constrained connection with the searches they actually care about.

**Recommended solution:** Pass the search `CancellationToken` into `LoadAsync` and check it before assigning the decoded image. `IArtworkLoader.LoadAsync` should take a `CancellationToken` parameter.

**Ready-to-use agent prompt:**
> Make artwork loading cancellable. (1) In `src/MusicEngine/Abstractions.cs`, add a `CancellationToken ct = default` parameter to `IArtworkLoader.LoadAsync`. (2) Update `src/MusicEngine/Http/ArtworkLoader.cs` to accept and forward it to the `GetAsync`/`ReadAsByteArrayAsync` calls (note: if you also implement the caching from PERF-05, key the cache on URL only and apply cancellation at the await site with `.WaitAsync(ct)` so a cancelling caller does not poison the shared task). (3) In `src/MusicEngine.App/ViewModels/MainViewModel.cs`, pass the current search token into the artwork load call around line 612 and add an early `if (ct.IsCancellationRequested) return;` before assigning the decoded `BitmapImage` to the item. Swallow `OperationCanceledException` silently. Build the solution and run the offline tests.

---

#### `PERF-07: Download resolve and search phases have no user-visible progress detail`

**Severity:** 🟡 Medium — UX opacity during the slowest operations.

**File & location:** `src/MusicEngine/Downloads/DownloadManager.cs` (`ResolveCandidatesAsync` with a 30-second resolve timeout and the slow-tier `Task.WhenAny` budget); `MainWindow.xaml` status/spinner bindings.

**Root cause & diagnosis:**
Resolving a download can legitimately take 30 seconds while `DownloadManager` ranks candidates, consults the Iranian slow tier, and falls back to yt-dlp. During that window the row shows the `Resolving` phase with a generic message and no indication of which source is being tried or how long remains. Search has the same shape: `Callbacks.Status` messages exist and are rich (the test harness prints them), but the UI collapses them into a single status line without per-provider state. A user on a slow proxy cannot distinguish "working" from "hung".

**Recommended solution:**
Surface the structured information already flowing through the system: show the current source being tried in the download row, and render a per-provider chip strip during search (pending / responded / timed out / offline), driven by `ProviderHealthMonitor` and `ProviderRegistry.OfflineSources`. Add an elapsed-time indicator for operations over ~3 seconds.

**Ready-to-use agent prompt:**
> Surface pipeline detail in the UI. (1) In `src/MusicEngine/Downloads/DownloadManager.cs`, ensure the `DownloadProgress.Message` emitted during the `Resolving` phase names the provider currently being attempted (e.g. `$"Trying {provider.DisplayName}…"`) — add the provider name to the existing progress reports without changing the `DownloadProgress` record shape. (2) In `src/MusicEngine.App/ViewModels/MainViewModel.cs`, add an `ObservableCollection<ProviderStatusViewModel> ProviderStatuses` populated at search start from `ProviderRegistry.EnabledSearchProviders()`, with a `State` enum (Pending/Responded/TimedOut/Offline) updated from the `SearchService.Callbacks.Status` messages and from `ProviderRegistry.OfflineSources`. (3) In `src/MusicEngine.App/MainWindow.xaml`, add a horizontal `ItemsControl` chip strip bound to `ProviderStatuses` beneath the search bar, using the existing `Chip` style with a `DataTrigger` per state for colour (`AccentBrush` responded, `WarnBrush` pending, `DangerBrush` timed out, `FaintTextBrush` offline), visible only while `IsSearching` or when any provider is offline. Build and run the app and confirm the chips update during a live search.

### 2.5 Code Quality & Modernization (`MODERN-*`)

---

#### `MODERN-01: Configuration and state are concrete classes with no abstraction`

**Severity:** 🟠 High — the single biggest blocker to unit-testing the engine.

**File & location:** `src/MusicEngine/Configuration/AppConfig.cs` and `AppState.cs` (static `Load()`, instance `Save()`, no interface); consumed directly by `YtDlpProvider:43`, `PersianIndexProvider:35`, `DownloadManager`, `MainViewModel`, `SettingsWindow:19`, `MainWindow:201`, and the test harness.

**Root cause & diagnosis:**
`AppConfig` is a concrete class whose `Load()` is a static factory reading a fixed path and whose `Save()` writes to that same fixed path. Every consumer therefore has a hard dependency on the file system. `tests/MusicEngine.Tests/Program.cs` demonstrates the consequence: it calls `Configuration.AppConfig.Load()` at least eight times (lines 33, 121, 165, 219, 263, 479, 531, 682) and mutates `cfg.OutputDirectory` to redirect writes (`:245`, `:683`) — testing against the developer's real settings file. There is no way to construct a provider with a synthetic configuration.

**Recommended solution:**
Introduce `ISettings` (read surface) and `ISettingsWriter` (persist surface), implemented by `AppConfig`. Providers take `ISettings`. Keep `AppConfig.Load()` as the production factory. This is a non-breaking, additive change that immediately makes every provider constructible in a test with an inline settings object.

**Ready-to-use agent prompt:**
> Add a settings abstraction to the engine. (1) In `src/MusicEngine/Configuration/`, create `ISettings.cs` declaring a read-only interface with every property currently read by consumers: `string OutputDirectory`, `string? ProxyUrl`, `int MaxParallelDownloads`, `string? YtDlpPath`, `string? FfmpegPath`, `string PythonPath`, `string? CookiesBrowser`, `string? CookiesFile`, `bool EnablePersianIndex`, `int SearchTimeoutSeconds`, `int BitrateKbps`, `FilenameTemplate FilenameTemplate`, `string Accent`, `bool ClipboardMonitor`, `bool MinimizeToTray`, `bool DownloadToasts`, `IReadOnlyCollection<string> DisabledSources`, and `bool IsSourceEnabled(ProviderId id)`. (2) Make `AppConfig : ISettings` — no property changes needed, just widen `DisabledSources` exposure if required. (3) Change the constructor parameter type from `AppConfig` to `ISettings` in `YtDlpProvider`, `PersianIndexProvider`, `DownloadManager`, and `ProviderRegistry`. Leave `MainViewModel` and the WPF layer on the concrete `AppConfig` since they also write. (4) In `src/MusicEngine.App/App.xaml.cs`, add `services.AddSingleton<ISettings>(sp => sp.GetRequiredService<AppConfig>());` so both resolve to the same instance. Build the solution and run the offline tests — the test harness should still compile unchanged.

---

#### `MODERN-02: No real test framework; the test project is a console harness`

**Severity:** 🟠 High — no CI signal, no per-test isolation, no assertion diagnostics.

**File & location:** `tests/MusicEngine.Tests/Program.cs` (1,010 lines: a hand-rolled `Test`/`TestAsync`/`Live` runner plus six debug subcommands).

**Root cause & diagnosis:**
The file mixes genuinely valuable engineering with the wrong packaging. The four `HttpDownloader` tests (`:437-444`, implementations `:742-823`) are excellent — they spin up a real `HttpListener` with Range support, throttling and injected 503s, and verify byte-exact resume, URL-bound state, truncation rejection and chunk retry. That is better download-engine testing than most projects have. But it all runs through `Console.WriteLine` + a `_failures` counter and `Environment.Exit(1)`, which means: no test discovery, no `dotnet test`, no CI integration, no isolation between tests, no structured failure output, and the offline tests are interleaved with six interactive debug modes (`bisect`, `debugsc`, `gatecheck`, `fifty`, `debugapp`, `dl`, `debugpersian`) that are really developer tools, not tests.

**Recommended solution:**
Split into two projects: `MusicEngine.Tests` (xUnit — the assertions, `[Fact]`/`[Theory]`, plus the `LocalRangeServer` as a fixture) and `MusicEngine.DevTools` (a console app retaining the debug subcommands). The assertions port almost mechanically since each is already a `Func<bool>`.

**Ready-to-use agent prompt:**
> Convert the test harness to xUnit while preserving every existing assertion. (1) Add xUnit to `tests/MusicEngine.Tests/MusicEngine.Tests.csproj`: `xunit` 2.9.2, `xunit.runner.visualstudio` 2.8.2, `Microsoft.NET.Test.Sdk` 17.11.1, and change `OutputType` to `Library`. (2) Create `TextPipelineTests.cs` and port each `Test("name", () => assertion)` call from `Program.cs` lines 300-434 into a `[Fact]` method with a descriptive name, replacing the boolean return with `Assert.True(...)` / `Assert.Equal(...)` so failures report actual values. (3) Create `HttpDownloaderTests.cs` and port the four `TestAsync` download-engine tests (lines 437-444 and their implementations at 742-823) into `[Fact] async Task` methods, moving `LocalRangeServer`, `RandomBytes`, `NewTempTarget`, `FileMatchesAsync`, `DownloadedFromStateAsync` and `StartAndCancelMidwayAsync` into the same file or a shared `TestSupport.cs`. Keep the temp-directory `try/finally` cleanup. (4) Create a NEW project `tools/MusicEngine.DevTools/` (console, referencing `src/MusicEngine`) and move the `bisect`, `debugsc`, `gatecheck`, `fifty`, `debugapp`, `dl` and `debugpersian` subcommands there verbatim, plus the `Live`/`RunLiveTestsAsync` network smoke tests. Add both projects to `MusicEngine.sln`. (5) Verify with `D:\dotnet-sdk\dotnet.exe test tests\MusicEngine.Tests` that all ported tests pass, and `D:\dotnet-sdk\dotnet.exe run --project tools\MusicEngine.DevTools -- fifty` still works.

---

#### `MODERN-03: SearchService is a 660-line class holding the entire pipeline`

**Severity:** 🟡 Medium — high cognitive load, hard to test a stage in isolation.

**File & location:** `src/MusicEngine/Search/SearchService.cs` (whole file; `CollectAsync` alone spans `:590-660`).

**Root cause & diagnosis:**
One class performs URL resolution, cache lookup, query expansion, catalog fan-out, goal resolution, gating (strict and loose), artist-mode branching, retrieval fan-out, rescue rounds, work grouping and ranking. The static gate methods (`PassesGoalGate`, `PassesLooseGate`) are public purely so the test harness can call them (`Program.cs:72-73`, `:295`) — a sign the seams want to be real types. Because the stages share mutable locals, adding a stage means understanding the whole method.

**Recommended solution:**
Extract the stages behind small interfaces, keeping `SearchService` as the orchestrator: `IQueryPlanner` (expansion + provider plans), `IGoalGate` (the two gate methods, already static and pure — ideal first extraction), `IProviderFanOut` (`CollectAsync`), `IResultAssembler` (grouping + ranking). Start with `IGoalGate` since it is pure and already has test coverage.

**Ready-to-use agent prompt:**
> Extract the first seam from `src/MusicEngine/Search/SearchService.cs` without changing behaviour. Create `src/MusicEngine/Search/GoalGate.cs` containing a `public interface IGoalGate { bool PassesStrict(SearchResult r, GoalSong goal); bool PassesLoose(SearchResult r, GoalSong goal); }` and a `public sealed class GoalGate : IGoalGate` whose methods contain the bodies of the existing static `SearchService.PassesGoalGate` and `PassesLooseGate` moved verbatim. In `SearchService`, keep the existing public static methods as thin delegating wrappers marked `[Obsolete("Use IGoalGate")]`-free (do NOT add the attribute — the test harness calls them) that forward to a private static `GoalGate` instance, and add an optional `IGoalGate? gate = null` constructor parameter used by the instance pipeline, defaulting to `new GoalGate()`. Do not change any gating logic, thresholds or comparison order. Build and run the offline tests — the `gatecheck` behaviour and all gate-related assertions must produce identical results.

---

#### `MODERN-04: ProviderHealth.cs holds two unrelated public types`

**Severity:** 🔵 Low — discoverability; a real cost during this audit.

**File & location:** `src/MusicEngine/Search/ProviderHealth.cs` — `ProviderHealthMonitor` at `:1-60` and `SearchResultCache` at `:72-111`.

**Root cause & diagnosis:**
`SearchResultCache` — a query-result cache — lives inside a file named after provider health tracking. The two share nothing. This was discovered the slow way during this audit (a search for `SearchResultCache.cs` returned nothing). The same one-type-per-file convention is followed correctly nearly everywhere else in the engine, so this is an outlier. `Models/TrackModels.cs`, `WorkModels.cs` and `DownloadModels.cs` group closely-related records, which is reasonable; this is not that.

**Recommended solution:** Move `SearchResultCache` to `src/MusicEngine/Search/SearchResultCache.cs`.

**Ready-to-use agent prompt:**
> In `src/MusicEngine`, move the `SearchResultCache` class (currently at the bottom of `Search/ProviderHealth.cs`, roughly lines 62-111 including its XML doc comment) into a new file `Search/SearchResultCache.cs` with the same `namespace MusicEngine.Search;` and the same `using` directives it needs. Leave `ProviderHealthMonitor` in `ProviderHealth.cs`. No code changes beyond the move. Build the solution.

---

#### `MODERN-05: Modernization opportunities across the engine`

**Severity:** 🔵 Low — readability and allocation wins; zero behavioural risk if done carefully.

**File & location:** engine-wide. Representative sites listed below.

**Root cause & diagnosis:**
The codebase already uses file-scoped namespaces, `required` members, records, target-typed `new`, list patterns and `u8` literals (`AudioFile.cs`'s `"ftyp"u8` is a nice touch), so the baseline is modern. Remaining gaps:

| Opportunity | Sites |
|---|---|
| Primary constructors | Most providers assign 3-6 ctor parameters to readonly fields verbatim |
| `[GeneratedRegex]` | See `BUG-15`; also worth migrating the existing `RegexOptions.Compiled` statics |
| `FrozenDictionary` / `FrozenSet` | `FinglishConverter` tables and `JunkFilter` arrays are build-once/read-many — ideal fits |
| Collection expressions (`[...]`) | `ProviderHosts` arrays, `JunkFilter` static arrays |
| `ArgumentNullException.ThrowIfNull` | Constructor guards, where present at all |
| `TimeProvider` | `DateTimeOffset.UtcNow` in `SearchResultCache` and `ProviderHealthMonitor` blocks time-based testing |
| `System.Threading.Lock` | The `object _lock` fields (a .NET 9 item; note for later) |
| Formatting cleanup | `App.xaml.cs:77` has two statements on one line and `:90-102` is misindented; `DownloadModels.cs`'s `DownloadPhase` enum has irregular indentation |

**Recommended solution:** Apply in one mechanical pass with a build after each file. The `FrozenDictionary` change is the only one with a measurable performance effect (faster lookups on the Finglish hot path); the rest are readability.

**Ready-to-use agent prompt:**
> Apply mechanical modernization to `src/MusicEngine`, building after each file with `D:\dotnet-sdk\dotnet.exe build MusicEngine.sln -c Release`. (1) In `src/MusicEngine/Text/FinglishConverter.cs`, change the `Lazy<Dictionary<...>>` tables to `Lazy<System.Collections.Frozen.FrozenDictionary<string,string>>` by appending `.ToFrozenDictionary(StringComparer.Ordinal)` in each factory, and in `src/MusicEngine/Text/JunkFilter.cs` convert the static string arrays used for `Contains` checks into `FrozenSet<string>` via `.ToFrozenSet(StringComparer.OrdinalIgnoreCase)`. Keep all lookup semantics and comparers identical. (2) Convert providers whose constructors only assign parameters to readonly fields to primary constructors — do `ITunesProvider`, `Nex1MusicProvider` and `PersianSitesProvider` only (skip `YtDlpProvider` and `PersianIndexProvider`, which have real constructor logic). (3) Replace array initializers with collection expressions (`[a, b, c]`) in `src/MusicEngine/Network/ProviderHosts.cs`. (4) Add `ArgumentNullException.ThrowIfNull` guards for reference-type constructor parameters in `SearchService`, `DownloadManager` and `ProviderRegistry`. (5) Fix formatting: in `src/MusicEngine.App/App.xaml.cs` split the two statements sharing line 77 onto separate lines and re-indent lines 90-102 to 8 spaces; in `src/MusicEngine/Models/DownloadModels.cs` normalize the `DownloadPhase` enum member indentation. Do not change any behaviour, and run the offline tests at the end.

---

#### `MODERN-06: Duplicated per-provider enumeration in four places`

**Severity:** 🟡 Medium — adding a provider requires edits in ~6 files with no compiler help.

**File & location:** `src/MusicEngine/Models/ProviderId.cs` (the enum), `src/MusicEngine/Network/ProviderHosts.cs` (`For`/`DownloadFor` switches), `src/MusicEngine.App/App.xaml.cs:62-75` (nine registrations), `src/MusicEngine.App/SettingsWindow.xaml.cs:32-39`, `:111-118`, `:128-135` (three separate eight-way lists), `src/MusicEngine.App/SettingsWindow.xaml` (eight hard-coded checkboxes), and `src/MusicEngine/Downloads/DownloadManager.cs` (`DownloadRank` switch).

**Root cause & diagnosis:**
The provider set is spelled out independently in each of these places. Nothing links them, so omitting one produces a silent gap: a new provider that works in search but is invisible in Settings, or unrankable in `DownloadRank` (falling into the default branch and ranking last for no stated reason). The `SettingsWindow` triplication is the worst offender.

**Recommended solution:**
Make the enum the single source of truth and derive the rest. Add a `ProviderCatalog` static exposing `IReadOnlyList<ProviderDescriptor>` where a descriptor carries `Id`, `DisplayName`, `Tier`, `Hosts`, `DownloadHosts`, `DownloadRank` and `UserSelectable`. Settings then binds to a projection of it (see `MVVM-02`), `ProviderHosts` reads from it, and `DownloadRank` becomes a lookup. Add a test asserting every `ProviderId` except `Unknown` has a descriptor — that test is what actually prevents the silent gap.

**Ready-to-use agent prompt:**
> Create a single source of truth for providers. Add `src/MusicEngine/Providers/ProviderCatalog.cs` with `public sealed record ProviderDescriptor(ProviderId Id, string DisplayName, SearchTier Tier, string[] Hosts, string[] DownloadHosts, int DownloadRank, bool UserSelectable)` and a `public static class ProviderCatalog` exposing `public static IReadOnlyList<ProviderDescriptor> All { get; }` plus `public static ProviderDescriptor Get(ProviderId id)`. Populate it by moving the data currently hard-coded in `src/MusicEngine/Network/ProviderHosts.cs` (the per-`ProviderId` host lists from `For` and `DownloadFor`) and the rank values from the `DownloadRank` switch in `src/MusicEngine/Downloads/DownloadManager.cs`. Then rewrite `ProviderHosts.For`/`DownloadFor` and `DownloadManager.DownloadRank` as lookups into `ProviderCatalog`, keeping their existing public signatures and returning identical values for every provider (verify each one against the original code). Set `UserSelectable = false` for `Unknown`, `Spotify` and `YtDlp`. Finally add a test in `tests/MusicEngine.Tests` asserting that every `Enum.GetValues<ProviderId>()` value except `Unknown` has a `ProviderCatalog` entry, and that `ProviderHosts.For` returns the same array contents as before for all providers. Build and run the offline tests.

---

#### `MODERN-07: Duplicated proxyUrl plumbing through every provider constructor`

**Severity:** 🔵 Low — repetitive wiring, easy to get inconsistent.

**File & location:** `src/MusicEngine.App/App.xaml.cs:63-70` (four providers get `proxyUrl: config.ProxyUrl` explicitly, five do not), mirrored in `tests/MusicEngine.Tests/Program.cs` at seven separate construction sites.

**Root cause & diagnosis:**
`SharedHttpClient` already knows the proxy URL (constructed with it at `App.xaml.cs:58`) and `Reachability` already knows it (`:57`), yet four providers additionally receive it as a separate constructor argument. The asymmetry — `DeezerProvider`, `YouTubeProvider`, `SoundCloudProvider`, `RadioJavanProvider` take it; `ITunesProvider`, `Nex1MusicProvider`, `PersianSitesProvider` do not — is undocumented, and a reader cannot tell whether it is meaningful or accidental. The test harness reproduces the same pattern seven times, so a change to the convention means fourteen edits.

**Recommended solution:** Let providers obtain the proxy from the injected `SharedHttpClient` (expose `ProxyUrl` on it, which it already stores) and drop the parameter. If a provider genuinely needs the raw URL for a sub-process (`YtDlpProvider` passes `--proxy`), get it from `ISettings` (`MODERN-01`) rather than a bespoke parameter.

**Ready-to-use agent prompt:**
> Remove the redundant `proxyUrl` constructor parameter from providers that already have `SharedHttpClient`. (1) In `src/MusicEngine/Http/SharedHttpClient.cs`, add `public string? ProxyUrl { get; }` set from the constructor argument (it is already stored in a field — expose it). (2) In `DeezerProvider`, `YouTubeProvider`, `SoundCloudProvider` and `RadioJavanProvider`, delete the `proxyUrl` constructor parameter and read `http.ProxyUrl` internally wherever the field is currently used. (3) Update all construction sites: `src/MusicEngine.App/App.xaml.cs` lines 63-70 (they can then use the plain `services.AddSingleton<T>()` form like the other providers) and every occurrence in `tests/MusicEngine.Tests/Program.cs`. Leave `YtDlpProvider` and `PersianIndexProvider` alone — they need the raw URL for sub-process arguments and get it from config. Build the solution and run the offline tests.

---

#### `MODERN-08: Dead and vestigial code`

**Severity:** 🔵 Low.

**File & location:** `src/MusicEngine/Network/Reachability.cs:47` (`public string? ProxyUrl { get; }` — never assigned, always `null`); `src/MusicEngine/Models/ProviderId.cs` (`Spotify` is declared but no `SpotifyProvider` exists; it is only reachable through `UrlQueryResolver`'s oEmbed path); `Reachability.cs:110-113` (an empty `else` branch containing only a comment); repo root `fifty-results*.txt` artifacts and several overlapping analysis documents (`DOWNLOAD_ISSUES_ANALYSIS.md`, `DOWNLOAD_ISSUES_DEEP_ANALYSIS.md`, `GEMINI_DOWNLOAD_FIX_PROMPT.md`, `GEMINI_DOWNLOAD_QUICKWINS_PROMPT.md`).

**Root cause & diagnosis:**
`Reachability.ProxyUrl` is an auto-property with no setter assignment anywhere in the constructor — every read returns `null`, which is a trap for any future caller that trusts it. The `else` branch at `:110-113` swallows an invalid proxy URL with only a comment where a log call belongs. The root-level `.txt` result dumps and four overlapping markdown analyses make it unclear which document is current.

**Recommended solution:** Either assign `ProxyUrl` in the constructor or delete it (deleting is safer — nothing reads it). Replace the empty `else` with a logged warning. Consolidate the root markdown files into this roadmap plus `README.md`/`HANDOFF.md`, and gitignore the `fifty-results*.txt` artifacts.

**Ready-to-use agent prompt:**
> Clean up dead code. (1) In `src/MusicEngine/Network/Reachability.cs`, grep the solution for `\.ProxyUrl` on a `Reachability` instance; if there are no consumers, delete the never-assigned `public string? ProxyUrl { get; }` property at line 47. If there are consumers, assign it from the constructor parameter instead. (2) Replace the empty `else` branch at lines 110-113 with a real diagnostic: add an optional `ILogger<Reachability>? logger = null` constructor parameter (defaulting to `NullLogger`) and log a warning naming the unparseable proxy URL. (3) Add `fifty-results*.txt` to `.gitignore`. (4) Do NOT delete any root-level markdown files — instead report which of `DOWNLOAD_ISSUES_ANALYSIS.md`, `DOWNLOAD_ISSUES_DEEP_ANALYSIS.md`, `GEMINI_DOWNLOAD_FIX_PROMPT.md` and `GEMINI_DOWNLOAD_QUICKWINS_PROMPT.md` describe issues already fixed in the current code, so the user can decide what to archive. Build the solution.

---

#### `MODERN-09: No structured logging sink in Release builds`

**Severity:** 🟠 High (diagnosability) — see also `FEAT-01`.

**File & location:** `src/MusicEngine.App/App.xaml.cs:53` (`b.AddDebug().SetMinimumLevel(LogLevel.Information)`), `src/MusicEngine.App/CrashLog.cs`.

**Root cause & diagnosis:**
The only logging provider is `AddDebug()`, which writes to the debugger output window and is a no-op for a user running the published exe. Providers dutifully call `_logger.LogWarning`/`LogDebug` throughout — `SearchService:627-633` logs provider timeouts and failures with structured parameters — and every one of those messages is discarded in production. `CrashLog` catches only the three global handler paths. The result is that field diagnosis relies entirely on reproducing the issue with a debugger attached.

**Recommended solution:** Add a rolling file sink at `%APPDATA%\MusicEngine\logs\app-{date}.log` with size/age-based retention, keeping `AddDebug()` for development. See `FEAT-01` for the full prompt.

**Ready-to-use agent prompt:** See `FEAT-01`.

### 2.6 Potential Features & Architectural Enhancements (`FEAT-*`)

---

#### `FEAT-01: Rolling file logging with a user-accessible "open logs" action`

**Severity:** 🟠 High value / low effort.

**Rationale:** Everything in `MODERN-09` — providers already emit well-structured log messages that are thrown away in Release. A file sink makes every existing `LogWarning`/`LogDebug` call in the engine immediately useful, turning "no results for X" from unreproducible into a one-file diagnosis. This is the highest value-per-line change in this document.

**Recommended solution:**
Add a small custom `ILoggerProvider` writing to `%APPDATA%\MusicEngine\logs\app-yyyy-MM-dd.log` with a background queue (never block the caller), a size cap per file and retention of the last 7 days. Avoid a new NuGet dependency if you prefer — a ~120-line provider is sufficient — or use Serilog if a dependency is acceptable. Add a "Open logs folder" button in Settings next to the existing crash-log path.

**Ready-to-use agent prompt:**
> Add file logging. (1) Create `src/MusicEngine.App/Logging/FileLoggerProvider.cs` implementing `ILoggerProvider` and `IDisposable`: it writes to `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MusicEngine", "logs", $"app-{DateTime.Now:yyyy-MM-dd}.log")`, creating the directory if needed. Use a `System.Threading.Channels.Channel<string>` (unbounded) plus a single background consumer task that appends batched lines with `File.AppendAllLinesAsync`, so `Log` never blocks the caller. Format each line as `{timestamp:HH:mm:ss.fff} {level,-5} {category}: {message}` followed by the exception on subsequent lines when present. On startup, delete log files older than 7 days and roll the current file when it exceeds 5 MB (append `-2`, `-3`, …). Implement `IsEnabled` against a configurable minimum level. (2) In `src/MusicEngine.App/App.xaml.cs` line 53, change the logging setup to `services.AddLogging(b => { b.AddDebug(); b.AddProvider(new Logging.FileLoggerProvider()); b.SetMinimumLevel(LogLevel.Debug); });` and ensure the provider is disposed in `OnExit`. (3) In `src/MusicEngine.App/SettingsWindow.xaml`, add a small "Open logs folder" `Button` styled with `BtnGhost` near the bottom, and in the code-behind (or the new `SettingsViewModel` if MVVM-02 is done) handle it with `Process.Start(new ProcessStartInfo(logsDir) { UseShellExecute = true })`. Build and run the app, perform a search, and confirm the log file contains the provider messages from `SearchService`.

---

#### `FEAT-02: Cancellable, retryable download queue with persistence across restarts`

**Severity:** 🟡 Medium value / medium effort.

**Rationale:** `HttpDownloader` already implements everything needed — `.part` + `.state` sidecars with per-chunk offsets and URL-bound resume authority, verified by four tests. But `DownloadManager`'s queue lives only in memory, so closing the app discards the queue and the user must re-search and re-queue. The most valuable capability in the codebase is not exposed to the user.

**Recommended solution:**
Persist the queue (job id, `TrackWork`, chosen candidate URL, target path, phase) to `%APPDATA%\MusicEngine\queue.json` on every state transition (debounced, atomic — reuse the `PERF-01`/`BUG-09` helpers). On startup, offer to resume incomplete jobs; `HttpDownloader` will pick up from the existing `.part`. Pair with the `Retry` command from `MVVM-10`.

**Ready-to-use agent prompt:**
> Add download queue persistence. (1) Create `src/MusicEngine/Downloads/DownloadQueueStore.cs` with a `sealed record PersistedJob(string Id, string Title, string Artist, string? SourceUrl, ProviderId Provider, string TargetPath, DownloadPhase Phase, DateTimeOffset QueuedAt)` and a `DownloadQueueStore` class exposing `IReadOnlyList<PersistedJob> Load()` and `Task SaveAsync(IEnumerable<PersistedJob> jobs)` writing atomically (temp file + `File.Move(overwrite: true)`) to `%APPDATA%\MusicEngine\queue.json`, with a debounce identical to the one added in PERF-01. (2) In `src/MusicEngine/Downloads/DownloadManager.cs`, take an optional `DownloadQueueStore?` constructor parameter and call its debounced save whenever a job is enqueued, changes phase, completes, fails or is cancelled. Add `public IReadOnlyList<PersistedJob> LoadPendingJobs()` and `public Task ResumeAsync(IEnumerable<PersistedJob> jobs, CancellationToken ct)` that re-enqueues jobs whose phase is not `Completed`, reusing the existing resolve/download path so `HttpDownloader` resumes from any existing `.part` file. (3) In `src/MusicEngine.App/ViewModels/MainViewModel.cs`, on construction check for pending jobs and, if any exist, surface a status message plus a `ResumeQueueCommand` (do not auto-resume — the user may not want it). (4) Register `DownloadQueueStore` as a DI singleton in `App.xaml.cs`. Build the solution and run the offline tests; then manually verify that queueing two downloads, killing the app, restarting and resuming produces complete, correct files.

---

#### `FEAT-03: Local library index so IsInLibrary is accurate`

**Severity:** 🟡 Medium value / low effort.

**Rationale:** `TrackItemViewModel.IsInLibrary` is currently derived from `AppState.AlreadyOwned(title, artist)` (`MainViewModel.cs:348`) — a match against the recorded *download history*, not against the files actually on disk. Files deleted outside the app still show as owned; files added by other means never do. Since the app's core promise is "get this song into my library", the badge being wrong undermines the primary flow.

**Recommended solution:**
Scan `OutputDirectory` on startup (background, incremental) and build an index keyed by normalised `artist::title` using the existing `TrackTextNormalizer.MatchKeys`, reading tags with TagLib# (already a dependency, already used by `TrackTagger`). Watch the folder with `FileSystemWatcher` for live updates. Fall back to filename parsing when tags are absent.

**Ready-to-use agent prompt:**
> Add a real library index. Create `src/MusicEngine/Audio/LibraryIndex.cs`: a `sealed class LibraryIndex : IDisposable` taking `ISettings` (or `AppConfig`) and an `ILogger`. It exposes `Task BuildAsync(CancellationToken ct)` which enumerates `*.mp3;*.m4a;*.flac;*.opus` under `settings.OutputDirectory` (recursive, wrapped in try/catch per file), reads Artist and Title via `TagLib.File.Create`, falls back to parsing `"Artist - Title"` from the filename when tags are empty, and stores normalized keys in a `HashSet<string>` built with `MusicEngine.Text.TrackTextNormalizer.Normalize($"{artist} {title}")`. It exposes `bool Contains(string artist, string title)` using the same normalization plus `TrackTextNormalizer.KeysOverlap` as a secondary check, and `void Add(string filePath)` for newly downloaded files. Add a `FileSystemWatcher` on the output directory (Created/Deleted/Renamed) that incrementally updates the set, debounced by 1 second, and raises `event Action? Changed`. Register it as a DI singleton in `src/MusicEngine.App/App.xaml.cs`, kick off `BuildAsync` from the warm-up path (not before `window.Show()`), and in `src/MusicEngine.App/ViewModels/MainViewModel.cs` change the `IsInLibrary` assignment (around line 348) to use `LibraryIndex.Contains(...)` OR the existing `_state.AlreadyOwned(...)`, re-evaluating all visible rows when `Changed` fires. Build the solution and verify the badge appears for songs already present in the music folder.

---

#### `FEAT-04: Crash reporting with opt-in diagnostics bundle`

**Severity:** 🟡 Medium value / low effort.

**Rationale:** `CrashLog` (`src/MusicEngine.App/CrashLog.cs`) appends to a text file and the message box tells the user where it is (`App.xaml.cs:37`). That is a reasonable floor, but there is no environment context — .NET version, app version, proxy configuration state, which providers were offline, whether yt-dlp/ffmpeg/python resolved — which is exactly the information needed to interpret a report from this app's environment.

**Recommended solution:**
Extend `CrashLog` to prepend a one-time environment header per session, and add a "Copy diagnostics" button that assembles a redacted bundle (config with the proxy host masked, the last N log lines, resolved tool paths, offline sources, OS/.NET versions) to the clipboard. Keep it fully local and explicitly user-initiated — no network transmission.

**Ready-to-use agent prompt:**
> Enrich crash diagnostics. (1) In `src/MusicEngine.App/CrashLog.cs`, add `public static void WriteSessionHeader()` that appends a block containing app version (`Assembly.GetEntryAssembly()?.GetName().Version`), `RuntimeInformation.FrameworkDescription`, `RuntimeInformation.OSDescription`, and the current UTC time; call it once from `OnStartup` before the exception handlers are wired. Also change `Write` to include the exception's full `ToString()` including inner exceptions and stack traces. (2) Add `src/MusicEngine.App/Diagnostics/DiagnosticsBundle.cs` with `public static string Build(AppConfig cfg, ProviderRegistry registry, YtDlpProvider ytdlp, PersianIndexProvider persian)` returning a plain-text report containing: app/runtime/OS versions; whether a proxy is configured (report ONLY the scheme and port, never the host or credentials); `registry.OfflineSources`; the enabled source list; whether yt-dlp, ffmpeg and python resolved (booleans only, not full paths); output directory existence and free disk space; and the last 100 lines of today's log file. Never include the output directory path itself, filenames, search history, or any URL. (3) Add a "Copy diagnostics" button to `SettingsWindow.xaml` that puts `DiagnosticsBundle.Build(...)` on the clipboard and shows a confirmation toast. Do NOT add any network transmission. Build and run the app, click the button, and paste the result into your summary to confirm no sensitive data leaks.

---

#### `FEAT-05: Settings hot-reload without restart`

**Severity:** 🔵 Low value / medium effort.

**Rationale:** Several settings are captured at construction time and never re-read: `Reachability` gets `config.ProxyUrl` at `App.xaml.cs:57`, `SharedHttpClient` at `:58`, and the four proxy-aware providers at `:63-70`. Changing the proxy in Settings therefore has no effect on already-constructed HTTP clients until the app restarts — but the UI gives no indication of that, so a user fixing their proxy sees continued failures and concludes the app is broken.

**Recommended solution:**
Short term (recommended): detect which changed settings require a restart and tell the user plainly ("Proxy changes take effect after restart — restart now?"). Longer term: make `SharedHttpClient` and `Reachability` support `Reconfigure(string? proxyUrl)` that rebuilds handlers and clears the route cache, and raise an `ISettings.Changed` event that providers subscribe to.

**Ready-to-use agent prompt:**
> Handle proxy setting changes honestly. Implement the short-term fix first. In `src/MusicEngine.App/MainWindow.xaml.cs` `ShowSettings` (around lines 213-239), capture `cfg.ProxyUrl` before applying the dialog values and compare afterwards; if it changed, show a `MessageBox` with Yes/No: "Proxy changes take effect after a restart. Restart MusicEngine now?" and on Yes call `System.Diagnostics.Process.Start(Environment.ProcessPath!)` followed by `Application.Current.Shutdown()`. Do the same check for `CookiesBrowser`/`CookiesFile` (these ARE read per-download by `YtDlpProvider`, so verify by reading that code first and only prompt for settings that genuinely require a restart — report your findings). Then, as a follow-up, add `public void Reconfigure(string? proxyUrl)` to `src/MusicEngine/Network/Reachability.cs` that disposes and rebuilds `_direct`/`_proxied`, clears `_cache` and raises `RoutesChanged` — but do NOT wire it up yet; leave it unused with an XML comment explaining it is the foundation for hot-reload. Build the app.

---

#### `FEAT-06: Batch / playlist download from a pasted link or text list`

**Severity:** 🔵 Low value / high effort.

**Rationale:** `UrlQueryResolver` already resolves single Spotify/YouTube/SoundCloud links via oEmbed, `DownloadManager` already runs a bounded worker pool (1-8 workers), and `MainViewModel` already dedupes by song key. Multi-track input is a natural extension of machinery that exists: paste a playlist URL or a newline-separated list of "Artist - Title" lines and have each resolved and queued. The test harness's `fifty` mode already proves the pipeline handles batches of 50 queries.

**Recommended solution:**
Add a "Paste list" dialog accepting either a playlist URL (resolved to its track list) or free text (one query per line). Run each through the normal pipeline and auto-queue the top-ranked downloadable version, with a review step before committing so the user can uncheck wrong matches. Respect `MaxParallelDownloads` and surface aggregate progress.

**Ready-to-use agent prompt:**
> Add batch queueing from a text list — implement the text-list path only, not playlist URL expansion. (1) Create `src/MusicEngine.App/BatchWindow.xaml(.cs)` plus `src/MusicEngine.App/ViewModels/BatchViewModel.cs`: a dialog with a multi-line `TextBox` (one query per line) and a "Resolve" button. On resolve, run each non-empty line through `SearchService` sequentially with a per-query timeout of 20 seconds, and populate an `ObservableCollection<BatchItemViewModel>` with the top matched work per line, each showing the query, the matched title/artist, the source, and an `IsSelected` checkbox defaulting to true (false when nothing matched). Report progress as "Resolving 7 of 32…" and support cancellation. (2) Add a "Queue selected" button that enqueues each selected item through the existing `DownloadManager` path used by `MainViewModel.Download`, respecting the existing song-key dedup. (3) Add a "Paste list…" button to the `MainWindow.xaml` header that opens the dialog, and register the new types in DI. Reuse the existing `Btn`/`BtnAccent`/`Input`/`Switch` styles for visual consistency. Build the app and verify a 10-line list resolves and queues correctly.

---

## 3. Phased Implementation Plan

Each phase is independently shippable. **Build and run the offline tests after every finding**, not just at the end of a phase — several findings touch shared code paths. The ordering is deliberate: stability first (so later refactors are not built on shifting sand), then decoupling (so performance work has seams to act on), then performance, then modernization, then features.

### Phase 1 — Stability & Correctness (highest priority)

**Goal:** eliminate resource leaks, silent corruption paths, cancellation bugs and startup hangs. Nothing here changes visible behaviour except by making it correct.

| Order | ID | Title | Risk of change |
|:--:|---|---|---|
| 1 | `BUG-01` | Cancel abandoned fan-out provider tasks | Medium — touches the search hot path |
| 2 | `BUG-02` | Cancellation-based read watchdog | Medium — covered by 4 existing tests |
| 3 | `BUG-03` | Stop swallowing cancellation in Reachability | Low |
| 4 | `BUG-04` | Token-independent probe cache | Low |
| 5 | `BUG-09` | Atomic config/state writes | Low |
| 6 | `BUG-05` | Remove blocking startup probes | Low |
| 7 | `BUG-06` | Bounded, non-deadlocking shutdown | Low |
| 8 | `BUG-07` | PreviewPlayer generation guard | Low |
| 9 | `BUG-11` | Remove `stderrTask.Result` | Trivial |
| 10 | `BUG-13` | Scope TLS validation exemptions | **Medium-high** — may break scraping hosts; run live tests |
| 11 | `BUG-08` | Bound and LRU the caches | Low |
| 12 | `BUG-12` | Log inside silent catch blocks | Trivial |
| 13 | `BUG-10` | One config instance via DI | Low |
| 14 | `BUG-14` | Share HttpClient in UrlQueryResolver | Low |

**Exit criteria:** offline tests pass; `dotnet run -- live` shows no regression in provider reachability; app starts in under 1 second to visible window; exiting with active downloads terminates cleanly within 3 seconds; a cancelled search leaves no host marked `Dead`.

### Phase 2 — MVVM Decoupling

**Goal:** move behaviour out of code-behind and give the ViewModel testable seams. Do `MODERN-01` first — the settings abstraction unblocks testing everything else.

| Order | ID | Title |
|:--:|---|---|
| 1 | `MODERN-01` | `ISettings` / `ISettingsWriter` abstraction |
| 2 | `MODERN-02` | Convert the test harness to xUnit |
| 3 | `MVVM-03` | Replace `async void` handlers with commands |
| 4 | `MVVM-01` | Move view state from code-behind into bindings |
| 5 | `MVVM-05` | Fix missing `PropertyChanged` raises |
| 6 | `MVVM-04` | Incremental result merge instead of clear-and-refill |
| 7 | `MVVM-02` | `SettingsViewModel` + XAML accent template |
| 8 | `MVVM-06` | Extract collaborators from `MainViewModel` |
| 9 | `MVVM-07` | `DispatcherTimer` instead of threading timers |
| 10 | `MVVM-08` | Transient window, `TrayIconService`, split `OnStartup` |

**Exit criteria:** `MainWindow.xaml.cs` under ~80 lines with no `PropertyChanged` subscription and no `async void`; `SettingsWindow.xaml.cs` under ~30 lines; `MainViewModel` under ~250 lines; `dotnet test` green with the ported tests plus new tests for the extracted collaborators.

### Phase 3 — Performance & XAML Optimization

**Goal:** eliminate UI-thread I/O and per-row rendering cost. `MVVM-04` from Phase 2 is a prerequisite for `XAML-01` to be fully effective.

| Order | ID | Title |
|:--:|---|---|
| 1 | `PERF-01` | Debounced async persistence off the UI thread |
| 2 | `PERF-04` | Show the window before warm-up |
| 3 | `XAML-01` | Stop per-item entrance animation replaying |
| 4 | `XAML-02` | Remove per-row shadow, `VisualBrush` mask, forever-loop |
| 5 | `XAML-03` | Fix the `BoolToVis` key collision |
| 6 | `PERF-02` | Reverse-index the Finglish dictionary |
| 7 | `PERF-05` | Artwork cache |
| 8 | `PERF-06` | Cancellable artwork loads |
| 9 | `XAML-05` | Virtualize history, cap state lists |
| 10 | `XAML-04` | Toast layer hit-testing |
| 11 | `PERF-03` | Canonical cache keys + provider response cache |
| 12 | `XAML-06` | Flatten the download row template |
| 13 | `XAML-07` | Freeze palette brushes, guard accent references |
| 14 | `PERF-07` | Per-provider search status chips, resolve detail |
| 15 | `MVVM-10` | Empty states, retry, offline-aware messages |

**Exit criteria:** no synchronous file I/O on the dispatcher thread (verify by breakpointing `File.WriteAllText`); smooth scrolling on a 200-row result list; launch-to-window under 500 ms; repeated cross-script searches served from cache.

### Phase 4 — Modernization & Structure

**Goal:** reduce duplication and cognitive load now that behaviour is stable and covered by tests.

| Order | ID | Title |
|:--:|---|---|
| 1 | `MODERN-04` | Split `SearchResultCache` into its own file |
| 2 | `MODERN-06` | `ProviderCatalog` as the single source of truth |
| 3 | `MODERN-07` | Drop redundant `proxyUrl` parameters |
| 4 | `MODERN-03` | Extract `IGoalGate` from `SearchService` |
| 5 | `BUG-15` | `[GeneratedRegex]` for remaining inline regexes |
| 6 | `MODERN-05` | `FrozenDictionary`, primary constructors, formatting |
| 7 | `MODERN-08` | Remove dead code, gitignore artifacts |
| 8 | `MVVM-09` | Converters return `Brush`, not strings |

**Exit criteria:** adding a hypothetical tenth provider requires edits in `ProviderId`, `ProviderCatalog` and one provider class only — verified by writing the provider and observing it appear in Settings without further changes; no file over 400 lines in `src/MusicEngine/Search`.

### Phase 5 — Features & Observability

**Goal:** capabilities the architecture already almost supports. `FEAT-01` first — it makes every subsequent phase easier to debug and should arguably be pulled forward into Phase 1 if diagnosis is currently painful.

| Order | ID | Title |
|:--:|---|---|
| 1 | `FEAT-01` | Rolling file logging + open-logs action |
| 2 | `FEAT-04` | Enriched crash diagnostics bundle |
| 3 | `FEAT-03` | Real library index from disk |
| 4 | `FEAT-02` | Persistent, resumable download queue |
| 5 | `FEAT-05` | Restart prompt for proxy changes |
| 6 | `FEAT-06` | Batch queue from a pasted list |

**Exit criteria:** a user-reported problem can be diagnosed from `%APPDATA%\MusicEngine\logs` without a debugger; the library badge reflects files on disk; queue survives a restart.

### Dependency notes

- `MODERN-01` (ISettings) should precede `MODERN-02` (xUnit) — testing is much easier with the abstraction in place.
- `MVVM-04` (incremental merge) should precede `XAML-01` (animation) — otherwise the animation still replays on every batch.
- `BUG-09` (atomic writes) should precede `PERF-01` (async writes) — the atomic helper is reused.
- `MODERN-06` (ProviderCatalog) should precede `MVVM-02` (SettingsViewModel) — the VM binds to the catalog.
- `BUG-13` (TLS) is the only item that can plausibly break working functionality; schedule it when live testing is possible.

---

## 4. Master Orchestration Prompt

Use the prompt below verbatim as the top-level instruction to Claude Code for executing this roadmap phase by phase. It embeds the audit's constraints, the verification gate, and the full finding list so the executing agent never needs to re-derive the codebase from scratch.

```text
You are executing a pre-audited improvement roadmap for the MusicEngine WPF codebase at D:\MusicEngine\v2.

HARD CONSTRAINTS (applies to every phase):
1. Work in order: Phase 1 → Phase 2 → Phase 3 → Phase 4 → Phase 5. Do not skip ahead.
2. Within a phase, implement each finding in the order listed below. Implement them ONE AT A TIME.
3. After EVERY finding: run `D:\dotnet-sdk\dotnet.exe build MusicEngine.sln -c Release`, then
   `cd tests\MusicEngine.Tests && D:\dotnet-sdk\dotnet.exe run` (offline tests). Both must pass
   before moving to the next finding. If the offline tests fail, fix your change (the tests were
   verified green before you started) and re-run.
4. Do not modify files you are not instructed to modify. Do not reformat unrelated code.
5. Do not add new NuGet dependencies unless the finding explicitly says a dependency is acceptable.
6. Never change public API shape unless a finding explicitly requires it; when it does, update
   every call site in the same change.
7. Some findings reference source you may want to re-read first. Re-read the target file before
   editing it; positions given are approximate line numbers from the audit date and may drift.
8. Keep a running list of what you changed. Report at the end of each phase: files touched,
   tests run, and any deviation from the finding text.

The roadmap document to consult for full detail (severity, root cause, recommended solution) is
WPF_CODEBASE_AUDIT_ROADMAP.md in the repo root. Each finding below is one entry in that document's
Section 2. The recommended-solution and ready-to-use-agent-prompt blocks for each finding are the
authoritative instruction set — prefer them over these one-line summaries.

== BEHAVIOUR: DO NOT START THE WORK ON YOUR OWN. ==
At the very beginning of your run, present the user with: (1) the phase you're about to start,
(2) the count of findings in that phase, and (3) the first finding's title. Then PAUSE and wait
for the user to type "go" (or an explicit override such as "skip to finding X in phase Y"). After
each phase completes, present the same summary for the next phase and pause again.

== PHASE 1 — Stability & Correctness ==
Implement in order:
BUG-01  Cancel abandoned fan-out provider tasks in SearchService.CollectAsync; guard onBatch with a
        closed flag; dispose the linked CTS via a continuation, never a using.
BUG-02  Replace the Task.WhenAny-based ReadWithWatchdogAsync in HttpDownloader with a
        CancelAfter-based watchdog so no orphaned ReadAsync can reuse the buffer.
BUG-03  Stop swallowing caller cancellation in Reachability.HttpAliveAsync (rethow OCE when the
        caller's token fired); make ProbeUncachedAsync propagate cancellation instead of Dead.
BUG-04  Make cached probes token-independent: start probes with CancellationToken.None, add a
        cleanup continuation; apply caller cancellation at the await site in RoutingHandler.
BUG-09  Make AppConfig.Save() and AppState.Save() atomic via a temp file + File.Move(overwrite:true).
BUG-05  Remove blocking startup probes: lazy+async availability for PersianIndexProvider (3s probe),
        PATH-scan instead of `where` in YtDlpProvider.ResolveBinary, fire-and-forget warm-up in
        App.xaml.cs after window.Show().
BUG-06  Bound the OnExit drain to 3s with WaitAsync; ensure tray disposal and sp.Dispose() always run.
BUG-07  Add a generation guard to PreviewPlayer so a stale MediaPlayer callback cannot stop current
        playback; surface MediaFailed via a Failed event.
BUG-11  Replace stderrTask.Result with await stderrTask.ConfigureAwait(false) in PersianIndexProvider.
BUG-13  Scope the TLS validation bypass to an explicit allow-list of non-API scraping hosts; allow
        only RemoteCertificateChainErrors for those hosts; keep APIs strict. Run live tests and
        report which hosts (if any) regress so the list can be tuned.
BUG-08  Make SearchResultCache a true LRU (LastAccessTicks, single allocation-free eviction pass);
        cap FinglishConverter.PhraseCache at 4096 with a Clear() guard.
BUG-12  Add ILogger (NullLogger default) to silent catch blocks in ArtworkLoader, TrackTagger,
        Reachability; replace Debug.WriteLine diagnostics with logger calls.
BUG-10  Make the DI AppConfig the single config instance used by SettingsWindow and Window_Closing.
BUG-14  Accept SharedHttpClient in UrlQueryResolver instead of constructing a new client per call.
Phase-1 exit check: offline tests green; `dotnet run -- live` shows no provider regressions;
launch-to-window under 1s; clean 3s exit.

== PHASE 2 — MVVM Decoupling ==
Implement in order:
MODERN-01  Add ISettings + ISettingsWriter implemented by AppConfig; providers take ISettings.
MODERN-02  Convert the tests project to xUnit (preserve every assertion; port LocalRangeServer and
           the four HttpDownloader tests); move debug subcommands to tools/MusicEngine.DevTools.
MVVM-03    Replace async void handlers with RelayCommands wired in XAML; VM catches and reports errors.
MVVM-01    Move view-state handling from MainWindow code-behind into VM properties + XAML bindings;
           delete VmOnPropertyChanged and the stringly-typed sort switch.
MVVM-05    Route all settable TrackItemViewModel/DownloadItemViewModel properties through Set<> and
           raise computed-property changes.
MVVM-04    Replace clear-and-refill in ApplyResults with a keyed incremental merge (result index),
           preserving selection and scroll position.
MVVM-02    Introduce SettingsViewModel; bind the settings window; replace FrameworkElementFactory
           accent templates with XAML DataTemplate + DataTrigger.
MVVM-06    Extract ClipboardWatcher, ToastService, PlaybackViewModel, DownloadQueueViewModel into
           separate files; bind MainWindow to the new nested paths. Build after EACH extraction.
MVVM-07    Replace the three System.Threading.Timers with DispatcherTimer.
MVVM-08    Transient MainWindow; extract TrayIconService; split OnStartup into focused methods; fix
           line-77 double statement and indentation at lines 90-102.
Phase-2 exit check: MainWindow.xaml.cs < 80 lines, no async void, no PropertyChanged subscription;
SettingsWindow.xaml.cs < 30 lines; MainViewModel < 250 lines; `dotnet test` green.

== PHASE 3 — Performance & XAML Optimization ==
Implement in order:
PERF-01  Debounced async persistence in AppState (mutate in memory, flush in background with
         File.WriteAllTextAsync + atomic rename); Task.Run the AppConfig.Save in the VM; flush
         synchronously in OnExit.
PERF-04  Show the window before warm-up work; lazy AppState resolution; BeginWarmup after Show().
XAML-01  Remove the per-item Loaded-entrance storyboard from the virtualized lists; animate the list
         once on first results.
XAML-02  Remove per-row DropShadowEffect, VisualBrush OpacityMask, and unbounded EqLoop animation;
         use border/shadow substitute and the playing-only DataTrigger.
XAML-03  Rename converters to BoolToVisibility / InvertedBoolToVisibility, declare once in App.xaml,
         delete window-level duplicates, update all references.
PERF-02  Add reverse indexes (FrozenSet/HashSet of values + reverse dictionary) to
         FinglishConverter; O(1) ScoreAlternatives and FindLatinForPersian.
PERF-05  Cache byte[] per artwork URL in ArtworkLoader (dict, 256 cap); second-level decoded-image
         cache in MainViewModel.
PERF-06  Thread a CancellationToken through IArtworkLoader.LoadAsync and the VM artwork path; check
         before assigning.
XAML-05  Virtualize the history list; cap AppState.History at 1000 and RecentSearches at 50.
XAML-04  IsHitTestVisible=False on the toast container; True only on the toast item Border.
PERF-03  Canonical cache key from the query expansion set; add ProviderResponseCache (45s TTL, 256
         cap) consulted in CollectAsync.
XAML-06  Flatten the download row template to one Grid; remove single-child panels.
XAML-07  po:Freeze the static palette brushes; convert stray StaticResource Accent to DynamicResource;
         add a comment enforcing the convention.
PERF-07  Name the provider in DownloadProgress.Message during Resolving; add per-provider status
         chips during search.
MVVM-10  Add empty states for downloads/history, a Retry command for failed rows, and offline-aware
         zero-result messaging.
Phase-3 exit check: no synchronous file I/O on the dispatcher; smooth scrolling on 200 rows;
launch-to-window < 500ms; cross-script repeated searches hit the cache.

== PHASE 4 — Modernization & Structure ==
Implement in order:
MODERN-04  Move SearchResultCache to Search/SearchResultCache.cs (no logic change).
MODERN-06  Add ProviderCatalog as the single source of truth for hosts and download rank; rewrite
           ProviderHosts and DownloadRank as lookups; add the enum-coverage test.
MODERN-07  Drop the redundant proxyUrl constructor parameter from the four proxy-aware providers;
           read it from SharedHttpClient.ProxyUrl; update app + test construction sites.
MODERN-03  Extract IGoalGate from SearchService's static gate methods; keep static wrappers working
           for the test harness.
BUG-15     Convert remaining inline Regex.Match calls to [GeneratedRegex] partial properties.
MODERN-05  FrozenDictionary for FinglishConverter tables, FrozenSet for JunkFilter arrays; primary
           constructors for the three simple providers; collection expressions in ProviderHosts;
           ArgumentNullException.ThrowIfNull guards; fix formatting drift.
MODERN-08  Delete the never-assigned Reachability.ProxyUrl (after grepping for consumers); log the
           invalid-proxy else branch; gitignore fifty-results*; report which root analysis docs are
           stale (do NOT delete them).
MVVM-09    BoolToRedConverter returns frozen Brushes from the theme, not hex strings.
Phase-4 exit check: adding a tenth provider requires ProviderId + ProviderCatalog + the provider
class only; no file in src/MusicEngine/Search over 400 lines; offline tests green.

== PHASE 5 — Features & Observability ==
Implement in order:
FEAT-01  Rolling file logger to %APPDATA%\MusicEngine\logs (channel-buffered writer, 7-day retention,
         5MB roll); AddProvider in App.xaml.cs; open-logs button in Settings.
FEAT-04  CrashLog session header + diagnostics bundle (redacted, local-only) with a copy button.
FEAT-03  LibraryIndex scanning OutputDirectory by tags + filename fallback, FileSystemWatcher
         increments, wired into IsInLibrary.
FEAT-02  DownloadQueueStore persisting the queue; DownloadManager resume path; startup resume prompt.
FEAT-05  Restart prompt when ProxyUrl changes in Settings; add (unused) Reachability.Reconfigure.
FEAT-06  Batch queue dialog for a pasted "Artist - Title" list; auto-queue top matches with review.
Phase-5 exit check: field diagnosis possible from logs alone; library badge reflects disk state;
queue survives restart.

FINAL REPORT FORMAT (after the last phase):
- Per-phase summary: findings implemented / skipped, files touched, tests run/result.
- Any deviation from a finding's recommended solution, with the reason.
- The top-3 highest-priority risks from Section 1.4, restated as either "fixed with <finding ID>"
  or "still open".
```

---

*Generated by a read-only static audit of `D:\MusicEngine\v2`. No application source file was modified in producing this document. All line numbers are as of 2026-08-18 and should be re-verified before editing.*




















