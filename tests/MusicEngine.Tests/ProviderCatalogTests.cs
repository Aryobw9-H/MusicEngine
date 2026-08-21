namespace MusicEngine.Tests;

using Models;
using Network;
using Providers;
using Xunit;

/// <summary>
/// MODERN-06 guard: the provider set is derived from <see cref="ProviderCatalog"/>
/// everywhere, so a new provider MUST get a descriptor row or it silently
/// disappears from Settings / download ranking. This test is the tripwire.
/// </summary>
public class ProviderCatalogTests
{
    [Fact]
    public void EveryProviderIdExceptUnknownHasADescriptor()
    {
        foreach (var id in Enum.GetValues<ProviderId>())
        {
            if (id == ProviderId.Unknown) continue;
            var d = ProviderCatalog.Get(id);
            Assert.Equal(id, d.Id);
            Assert.False(string.IsNullOrWhiteSpace(d.DisplayName));
            Assert.NotEmpty(d.Hosts);
        }
    }

    [Fact]
    public void UnknownAndYtDlpAreNotUserSelectable()
    {
        // yt-dlp exists in the catalog but is never a user toggle in Settings.
        Assert.False(ProviderCatalog.Get(ProviderId.YtDlp).UserSelectable);
        // Unknown has no descriptor — Get must throw rather than silently
        // returning a stub that ranks last everywhere.
        Assert.Throws<InvalidOperationException>(() => ProviderCatalog.Get(ProviderId.Unknown));
    }

    [Fact]
    public void HostsMatchTheOriginalSwitchValues()
    {
        Assert.Equal(new[] { "api-v2.soundcloud.com", "m.soundcloud.com" }, ProviderHosts.For(ProviderId.SoundCloud));
        Assert.Equal(new[] { "music-fa.com", "musics-fa.com", "upmusics.com" }, ProviderHosts.For(ProviderId.PersianIndex));
        Assert.Equal(new[] { "aimusicall.ir" }, ProviderHosts.For(ProviderId.PersianSites));
        // The Persian CDN is separate from its search host.
        Assert.Equal(new[] { "dl.aimusicall.ir" }, ProviderHosts.DownloadFor(ProviderId.PersianSites));
        Assert.Equal(ProviderHosts.For(ProviderId.YouTube), ProviderHosts.DownloadFor(ProviderId.YouTube));
    }
}
