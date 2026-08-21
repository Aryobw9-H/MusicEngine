# MusicEngine v2

A Windows desktop music search & downloader built for Iranian network conditions. The user types a song name in **any spelling** — Finglish (`tataloo behesht`), Persian (`تتلو بهشت`), English (`coldplay yellow`), or pastes a Spotify/YouTube link — and the app identifies the *real* song, shows downloadable copies of *that exact song* (not covers, reactions, or other songs by the artist), and saves it as a tagged MP3 with artwork.

## Architecture

```
v2/
├─ src/MusicEngine/              # THE ENGINE (no UI, no WPF references)
│  ├─ Abstractions.cs            # ISearchProvider / IDownloadProvider / SearchTier
│  ├─ Models/                    # ProviderId enum, SearchResult, TrackMetadata,
│  │                             #   TrackWork/TrackVersion/GoalSong, DownloadProgress
│  ├─ Text/                      # FinglishConverter, QueryParser, TrackTextNormalizer,
│  │                             #   JunkFilter, FinglishQueryExpander
│  ├─ Search/                    # SearchService (the pipeline), GoalResolver, Ranker,
│  │                             #   WorkGrouper, ProviderHealth + SearchResultCache
│  ├─ Providers/                 # One class per source (iTunes, Deezer, YouTube, SoundCloud, RadioJavan, Nex1Music, PersianSites, PersianIndex, YtDlp)
│  ├─ Network/                   # Reachability + RoutingHandler + ProviderHosts
│  ├─ Http/                      # SharedHttpClient (client factory, routing, browser headers)
│  ├─ Downloads/                 # DownloadManager (queue), HttpDownloader (multi-segment), FileNaming
│  ├─ Audio/                     # TrackTagger (TagLib# ID3v2 + artwork embed)
│  ├─ Configuration/             # AppConfig (appsettings.json), AppState (%APPDATA% state)
│  └─ Tools/persian_fetch.py     # python curl_cffi sidecar
├─ src/MusicEngine.App/          # WPF UI (MVVM, no framework — hand-rolled)
│  ├─ App.xaml(.cs)              # DI composition root, tray, global exception handler
│  ├─ MainWindow.xaml(.cs)       # the whole UI in one window
│  ├─ SettingsWindow.xaml(.cs)   # settings dialog
│  ├─ ViewModels/                # MainViewModel, TrackItemViewModel, Mvvm.cs
│  ├─ PreviewPlayer.cs           # preview playback (WPF MediaPlayer wrapper)
│  ├─ AccentTheme.cs             # 5 accent palettes, live resource swap
│  └─ CrashLog.cs                # %APPDATA%\MusicEngine\crash.log writer
└─ tests/MusicEngine.Tests/      # console harness
```

## Quick Start

Requires .NET 8 SDK (not on PATH by default on this machine — use `D:\dotnet-sdk\dotnet.exe`).

```bash
# Build
cd D:\MusicEngine\v2
D:\dotnet-sdk\dotnet.exe build MusicEngine.sln -c Release

# Run the app (also available as run.bat)
D:\dotnet-sdk\dotnet.exe run --project src\MusicEngine.App -c Release --no-build

# Run tests (offline unit tests)
cd tests\MusicEngine.Tests
D:\dotnet-sdk\dotnet.exe run
# Live network tests:
D:\dotnet-sdk\dotnet.exe run -- live
# 50-query batch correctness run:
D:\dotnet-sdk\dotnet.exe run -- fifty
```

## Key Features

- **Multi-script search**: Finglish, Persian, English, or pasted links all work
- **Goal-based identity**: iTunes/Deezer catalogs identify the real song; all other sources must match that identity
- **Streaming results**: providers answer at different speeds; rows appear incrementally
- **Download fallback chain**: native providers → yt-dlp universal downloader
- **Persian MP3s preferred**: every download also searches upmusics/musics-fa/nex1music for a direct 320k file, which outranks SoundCloud/Radio Javan/YouTube
- **Tagged MP3 output**: ID3v2 tags + embedded artwork via TagLib#
- **Smart network routing**: per-host direct/proxy/dead detection, auto-recovery when network changes
- **WPF dark theme UI**: 5 accent colors, preview playback, download queue/history, system tray, clipboard watcher, toast notifications

## Providers

| Provider | Tier | Search | Download | Preview | Notes |
|----------|------|--------|----------|---------|-------|
| iTunes | Catalog | ✓ | — | 30s m4a | Direct (Iran reachable) |
| Deezer | Catalog | ✓ | — | 30s mp3 | Direct |
| YouTube | Display | — | via yt-dlp | — | Requires proxy |
| SoundCloud | Display | — | ✓ (progressive MP3) | — | Requires proxy + client_id scrape |
| Radio Javan | Display | — | ✓ (direct MP3) | — | Direct (Iran host) |
| Nex1Music | Display | — | ✓ (direct MP3) | — | Direct (.com domain) |
| PersianSites | Display | — | ✓ (scraped MP3) | — | aimusicall/music-fa/upmusics |
| PersianIndex | Display | — | via python sidecar | — | curl_cffi impersonation |
| YtDlp | DownloadOnly | — | universal | — | Last-resort fallback |

## Configuration

- `appsettings.json` next to the exe (copied from `src/MusicEngine.App/appsettings.json`)
- Proxy: `socks5://127.0.0.1:10808` (v2ray SOCKS5) — required for YouTube/SoundCloud/Deezer
- **YouTube bot checks**: if yt-dlp downloads fail with "Sign in to confirm you're not a bot", the proxy exit IP is flagged. Set **Browser for YouTube cookies** in Settings (e.g. `chrome` — close the browser first so its cookie DB is not locked), or export a `cookies.txt` with a browser extension and set the **cookies.txt file** field (works while the browser is open).
- Crash log: `%APPDATA%\MusicEngine\crash.log`
- Persistent state: `%APPDATA%\MusicEngine\state.json`

## License

MIT — do whatever, no warranty.