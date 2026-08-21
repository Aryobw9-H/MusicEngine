namespace MusicEngine.Providers;

using Configuration;
using Models;
using Network;

/// <summary>
/// Holds every provider instance once and exposes the search set currently
/// enabled in settings. SearchService instances are cheap; they are constructed
/// per search against this registry so source toggles apply instantly.
///
/// Sources whose hosts are unreachable BOTH directly and through the proxy are
/// auto-disabled (they would only burn their timeout and fail) and listed in
/// <see cref="OfflineSources"/> for the UI status line. Routes are re-probed
/// whenever the network changes.
/// </summary>
public sealed class ProviderRegistry
{
    public ITunesProvider ITunes { get; }
    public DeezerProvider Deezer { get; }
    public YouTubeProvider YouTube { get; }
    public SoundCloudProvider SoundCloud { get; }
    public RadioJavanProvider RadioJavan { get; }
    public Nex1MusicProvider Nex1Music { get; }
    public PersianSitesProvider PersianSites { get; }
    public PersianIndexProvider? PersianIndex { get; }
    public YtDlpProvider YtDlp { get; }
    public RozMusicProvider RozMusic { get; }
    public MusicDelProvider MusicDel { get; }
    public BehMelodyProvider BehMelody { get; }
    public Melody98Provider Melody98 { get; }
    public AparatProvider Aparat { get; }

    public BiaMusicProvider BiaMusic { get; }

    public BeatMasteringProvider BeatMastering { get; }
    public MusicsFaProvider MusicsFa { get; }

    private readonly Configuration.ISettings _config;
    private readonly Reachability _reach;
    private readonly HashSet<ProviderId> _offline = new();
    private readonly object _offlineLock = new();

    public ProviderRegistry(
        Configuration.ISettings config,
        Reachability reachability,
        ITunesProvider iTunes,
        DeezerProvider deezer,
        YouTubeProvider youTube,
        SoundCloudProvider soundCloud,
        RadioJavanProvider radioJavan,
        Nex1MusicProvider nex1Music,
        PersianSitesProvider persianSites,
        PersianIndexProvider? persianIndex,
        YtDlpProvider ytDlp,
        RozMusicProvider rozMusic,
        MusicDelProvider musicDel,
        BehMelodyProvider behMelody,
        Melody98Provider melody98,
        AparatProvider aparat,

        BiaMusicProvider biaMusic,

        BeatMasteringProvider beatMastering,

        MusicsFaProvider musicsFa)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _reach = reachability;
        ITunes = iTunes;
        Deezer = deezer;
        YouTube = youTube;
        SoundCloud = soundCloud;
        RadioJavan = radioJavan;
        Nex1Music = nex1Music;
        PersianSites = persianSites;
        PersianIndex = persianIndex;
        YtDlp = ytDlp;
        RozMusic = rozMusic;
        MusicDel = musicDel;
        BehMelody = behMelody;
        Melody98 = melody98;
        Aparat = aparat;

        BiaMusic = biaMusic;

        BeatMastering = beatMastering;

        MusicsFa = musicsFa;
    }

    /// <summary>Display names of sources currently unreachable (direct AND proxied).</summary>
    public IReadOnlyList<string> OfflineSources
    {
        get
        {
            lock (_offlineLock)
            {
                return AllProviders()
                    .Where(p => _offline.Contains(p.Id))
                    .Select(p => p.DisplayName)
                    .ToList();
            }
        }
    }

    private IEnumerable<IMusicProvider> AllProviders()
    {
        var all = new List<IMusicProvider> { ITunes, Deezer, YouTube, SoundCloud, RadioJavan, Nex1Music, PersianSites, YtDlp, RozMusic, MusicDel, BehMelody, Melody98, Aparat, BiaMusic, BeatMastering, MusicsFa };
        if (PersianIndex is not null) all.Add(PersianIndex);
        return all;
    }

    /// <summary>
    /// Probe every provider's hosts (parallel, a few seconds). Providers go
    /// offline when none of their search hosts answer, or when none of their
    /// download hosts do (dead CDN = dead download points).
    /// </summary>
    public async Task RefreshRoutesAsync(CancellationToken ct = default)
    {
        var providers = AllProviders().ToList();
        var probes = providers.Select(async p =>
        {
            var hosts = ProviderHosts.For(p.Id);
            var dlHosts = ProviderHosts.DownloadFor(p.Id);
            if (hosts.Count == 0) return (p.Id, alive: true);
            var routes = await Task.WhenAll(hosts.Select(h => _reach.ProbeAsync(h, ct))).ConfigureAwait(false);
            var searchAlive = routes.Any(r => r is HostRoute.Direct or HostRoute.ViaProxy);
            // Download-host check only bites when the download CDN is separate
            // AND fully dead — partial CDNs degrade, total death disables.
            var dlRoutes = await Task.WhenAll(dlHosts.Select(h => _reach.ProbeAsync(h, ct))).ConfigureAwait(false);
            var downloadsAlive = dlRoutes.Any(r => r is HostRoute.Direct or HostRoute.ViaProxy);
            return (p.Id, alive: searchAlive && downloadsAlive);
        });
        var results = await Task.WhenAll(probes).ConfigureAwait(false);
        lock (_offlineLock)
        {
            _offline.Clear();
            foreach (var (id, alive) in results)
                if (!alive)
                    _offline.Add(id);
        }
    }

    /// <summary>Every available search provider, filtered by the user's source
    /// toggles and by live reachability.</summary>
    public IReadOnlyList<ISearchProvider> EnabledSearchProviders()
    {
        lock (_offlineLock)
        {
            return AllProviders()
                .OfType<ISearchProvider>()
                .Where(p => p.IsAvailable && _config.IsSourceEnabled(p.Id) && !_offline.Contains(p.Id))
                .ToList();
        }
    }

    /// <summary>Every available download provider. Source toggles do not gate
    /// the fallback chain. Route-probe offline status is IGNORED here —
    /// domestic CDN downloads must always be attempted even when the probe
    /// fails (probes use ICMP/TCP pings that ISPs often block, but actual
    /// HTTP downloads work fine). Only the native provider chain decides
    /// whether a source is usable.</summary>
    public IReadOnlyList<IDownloadProvider> DownloadProviders()
    {
        return AllProviders()
            .OfType<IDownloadProvider>()
            .Where(p => p.IsAvailable)
            .ToList();
    }
}
