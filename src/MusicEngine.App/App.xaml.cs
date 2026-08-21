namespace MusicEngine.App;

using System.IO;
using System.Windows;
using Configuration;
using Downloads;
using Audio;
using Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Providers;
using Search;
using ViewModels;
using MusicEngine.App.Ui;

/// <summary>
/// Composition root: config → state → http → providers → pipeline → download
/// manager → UI. Providers are registered once as singletons; ProviderRegistry
/// hands the enabled subsets to per-search SearchService instances.
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _services;
    private TrayIconService? _trayService;
    private static Logging.FileLoggerProvider? _fileLog;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        CrashLog.WriteSessionHeader();
        WireCrashHandlers();

        // Config is small and needed for theming, so it loads synchronously.
        // State (uncapped history JSON) and the provider graph are deferred:
        // AppState resolves lazily via DI and warm-up runs after Show (PERF-04).
        var config = AppConfig.Load();
        Directory.CreateDirectory(config.OutputDirectory);

        var services = BuildServices(config, Shutdown);
        _services = services;

        AccentTheme.Apply(config.Accent);

        var window = services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();

        StartShell(services, config, window);
    }

    private void WireCrashHandlers()
    {
        // Crash safety net: log unhandled exceptions instead of dying —
        // a UI-thread error in one view must not take the whole app down.
        DispatcherUnhandledException += (_, ex) =>
        {
            ex.Handled = true;
            CrashLog.Write("UI", ex.Exception);
            MessageBox.Show($"Something went wrong but the app is still running:\n\n{ex.Exception.Message}\n\nDetails: {CrashLog.Path}",
                "MusicEngine", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            CrashLog.Write("fatal", ex.ExceptionObject as Exception ?? new Exception(ex.ExceptionObject.ToString()));
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            ex.SetObserved();
            CrashLog.Write("task", ex.Exception);
        };
    }

    private static ServiceProvider BuildServices(AppConfig config, Action shutdown)
    {
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            b.AddDebug();
            // FEAT-01: file sink so Release builds are diagnosable without a debugger.
            _fileLog = new Logging.FileLoggerProvider();
            b.AddProvider(_fileLog);
            b.SetMinimumLevel(LogLevel.Debug);
        });

        services.AddSingleton(config);
        services.AddSingleton<Configuration.ISettings>(sp => sp.GetRequiredService<AppConfig>());
        services.AddSingleton(_ => AppState.Load()); // lazy: deferred past window.Show() (PERF-04)
        services.AddSingleton<Network.Reachability>(sp => new Network.Reachability(
            config.ProxyUrl, sp.GetRequiredService<ILogger<Network.Reachability>>()));
        services.AddSingleton<SharedHttpClient>(sp => new SharedHttpClient(
            config.ProxyUrl, sp.GetRequiredService<Network.Reachability>()));

        // Providers — singletons, one ProviderId each.
        services.AddSingleton<ITunesProvider>();
        services.AddSingleton<DeezerProvider>();
        services.AddSingleton<YouTubeProvider>();
        services.AddSingleton<SoundCloudProvider>();
        services.AddSingleton<RadioJavanProvider>();
        services.AddSingleton<Nex1MusicProvider>();
        services.AddSingleton<PersianSitesProvider>();
        services.AddSingleton<PersianIndexProvider>(sp => new PersianIndexProvider(
            sp.GetRequiredService<AppConfig>()));
        services.AddSingleton<YtDlpProvider>();
        services.AddSingleton<RozMusicProvider>();
        services.AddSingleton<MusicDelProvider>();
        services.AddSingleton<BehMelodyProvider>();
        services.AddSingleton<Melody98Provider>();
        services.AddSingleton<AparatProvider>(sp => new AparatProvider(
            sp.GetRequiredService<SharedHttpClient>(),
            sp.GetRequiredService<AppConfig>()));
        services.AddSingleton<BiaMusicProvider>();
        services.AddSingleton<BeatMasteringProvider>();
        services.AddSingleton<MusicsFaProvider>();

        services.AddSingleton<ProviderRegistry>();
        services.AddSingleton<TrackTagger>();
        services.AddSingleton<Audio.LibraryIndex>(); // FEAT-03: on-disk library index (badge accuracy)
        services.AddSingleton<ProviderHealthMonitor>();
        services.AddSingleton<SearchResultCache>();
        services.AddSingleton<ProviderResponseCache>();
        // FEAT-02: the queue store snapshots through the manager (deferred Func,
        // so registration order doesn't matter).
        services.AddSingleton<Downloads.DownloadQueueStore>(sp => new Downloads.DownloadQueueStore(
            () => sp.GetRequiredService<DownloadManager>().PendingJobsSnapshot()));
        services.AddSingleton<DownloadManager>(sp =>
        {
            var registry = sp.GetRequiredService<ProviderRegistry>();
            return new DownloadManager(
                registry.EnabledSearchProviders(),
                registry.DownloadProviders(),
                sp.GetRequiredService<AppConfig>(),
                sp.GetRequiredService<TrackTagger>(),
                sp.GetRequiredService<Downloads.DownloadQueueStore>(),
                sp.GetService<ILogger<DownloadManager>>());
        });
        services.AddSingleton<PreviewPlayer>();
        services.AddSingleton<IDispatcher, WpfDispatcher>();
        services.AddSingleton<IArtworkLoader, ArtworkLoader>();
        services.AddSingleton<ToastService>();
        services.AddSingleton<ClipboardWatcher>();
        services.AddSingleton<PlaybackViewModel>();
        services.AddSingleton<DownloadQueueViewModel>();
        services.AddSingleton<MainViewModel>(sp => new MainViewModel(
            sp.GetRequiredService<ProviderRegistry>(),
            sp.GetRequiredService<YtDlpProvider>(),
            sp.GetRequiredService<PersianIndexProvider>(),
            sp.GetRequiredService<Audio.LibraryIndex>(),
            sp.GetRequiredService<DownloadManager>(),
            sp.GetRequiredService<PreviewPlayer>(),
            sp.GetRequiredService<AppConfig>(),
            sp.GetRequiredService<AppState>(),
            sp.GetRequiredService<SearchResultCache>(),
            sp.GetRequiredService<ProviderHealthMonitor>(),
            sp.GetRequiredService<ProviderResponseCache>(),
            sp.GetRequiredService<IDispatcher>(),
            sp.GetRequiredService<IArtworkLoader>(),
            sp.GetRequiredService<SharedHttpClient>(),
            sp.GetRequiredService<ToastService>(),
            sp.GetRequiredService<ClipboardWatcher>(),
            sp.GetRequiredService<PlaybackViewModel>(),
            sp.GetRequiredService<DownloadQueueViewModel>()));
        services.AddTransient<MainWindow>();
        services.AddSingleton<TrayIconService>(sp =>
            new TrayIconService(sp.GetRequiredService<MainViewModel>(), shutdown));

        return services.BuildServiceProvider();
    }

    private void StartShell(ServiceProvider sp, AppConfig config, Window window)
    {
        // Tray attaches first — it is local and cheap, so a warm-up failure
        // can never leave the user without the tray icon.
        _trayService = sp.GetRequiredService<TrayIconService>();
        _trayService.Attach(window);

        try
        {
            BeginWarmup(sp);
        }
        catch (Exception ex)
        {
            CrashLog.Write("warmup", ex);
        }
    }

    /// <summary>Post-show network warm-up: route probes, timers, provider priming (PERF-04).</summary>
    private void BeginWarmup(ServiceProvider sp)
    {
        // Reachability: probe every provider's hosts once up front so dead
        // sources are auto-disabled before the first search, re-probe whenever
        // the network (local IP set) changes, and refresh periodically so a
        // false "dead" verdict (flaky proxy handshake) self-heals.
        var reach = sp.GetRequiredService<Network.Reachability>();
        var registry = sp.GetRequiredService<ProviderRegistry>();
        _ = RefreshRoutesAsync(registry);
        reach.RoutesChanged += () => _ = RefreshRoutesAsync(registry);
        var routeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        routeTimer.Tick += (_, _) => _ = RefreshRoutesAsync(registry);
        routeTimer.Start();

        // Warm up SoundCloud's client_id so the first search doesn't stall.
        _ = Task.Run(async () =>
        {
            try { await sp.GetRequiredService<SoundCloudProvider>().EnsureInitializedAsync(); }
            catch { /* search falls back to other providers */ }
        });

        // Warm up the Persian Index python probe (BUG-05) — async, so the
        // download tier just stays unavailable until the verdict lands.
        _ = Task.Run(async () =>
        {
            try { await sp.GetRequiredService<PersianIndexProvider>().EnsureAvailableAsync(); }
            catch { /* download tier stays unavailable */ }
        });

        // FEAT-03: index the music folder in the background so the "✓ In
        // library" badge reflects real files, not just download history.
        _ = Task.Run(async () =>
        {
            try { await sp.GetRequiredService<Audio.LibraryIndex>().BuildAsync(); }
            catch { /* badge falls back to history-based ownership */ }
        });
    }

    private static async Task RefreshRoutesAsync(ProviderRegistry registry)
    {
        try { await registry.RefreshRoutesAsync().ConfigureAwait(false); }
        catch { /* probing is best-effort */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_services is { } sp)
            {
                // Flush any pending debounced state writes before exit (PERF-01).
                try { sp.GetRequiredService<AppState>().Save(); }
                catch { /* best effort */ }
                // Bound the drain (BUG-06): a worker stuck in a stall watchdog or
                // a yt-dlp process ignoring Kill() must not hold the exit forever.
                try
                {
                    sp.GetRequiredService<DownloadManager>().StopAsync()
                        .WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
                }
                catch (TimeoutException)
                {
                    CrashLog.Write("exit", new TimeoutException("Download drain exceeded 3s; exiting anyway"));
                }
                catch { /* draining */ }
                try { sp.GetRequiredService<Network.Reachability>().Dispose(); }
                catch { /* best effort */ }
            }
        }
        finally
        {
            // Tray + container disposal must always run, even if the drain threw.
            // FileLoggerProvider flushes its channel on dispose.
            _trayService?.Dispose();
            _services?.Dispose();
            if (_fileLog is not null) try { _fileLog.Dispose(); } catch { /* best effort */ }
        }
        base.OnExit(e);
    }
}
