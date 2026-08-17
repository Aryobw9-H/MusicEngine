<System_Directives>
You are an expert reasoning and coding model.
1. If the request is ambiguous, lacks crucial information, or requires more file context, DO NOT GUESS. Output direct, clarifying questions.
2. If you have enough context, output ONLY the code using strict SEARCH/REPLACE blocks.
3. Omit all conversational text, pleasantries, and step-by-step explanations.
</System_Directives>

<Environment>
.NET 8 WPF application, two-project architecture:
- MusicEngine (net8.0) — UI-agnostic core: downloads, providers, search, models
- MusicEngine.App (net8.0-windows) — WPF UI: MainViewModel, TrackItemViewModel, DownloadItemViewModel, MainWindow.xaml

Key types:
- DownloadPhase enum: Queued, Resolving, Downloading, Tagging, Completed, Failed, Cancelled, AlreadyOwned, Paused
- DownloadProgress: Phase, Percent, BytesDone, BytesTotal, Message, FilePath
- DownloadManager: orchestrates download jobs via worker pool, fallback provider chain
- IDownloadProvider: interface with DownloadAsync(SearchResult, DownloadOptions, IProgress<DownloadProgress>, CancellationToken)
- HttpDownloader: static class with stateful segmented download (8 chunks, .state JSON persistence, resume support)
- 6 providers: PersianIndex (python), YtDlp (yt-dlp CLI), SoundCloud, RadioJavan, Nex1Music, PersianSites (all use HttpDownloader or external tools)
- UI: MainViewModel.DownloadQueue (ObservableCollection<DownloadItemViewModel>), XAML binds Pause/Resume buttons to IsActive/IsPaused
</Environment>

<Hermes_Analysis>
ROOT CAUSES (from deep analysis):

1. **Pause/Resume is broken at orchestration layer**: DownloadManager.Pause() cancels token + sets flag. Resume() creates NEW TaskCompletionSource, NEW CancellationTokenSource, re-queues job. RunJobAsync runs from scratch (re-resolves candidates, re-starts provider chain). Only HttpDownloader single-stream path accidentally resumes via .part file detection. Segmented downloads now persist .state JSON but NO provider/manager code reads it on resume.

2. **Cancel fails on paused jobs**: Cancel() checks _active dict, but paused jobs are removed from _active in worker finally block. Returns false silently.

3. **UI never shows Paused state**: MainViewModel.JobProgress handler only handles Completed/AlreadyOwned/Failed/Cancelled. Paused phase ignored → Pause/Resume buttons don't toggle.

4. **_queuedWorks blocks retry**: Download() uses DedupKey to prevent duplicates. On pause/fail, key never cleared → user can't re-download.

5. **Completed jobs leak in _jobs**: RunJobAsync returns success but never _jobs.TryRemove(job.Id).

6. **No provider resume interface**: IDownloadProvider.DownloadAsync signature lacks resume context (provider, candidate, offset). Adding it requires changing all 6 providers.

ARCHITECTURE DECISION: 
- Full pause/resume requires: job state persistence (SQLite/JSON), provider interface change, manager-level resume orchestration — 2-3 days work.
- QUICK WIN: Fix P0 issues (cancel, UI, leaks, retry blocking) + add "Restart Download" button (clears state, re-queues fresh) — works reliably, user-visible, no interface changes.
- HttpDownloader already has stateful segmented resume (8 chunks, .state persistence) — ready when manager supports it.
</Hermes_Analysis>

<Objective>
Apply minimal, high-impact fixes to make downloads reliable TODAY:
1. Fix Cancel to work on paused jobs (check _paused dict)
2. Fix UI to handle Paused phase (toggle Pause/Resume buttons)
3. Clear _queuedWorks on Failed/Cancelled/Paused so user can retry
4. Remove completed jobs from _jobs dict (fix leak)
5. Add "Restart Download" command/button (clears state, re-enqueues fresh)
6. Fix ProgressProxy throttle for Downloading phase (120ms drops 83% of segmented updates)
7. Fix watchdog task leak (await or dispose properly)

Do NOT change provider interfaces or add persistence layer. These are quick wins on existing architecture.
</Objective>

<Code_State>
[src/MusicEngine/Downloads/DownloadManager.cs]
<<<<<<< SEARCH
        public bool Cancel(string jobId)
        {
            _jobs.TryRemove(jobId, out _);
            _paused.TryRemove(jobId, out _);
            return _active.TryGetValue(jobId, out var cts) && CancelCore(cts);
        }
=======
        public bool Cancel(string jobId)
        {
            _jobs.TryRemove(jobId, out _);
            _paused.TryRemove(jobId, out _);
            if (_active.TryGetValue(jobId, out var cts))
                return CancelCore(cts);
            // Job might be paused (not in _active) — still report cancelled to UI
            if (_paused.TryRemove(jobId, out _))
            {
                JobProgress?.Invoke(jobId, new DownloadProgress(DownloadPhase.Cancelled, Message: "Cancelled"));
                return true;
            }
            return false;
        }
>>>>>>> REPLACE

[src/MusicEngine/Downloads/DownloadManager.cs]
<<<<<<< SEARCH
            catch (OperationCanceledException)
            {
                if (_paused.TryRemove(job.Id, out _))
                {
                    tcs.TrySetResult(new DownloadProgress(DownloadPhase.Paused, Message: "Paused"));
                }
                else
                {
                    _jobs.TryRemove(job.Id, out _);
                    tcs.TrySetResult(new DownloadProgress(DownloadPhase.Cancelled, Message: "Cancelled"));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Download job failed: {Title} — {Msg}", job.Title, ex.Message);
                _jobs.TryRemove(job.Id, out _);
                tcs.TrySetResult(new DownloadProgress(DownloadPhase.Failed, Message: ex.Message));
            }
            finally
            {
                _active.TryRemove(job.Id, out _);
                cts.Dispose();
            }
=======
            catch (OperationCanceledException)
            {
                if (_paused.TryRemove(job.Id, out _))
                {
                    tcs.TrySetResult(new DownloadProgress(DownloadPhase.Paused, Message: "Paused"));
                }
                else
                {
                    _jobs.TryRemove(job.Id, out _);
                    tcs.TrySetResult(new DownloadProgress(DownloadPhase.Cancelled, Message: "Cancelled"));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Download job failed: {Title} — {Msg}", job.Title, ex.Message);
                _jobs.TryRemove(job.Id, out _);
                tcs.TrySetResult(new DownloadProgress(DownloadPhase.Failed, Message: ex.Message));
            }
            finally
            {
                _active.TryRemove(job.Id, out _);
                cts.Dispose();
                // Clean up completed jobs from _jobs to prevent leak
                if (!_paused.ContainsKey(job.Id) && !_active.ContainsKey(job.Id))
                    _jobs.TryRemove(job.Id, out _);
            }
>>>>>>> REPLACE

[src/MusicEngine/Downloads/DownloadManager.cs]
<<<<<<< SEARCH
        public void Report(DownloadProgress value)
        {
            _last = value;
            _lastProgress = DateTime.UtcNow;
            if (value.Phase is DownloadPhase.Failed or DownloadPhase.Cancelled or DownloadPhase.Completed
                or DownloadPhase.AlreadyOwned or DownloadPhase.Paused)
            {
                _event?.Invoke(_jobId, value);
                return;
            }
            if ((DateTime.UtcNow - _lastEmit).TotalMilliseconds >= 120)
            {
                _lastEmit = DateTime.UtcNow;
                _event?.Invoke(_jobId, value);
            }
        }
=======
        public void Report(DownloadProgress value)
        {
            _last = value;
            _lastProgress = DateTime.UtcNow;
            if (value.Phase is DownloadPhase.Failed or DownloadPhase.Cancelled or DownloadPhase.Completed
                or DownloadPhase.AlreadyOwned or DownloadPhase.Paused)
            {
                _event?.Invoke(_jobId, value);
                return;
            }
            // Faster throttle during active downloading (50ms) to capture segmented progress
            var throttleMs = value.Phase == DownloadPhase.Downloading ? 50 : 120;
            if ((DateTime.UtcNow - _lastEmit).TotalMilliseconds >= throttleMs)
            {
                _lastEmit = DateTime.UtcNow;
                _event?.Invoke(_jobId, value);
            }
        }
>>>>>>> REPLACE

[src/MusicEngine/Downloads/DownloadManager.cs]
<<<<<<< SEARCH
                                                var watchdog = Task.Run(async () =>
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
=======
                                                var watchdog = Task.Run(async () =>
                                                {
                                                    try
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
                                                    }
                                                    catch (OperationCanceledException) { /* expected on cancel */ }
                                                    finally
                                                    {
                                                        watchdogCts.Dispose();
                                                    }
                                                });
>>>>>>> REPLACE

[src/MusicEngine/App/ViewModels/MainViewModel.cs]
<<<<<<< SEARCH
                _downloads.JobProgress += (id, p) => _ui.Run(() =>
                {
                    var item = DownloadQueue.FirstOrDefault(d => d.JobId == id);
                    if (item is null) return;
                    item.Apply(p, _jobProvider.TryGetValue(id, out var prov) ? prov : "");
                    if (p.Phase is DownloadPhase.Completed or DownloadPhase.AlreadyOwned
                        or DownloadPhase.Failed or DownloadPhase.Cancelled)
                    {
                        OnPropertyChanged(nameof(ActiveDownloads));
                        if (p.Phase == DownloadPhase.Completed)
                        {
                            if (_jobIdentity.TryGetValue(id, out var identity) && p.FilePath is { Length: > 0 })
                                RecordHistory(identity.Title, identity.Artist, p.FilePath,
                                    _jobProvider.TryGetValue(id, out var provName) ? provName : "MusicEngine");
                            if (_config.DownloadToasts)
                                PushToast(new ToastViewModel { Title = "Download complete", Message = item.Title, FilePath = p.FilePath });
                        }
                        else if (p.Phase is DownloadPhase.Failed or DownloadPhase.Cancelled)
                        {
                            // Allow retry: remove from queued works so user can re-download
                            if (_jobIdentity.TryGetValue(id, out var failedIdentity))
                            {
                                var key = $"{failedIdentity.Title}|{failedIdentity.Artist}".Trim();
                                _queuedWorks.Remove(key);
                            }
                            if (p.Phase == DownloadPhase.Failed && _config.DownloadToasts)
                                PushToast(new ToastViewModel { Title = "Download failed", Message = item.Title, IsError = true });
                        }
                    }
                });
=======
                _downloads.JobProgress += (id, p) => _ui.Run(() =>
                {
                    var item = DownloadQueue.FirstOrDefault(d => d.JobId == id);
                    if (item is null) return;
                    item.Apply(p, _jobProvider.TryGetValue(id, out var prov) ? prov : "");
                    if (p.Phase is DownloadPhase.Completed or DownloadPhase.AlreadyOwned
                        or DownloadPhase.Failed or DownloadPhase.Cancelled or DownloadPhase.Paused)
                    {
                        OnPropertyChanged(nameof(ActiveDownloads));
                        if (p.Phase == DownloadPhase.Completed)
                        {
                            if (_jobIdentity.TryGetValue(id, out var identity) && p.FilePath is { Length: > 0 })
                                RecordHistory(identity.Title, identity.Artist, p.FilePath,
                                    _jobProvider.TryGetValue(id, out var provName) ? provName : "MusicEngine");
                            if (_config.DownloadToasts)
                                PushToast(new ToastViewModel { Title = "Download complete", Message = item.Title, FilePath = p.FilePath });
                        }
                        else if (p.Phase is DownloadPhase.Failed or DownloadPhase.Cancelled or DownloadPhase.Paused)
                        {
                            // Allow retry: remove from queued works so user can re-download
                            if (_jobIdentity.TryGetValue(id, out var failedIdentity))
                            {
                                var key = $"{failedIdentity.Title}|{failedIdentity.Artist}".Trim();
                                _queuedWorks.Remove(key);
                            }
                            if (p.Phase == DownloadPhase.Failed && _config.DownloadToasts)
                                PushToast(new ToastViewModel { Title = "Download failed", Message = item.Title, IsError = true });
                        }
                    }
                });
>>>>>>> REPLACE

[src/MusicEngine/App/ViewModels/MainViewModel.cs]
<<<<<<< SEARCH
        public RelayCommand DownloadSelectedCommand { get; }
        public RelayCommand DownloadAllCommand { get; }
        public RelayCommand PauseDownloadCommand { get; }
        public RelayCommand ResumeDownloadCommand { get; }
        public RelayCommand OpenSettingsCommand { get; }
        public RelayCommand OpenFolderCommand { get; }
        public RelayCommand ClearFinishedCommand { get; }
        public RelayCommand ClearHistoryCommand { get; }
        public RelayCommand ShowDownloadsCommand { get; }
        public RelayCommand ShowHistoryCommand { get; }
        public RelayCommand StopPreviewCommand { get; }
        public RelayCommand SearchClipboardCommand { get; }
        public RelayCommand DismissClipboardCommand { get; }
=======
        public RelayCommand DownloadSelectedCommand { get; }
        public RelayCommand DownloadAllCommand { get; }
        public RelayCommand PauseDownloadCommand { get; }
        public RelayCommand ResumeDownloadCommand { get; }
        public RelayCommand RestartDownloadCommand { get; }
        public RelayCommand OpenSettingsCommand { get; }
        public RelayCommand OpenFolderCommand { get; }
        public RelayCommand ClearFinishedCommand { get; }
        public RelayCommand ClearHistoryCommand { get; }
        public RelayCommand ShowDownloadsCommand { get; }
        public RelayCommand ShowHistoryCommand { get; }
        public RelayCommand StopPreviewCommand { get; }
        public RelayCommand SearchClipboardCommand { get; }
        public RelayCommand DismissClipboardCommand { get; }
>>>>>>> REPLACE

[src/MusicEngine/App/ViewModels/MainViewModel.cs]
<<<<<<< SEARCH
        DownloadSelectedCommand = new RelayCommand(_ => { if (ResultsView.FirstOrDefault() is { } t) Download(t); });
        DownloadAllCommand = new RelayCommand(_ => DownloadAll(), _ => ResultsView.Count > 0);
        PauseDownloadCommand = new RelayCommand(p => { if (p is DownloadItemViewModel d) _downloads.Pause(d.JobId); });
        ResumeDownloadCommand = new RelayCommand(p => { if (p is DownloadItemViewModel d) _downloads.Resume(d.JobId); });
        OpenSettingsCommand = new RelayCommand(_ => SettingsRequested?.Invoke());
=======
        DownloadSelectedCommand = new RelayCommand(_ => { if (ResultsView.FirstOrDefault() is { } t) Download(t); });
        DownloadAllCommand = new RelayCommand(_ => DownloadAll(), _ => ResultsView.Count > 0);
        PauseDownloadCommand = new RelayCommand(p => { if (p is DownloadItemViewModel d) _downloads.Pause(d.JobId); });
        ResumeDownloadCommand = new RelayCommand(p => { if (p is DownloadItemViewModel d) _downloads.Resume(d.JobId); });
        RestartDownloadCommand = new RelayCommand(p => 
        {
            if (p is DownloadItemViewModel d)
            {
                _downloads.Cancel(d.JobId);
                var key = $"{d.Title.Split(" — ")[1]}|{d.Title.Split(" — ")[0]}".Trim();
                _queuedWorks.Remove(key);
                if (DownloadQueue.FirstOrDefault(x => x.JobId == d.JobId) is { } item)
                {
                    var work = Results.FirstOrDefault(r => r.Title == item.Title && r.Artist == item.Title.Split(" — ")[1])?.Work;
                    if (work != null) _ = _downloads.EnqueueAsync(work);
                }
            }
        });
        OpenSettingsCommand = new RelayCommand(_ => SettingsRequested?.Invoke());
>>>>>>> REPLACE

[src/MusicEngine/App/MainWindow.xaml]
<<<<<<< SEARCH
                                    <Button Content="⏸" Style="{StaticResource BtnGhost}" FontSize="11.5"
                                            Command="{Binding DataContext.PauseDownloadCommand, RelativeSource={RelativeSource AncestorType=Window}}"
                                            CommandParameter="{Binding}" Margin="0,0,4,0" ToolTip="Pause"
                                            Visibility="{Binding IsActive, Converter={StaticResource BoolToVis}}"/>
                                    <Button Content="▶" Style="{StaticResource BtnGhost}" FontSize="11.5"
                                            Command="{Binding DataContext.ResumeDownloadCommand, RelativeSource={RelativeSource AncestorType=Window}}"
                                            CommandParameter="{Binding}" Margin="0,0,4,0" ToolTip="Resume"
                                            Visibility="{Binding IsPaused, Converter={StaticResource BoolToVis}}"/>
                                    <Button Content="✕" Style="{StaticResource BtnGhost}" FontSize="11.5"
                                            Click="CancelDownload_Click" ToolTip="Cancel / remove"/>
=======
                                    <Button Content="⏸" Style="{StaticResource BtnGhost}" FontSize="11.5"
                                            Command="{Binding DataContext.PauseDownloadCommand, RelativeSource={RelativeSource AncestorType=Window}}"
                                            CommandParameter="{Binding}" Margin="0,0,4,0" ToolTip="Pause"
                                            Visibility="{Binding IsActive, Converter={StaticResource BoolToVis}}"/>
                                    <Button Content="▶" Style="{StaticResource BtnGhost}" FontSize="11.5"
                                            Command="{Binding DataContext.ResumeDownloadCommand, RelativeSource={RelativeSource AncestorType=Window}}"
                                            CommandParameter="{Binding}" Margin="0,0,4,0" ToolTip="Resume"
                                            Visibility="{Binding IsPaused, Converter={StaticResource BoolToVis}}"/>
                                    <Button Content="↻" Style="{StaticResource BtnGhost}" FontSize="11.5"
                                            Command="{Binding DataContext.RestartDownloadCommand, RelativeSource={RelativeSource AncestorType=Window}}"
                                            CommandParameter="{Binding}" Margin="0,0,4,0" ToolTip="Restart download"
                                            Visibility="{Binding IsFailed, Converter={StaticResource BoolToVis}}"/>
                                    <Button Content="✕" Style="{StaticResource BtnGhost}" FontSize="11.5"
                                            Click="CancelDownload_Click" ToolTip="Cancel / remove"/>
>>>>>>> REPLACE
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