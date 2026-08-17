namespace MusicEngine.Network;

using Models;

/// <summary>
/// The hosts each provider depends on — used by startup/network-change probes to
/// auto-disable sources that are unreachable both directly and through the
/// proxy. Search hosts decide search availability; download hosts decide whether
/// the provider is a usable download link (a source whose CDN is dead is a dead
/// download point, not a result).
/// </summary>
public static class ProviderHosts
{
    public static IReadOnlyList<string> For(ProviderId id) => id switch
    {
        ProviderId.ITunes => new[] { "itunes.apple.com" },
        ProviderId.Deezer => new[] { "api.deezer.com" },
        ProviderId.YouTube => new[] { "www.youtube.com" },
        ProviderId.SoundCloud => new[] { "api-v2.soundcloud.com", "m.soundcloud.com" },
        ProviderId.RadioJavan => new[] { "rj-deskcloud.com" },
        ProviderId.Nex1Music => new[] { "nex1music.com" },
        ProviderId.PersianSites => new[] { "aimusicall.ir" },
        ProviderId.PersianIndex => new[] { "music-fa.com", "musics-fa.com", "upmusics.com" },
        ProviderId.YtDlp => new[] { "www.youtube.com" },
        _ => Array.Empty<string>(),
    };

    /// <summary>
    /// Hosts that must answer for the provider's DOWNLOADS to be real (distinct
    /// from search when the CDN is separate). A source whose files are dead is a
    /// dead download point — the provider is auto-disabled until the CDN returns.
    /// </summary>
    public static IReadOnlyList<string> DownloadFor(ProviderId id) => id switch
    {
        ProviderId.PersianSites => new[] { "dl.aimusicall.ir" },
        _ => For(id),
    };
}
