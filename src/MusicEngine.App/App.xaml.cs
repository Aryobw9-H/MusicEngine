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

/// <summary>
/// Composition root: config → state → http → providers → pipeline → download
/// manager → UI. Providers are registered once as singletons; ProviderRegistry
/// hands the enabled subsets to per-search SearchService instances.
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _services;
    private System.Windows.Forms.NotifyIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

        var config = AppConfig.Load();
        Directory.CreateDirectory(config.OutputDirectory);
        var state = AppState.Load();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddDebug().SetMinimumLevel(LogLevel.Information));

        services.AddSingleton(config);
        services.AddSingleton(state);
        services.AddSingleton<Network.Reachability>(_ => new Network.Reachability(config.ProxyUrl));
        services.AddSingleton<SharedHttpClient>(sp => new SharedHttpClient(
            config.ProxyUrl, sp.GetRequiredService<Network.Reachability>()));

        // Providers — singletons, one ProviderId each.
        services.AddSingleton<ITunesProvider>();
        services.AddSingleton<DeezerProvider>(sp => new DeezerProvider(
            sp.GetRequiredService<SharedHttpClient>(), proxyUrl: config.ProxyUrl));
        services.AddSingleton<YouTubeProvider>(sp => new YouTubeProvider(
            sp.GetRequiredService<SharedHttpClient>(), proxyUrl: config.ProxyUrl));
        services.AddSingleton<SoundCloudProvider>(sp => new SoundCloudProvider(
            sp.GetRequiredService<SharedHttpClient>(), proxyUrl: config.ProxyUrl));
        services.AddSingleton<RadioJavanProvider>(sp => new RadioJavanProvider(
            sp.GetRequiredService<SharedHttpClient>(), proxyUrl: config.ProxyUrl));
        services.AddSingleton<Nex1MusicProvider>();
        services.AddSingleton<PersianSitesProvider>();
        services.AddSingleton<PersianIndexProvider?>(sp => new PersianIndexProvider(
            sp.GetRequiredService<AppConfig>()));
        services.AddSingleton<YtDlpProvider>();

        services.AddSingleton<ProviderRegistry>();        services.AddSingleton<TrackTagger>();
        services.AddSingleton<ProviderHealthMonitor>();
        services.AddSingleton<SearchResultCache>();
        services.AddSingleton<DownloadManager>(sp =>
        {
            var registry = sp.GetRequiredService<ProviderRegistry>();
            return new DownloadManager(
                registry.EnabledSearchProviders(),
                registry.DownloadProviders(),
                sp.GetRequiredService<AppConfig>(),
                sp.GetRequiredService<TrackTagger>());
        });
        services.AddSingleton<PreviewPlayer>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        _services = services.BuildServiceProvider();

        AccentTheme.Apply(config.Accent);

        // Reachability: probe every provider's hosts once up front so dead
        // sources are auto-disabled before the first search, re-probe whenever
        // the network (local IP set) changes, and refresh periodically so a
        // false "dead" verdict (flaky proxy handshake) self-heals.
        var reach = _services.GetRequiredService<Network.Reachability>();
        var registry = _services.GetRequiredService<ProviderRegistry>();
        _ = RefreshRoutesAsync(registry);
        reach.RoutesChanged += () => _ = RefreshRoutesAsync(registry);
        var routeTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        routeTimer.Tick += (_, _) => _ = RefreshRoutesAsync(registry);
        routeTimer.Start();

        // Warm up SoundCloud's client_id so the first search doesn't stall.
        _ = Task.Run(async () =>
        {
            try { await _services.GetRequiredService<SoundCloudProvider>().EnsureInitializedAsync(); }
            catch { /* search falls back to other providers */ }
        });

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
        InitTray(window);
    }

    private static async Task RefreshRoutesAsync(ProviderRegistry registry)
    {
        try { await registry.RefreshRoutesAsync().ConfigureAwait(false); }
        catch { /* probing is best-effort */ }
    }

    /// <summary>System tray icon: quick re-open, cancel-all, exit.</summary>
    private void InitTray(MainWindow window)
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "MusicEngine",
            Visible = true,
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open MusicEngine", null, (_, _) => RestoreWindow(window));
        menu.Items.Add("Cancel all downloads", null, (_, _) =>
            _services?.GetRequiredService<MainViewModel>().CancelAll());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            _tray!.Visible = false;
            Shutdown();
        });
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => RestoreWindow(window);
    }

    private static void RestoreWindow(Window window)
    {
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        if (_services is { } sp)
        {
            try { sp.GetRequiredService<DownloadManager>().StopAsync().GetAwaiter().GetResult(); }
            catch { /* draining */ }
            try { sp.GetRequiredService<Network.Reachability>().Dispose(); }
            catch { /* best effort */ }
            sp.Dispose();
        }
        base.OnExit(e);
    }
}
