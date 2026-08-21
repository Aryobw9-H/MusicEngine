namespace MusicEngine.Network;

using Models;
using Providers;

/// <summary>
/// The hosts each provider depends on — used by startup/network-change probes to
/// auto-disable sources that are unreachable both directly and through the
/// proxy. Search hosts decide search availability; download hosts decide whether
/// the provider is a usable download link (a source whose CDN is dead is a dead
/// download point, not a result). Data lives in <see cref="ProviderCatalog"/>
/// (MODERN-06) — this file is just the read surface.
/// </summary>
public static class ProviderHosts
{
    public static IReadOnlyList<string> For(ProviderId id) => ProviderCatalog.Get(id).Hosts;

    /// <summary>
    /// Hosts that must answer for the provider's DOWNLOADS to be real (distinct
    /// from search when the CDN is separate). A source whose files are dead is a
    /// dead download point — the provider is auto-disabled until the CDN returns.
    /// </summary>
    public static IReadOnlyList<string> DownloadFor(ProviderId id) => ProviderCatalog.Get(id).DownloadHosts;
}
