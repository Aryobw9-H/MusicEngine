namespace MusicEngine.Providers;

using Models;

/// <summary>
/// One row of provider metadata. <see cref="ProviderId"/> is the enum identity;
/// this is the single place where display names, tiers, hosts, download ranks
/// and user-visible toggle status live, so adding a provider means editing the
/// enum + one row here (MODERN-06). A test asserts every enum value except
/// <see cref="ProviderId.Unknown"/> has a descriptor — that test is what
/// prevents a silent gap (a provider that works in search but is invisible in
/// Settings, or unrankable in download resolution).
/// </summary>
public sealed record ProviderDescriptor(
    ProviderId Id,
    string DisplayName,
    SearchTier Tier,
    IReadOnlyList<string> Hosts,
    IReadOnlyList<string> DownloadHosts,
    int DownloadRank,
    bool UserSelectable);

/// <summary>Single source of truth for the provider set.</summary>
public static class ProviderCatalog
{
    public static IReadOnlyList<ProviderDescriptor> All { get; } = new[]
    {
        new ProviderDescriptor(ProviderId.ITunes, "iTunes", SearchTier.Catalog,
            ["itunes.apple.com"], ["itunes.apple.com"], 0, true),
        new ProviderDescriptor(ProviderId.Deezer, "Deezer", SearchTier.Catalog,
            ["api.deezer.com"], ["api.deezer.com"], 0, true),
        new ProviderDescriptor(ProviderId.Spotify, "Spotify", SearchTier.Display,
            ["open.spotify.com"], ["open.spotify.com"], 0, false),
        new ProviderDescriptor(ProviderId.YouTube, "YouTube", SearchTier.Display,
            ["www.youtube.com"], ["www.youtube.com"], 4, true),
        new ProviderDescriptor(ProviderId.SoundCloud, "SoundCloud", SearchTier.Display,
            ["api-v2.soundcloud.com", "m.soundcloud.com"], ["api-v2.soundcloud.com", "m.soundcloud.com"], 2, true),
        new ProviderDescriptor(ProviderId.RadioJavan, "Radio Javan", SearchTier.Display,
            ["rj-deskcloud.com"], ["rj-deskcloud.com"], 1, true),
        new ProviderDescriptor(ProviderId.Nex1Music, "Nex1Music", SearchTier.DownloadOnly,
            ["nex1music.com"], ["nex1music.com"], 5, true),
        new ProviderDescriptor(ProviderId.PersianSites, "Iranian Music Sites", SearchTier.DownloadOnly,
            ["aimusicall.ir"], ["dl.aimusicall.ir"], 5, true),
        new ProviderDescriptor(ProviderId.PersianIndex, "Persian Index", SearchTier.DownloadOnly,
            ["music-fa.com", "musics-fa.com", "upmusics.com"],
            ["music-fa.com", "musics-fa.com", "upmusics.com"], 5, true),
        new ProviderDescriptor(ProviderId.YtDlp, "yt-dlp", SearchTier.DownloadOnly,
            ["www.youtube.com"], ["www.youtube.com"], 3, false),
        new ProviderDescriptor(ProviderId.RozMusic, "RozMusic", SearchTier.DownloadOnly,
            ["rozmusic.com"], ["dl.rozmusic.com"], 5, true),
        new ProviderDescriptor(ProviderId.MusicDel, "MusicDel", SearchTier.DownloadOnly,
            ["musicdel.ir"], ["dl.musicdel.ir"], 5, true),
        new ProviderDescriptor(ProviderId.BehMelody, "BehMelody", SearchTier.DownloadOnly,
            ["behmelody.in"], ["dl.behmelody.in"], 5, true),
        new ProviderDescriptor(ProviderId.Melody98, "Melody98", SearchTier.DownloadOnly,
            ["melody98.ir"], ["dl.melody98.ir"], 5, true),
        new ProviderDescriptor(ProviderId.Aparat, "Aparat", SearchTier.DownloadOnly,
            ["www.aparat.com"], ["www.aparat.com", "cdn.asset.aparat.com"], 5, true),
        new ProviderDescriptor(ProviderId.BiaMusic, "BiaMusic", SearchTier.DownloadOnly,
            ["biamusic.ir"], ["dl.biamusic.ir"], 5, true),
        new ProviderDescriptor(ProviderId.BeatMastering, "BeatMastering", SearchTier.DownloadOnly,
            ["beatmastering.ir"], ["dl.beatmastering.ir"], 5, true),
        new ProviderDescriptor(ProviderId.MusicsFa, "MusicsFa", SearchTier.DownloadOnly,
            ["musics-fa.com"], ["dls.musics-fa.com"], 5, true),
    };

    /// <summary>Descriptor for a provider id; throws for an id missing from the catalog.</summary>
    public static ProviderDescriptor Get(ProviderId id) =>
        All.FirstOrDefault(d => d.Id == id)
        ?? throw new InvalidOperationException($"No ProviderCatalog entry for {id} — add one.");
}
