namespace MusicEngine.Models;

/// <summary>
/// Stable identity of every music source. One value per provider class —
/// these route search results back to the right downloader, so duplicates
/// break download routing.
/// </summary>
public enum ProviderId
{
    Unknown = 0,
    ITunes,
    Deezer,
    Spotify,
    YouTube,
    SoundCloud,
    RadioJavan,
    Nex1Music,
    PersianSites,   // aimusicall / music-fa / upmusics HTML profiles (one provider)
    PersianIndex,   // python curl_cffi sidecar
    YtDlp,          // universal downloader (download-tier; never a search provider)
}
