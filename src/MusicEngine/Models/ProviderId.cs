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
    RozMusic,       // rozmusic.com - domestic MP3 320/128
    MusicDel,       // musicdel.ir - domestic MP3 320/128/64
    BehMelody,      // behmelody.in - domestic MP3 320/128 + FLAC
    Melody98,       // melody98.ir - domestic MP3 320/128
    Aparat,         // aparat.com - Iranian YouTube, domestic video/audio
    BiaMusic,       // biamusic.ir - domestic MP3 320/128
    BeatMastering,  // beatmastering.ir - domestic MP3 320
    MusicsFa,       // musics-fa.com - domestic MP3 320/128
}
