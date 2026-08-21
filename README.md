# MusicEngine v2

A Windows desktop music search & downloader built for Iranian network conditions. The user types a song name in **any spelling** — Finglish (`tataloo behesht`), Persian (`تتلو بهشت`), English (`coldplay yellow`), or pastes a Spotify/YouTube link — and the app:

1. **Translates** the query to both Persian and Finglish automatically
2. **Searches** all providers in parallel (catalogs + domestic scrapers)
3. **Identifies** the real song via iTunes/Deezer catalog matching
4. **Groups** duplicate results from different providers into one row (artist+title+duration ±5s)
5. **Downloads** from the best available provider with automatic fallback chain
6. **Tags** the MP3 with ID3v2 metadata + embedded artwork

## Architecture

```
v2/
├─ src/MusicEngine/                    # THE ENGINE (no UI, no WPF references)
│  ├─ Abstractions.cs                  # ISearchProvider / IDownloadProvider / SearchTier
│  ├─ Models/
│  │  ├─ ProviderId.cs                # Enum: iTunes, Deezer, YouTube, SoundCloud,
│  │  │                                #   RadioJavan, Nex1Music, RozMusic, MusicDel,
│  │  │                                #   BehMelody, Melody98, BiaMusic, BeatMastering,
│  │  │                                #   MusicsFa, Aparat, PersianSites, PersianIndex, YtDlp
│  │  ├─ TrackModels.cs               # SearchResult, TrackMetadata
│  │  ├─ WorkModels.cs                # TrackWork, TrackVersion, GoalSong
│  │  └─ DownloadModels.cs            # DownloadProgress, DownloadResult, DownloadPhase
│  ├─ Text/
│  │  ├─ FinglishConverter.cs         # Persian ↔ Latin transliteration (bidirectional)
│  │  ├─ FinglishQueryExpander.cs     # Expands query to [Persian, Finglish, Combined]
│  │  ├─ QueryParser.cs               # Artist/Title extraction from raw queries
│  │  ├─ TrackTextNormalizer.cs        # Cross-script normalization, fuzzy matching
│  │  └─ JunkFilter.cs                # Filters junk titles (covers, reactions, etc.)
│  ├─ Search/
│  │  ├─ SearchService.cs             # THE PIPELINE: query → fan-out → gate → group → rank
│  │  ├─ ProviderFanOut.cs            # Builds per-provider query plans, concurrent collection
│  │  ├─ GoalResolver.cs              # Resolves the "goal song" from catalog results
│  │  ├─ GoalGate.cs                  # Strict/loose/ultra-lenient gate for result filtering
│  │  ├─ Ranker.cs                    # Scores results by relevance + quality
│  │  ├─ WorkGrouper.cs              # Groups results by artist+title+duration (±5s)
│  │  ├─ WorkAssembler.cs            # Builds final TrackWork list from catalog + retrieval
│  │  ├─ AlbumDetection.cs            # Album/artist mode detection
│  │  ├─ SearchRunContext.cs          # Shared state during a search run
│  │  ├─ QueryHeuristics.cs           # Persian-ish query detection for speculative search
│  │  ├─ SearchResultCache.cs         # In-memory cache (keyed on canonical expanded form)
│  │  ├─ UrlQueryResolver.cs          # Resolves pasted Spotify/YouTube URLs to text queries
│  │  └─ ProviderHealth.cs            # Per-provider health monitoring + quiescing
│  ├─ Providers/
│  │  ├─ ProviderRegistry.cs          # Registers all providers, provides search/download lists
│  │  ├─ ProviderCatalog.cs           # Catalog metadata (artist/album/track numbers)
│  │  ├─ ITunesProvider.cs            # Catalog: search + 30s preview
│  │  ├─ DeezerProvider.cs            # Catalog: search + 30s preview
│  │  ├─ YouTubeProvider.cs           # Search via YouTube (proxy required)
│  │  ├─ SoundCloudProvider.cs        # Search + progressive MP3 download (proxy required)
│  │  ├─ RadioJavanProvider.cs        # Search + direct MP3 (Iran host)
│  │  ├─ Nex1MusicProvider.cs         # Search + direct MP3 320kbps
│  │  ├─ RozMusicProvider.cs          # Search + direct MP3 320kbps
│  │  ├─ MusicDelProvider.cs          # Search + direct MP3 320kbps
│  │  ├─ BehMelodyProvider.cs         # Search + direct MP3/FLAC
│  │  ├─ Melody98Provider.cs          # Search + direct MP3 320kbps
│  │  ├─ BiaMusicProvider.cs          # Search + direct MP3 320kbps
│  │  ├─ BeatMasteringProvider.cs     # Search + CDN MP3 320kbps
│  │  ├─ MusicsFaProvider.cs          # Search + direct MP3 320kbps
│  │  ├─ AparatProvider.cs            # Search + video download (FFmpeg audio extract)
│  │  ├─ PersianSitesProvider.cs      # Aggregates aimusicall/music-fa/upmusics
│  │  ├─ PersianIndexProvider.cs      # Python curl_cffi sidecar (impersonation)
│  │  └─ YtDlpProvider.cs            # Universal fallback (last resort)
│  ├─ Downloads/
│  │  ├─ DownloadManager.cs           # Queue: resolve candidates → build chain → download
│  │  ├─ HttpDownloader.cs            # Multi-segment parallel HTTP download
│  │  ├─ FileNaming.cs                # Output filename from template + metadata
│  │  ├─ DownloadQueueStore.cs        # Persistent download queue (survives restart)
│  │  └─ AudioFile.cs                 # Audio file metadata extraction
│  ├─ Http/
│  │  ├─ SharedHttpClient.cs          # Client factory, routing, browser headers, TLS relax
│  │  └─ ArtworkLoader.cs             # Async artwork fetch with timeout
│  ├─ Audio/
│  │  ├─ TrackTagger.cs               # TagLib# ID3v2 tagging + artwork embed (crash-safe)
│  │  └─ LibraryIndex.cs              # Scans output folder for existing tracks
│  ├─ Network/
│  │  ├─ Reachability.cs              # Per-host connectivity detection
│  │  └─ ProviderHosts.cs             # Host→provider mapping for routing
│  └─ Configuration/
│     ├─ AppConfig.cs                 # appsettings.json config model
│     ├─ AppState.cs                  # %APPDATA% persistent state
│     └─ ISettings.cs                 # Settings interface
├─ src/MusicEngine.App/                # WPF UI (MVVM, hand-rolled — no framework)
│  ├─ App.xaml(.cs)                   # DI composition root, tray, global exception handler
│  ├─ MainWindow.xaml(.cs)            # Main search + results UI
│  ├─ SettingsWindow.xaml(.cs)        # Settings dialog (proxy, cookies, paths)
│  ├─ BatchWindow.xaml(.cs)           # Batch download from playlist/album
│  ├─ ViewModels/
│  │  ├─ MainViewModel.cs            # Search, results, download orchestration
│  │  ├─ TrackItemViewModel.cs        # Single track result (with provider versions)
│  │  ├─ DownloadQueueViewModel.cs    # Download queue + history
│  │  ├─ PlaybackViewModel.cs         # Preview playback control
│  │  ├─ BatchViewModel.cs            # Batch download logic
│  │  ├─ SettingsViewModel.cs         # Settings binding
│  │  └─ Mvvm.cs                      # INotifyPropertyChanged base classes
│  ├─ Ui/
│  │  ├─ ClipboardWatcher.cs          # Auto-detect pasted Spotify/YouTube URLs
│  │  ├─ ToastService.cs              # Desktop toast notifications
│  │  ├─ TrayIconService.cs           # System tray icon
│  │  └─ WpfDispatcher.cs             # Thread-safe UI dispatch helper
│  ├─ Diagnostics/
│  │  └─ DiagnosticsBundle.cs         # Export logs + state for debugging
│  ├─ Logging/
│  │  └─ FileLoggerProvider.cs        # Rolling file logger (%APPDATA%)
│  ├─ PreviewPlayer.cs                # WPF MediaPlayer wrapper for 30s previews
│  ├─ AccentTheme.cs                  # 5 accent palettes, live resource swap
│  ├─ Converters.cs                   # WPF value converters
│  └─ CrashLog.cs                     # %APPDATA%\MusicEngine\crash.log writer
├─ tests/MusicEngine.Tests/            # Unit + integration tests
│  ├─ AlbumSearchTests.cs             # Album search correctness
│  ├─ TextPipelineTests.cs            # Text normalization / Finglish conversion
│  ├─ DownloadQueuePersistenceTests.cs # Queue survive restart
│  ├─ HttpDownloaderTests.cs          # Multi-segment download
│  ├─ LibraryIndexTests.cs            # Library scanning
│  ├─ ProviderCatalogTests.cs         # Catalog metadata
│  └─ YouTubeDiscoveryTests.cs        # YouTube playlist detection
├─ tools/MusicEngine.DevTools/         # Developer utilities
└─ MusicEngine.sln                     # Solution file
```

## Features

### Bilingual Search
- User types in **any script** — the app auto-expands to both Persian and Finglish
- `"فدایی کمین"` → also searches `"fadaei kamin"` on all providers
- `"fadaei kamin"` → also searches `"فدایی کمین"` on all providers
- Combined variant `"فدایی کمین fadaei kamin"` also sent for maximum recall

### Smart Result Grouping
- Results from different providers (BeatMastering, RozMusic, Nex1Music, etc.) for the **same song** are grouped into **one row**
- Matching: same artist + title + duration within ±5 seconds
- Each grouped row shows a **provider picker** — click to choose which source to download from
- Same provider showing different versions (remix, live, etc.) → shown as separate versions

### Download Fallback Chain
- **Native provider first**: BeatMastering → RozMusic → Nex1Music → MusicDel → etc.
- **yt-dlp universal fallback**: if all domestic providers fail
- Automatic retry on transient failures
- Resume support for interrupted downloads

### 15+ Music Sources

| Provider | Tier | Search | Download | Quality | Notes |
|----------|------|--------|----------|---------|-------|
| iTunes | Catalog | ✓ | — | 30s m4a preview | Direct (Iran reachable) |
| Deezer | Catalog | ✓ | — | 30s mp3 preview | Direct |
| SoundCloud | Display | ✓ | ✓ | Progressive MP3 | Requires proxy + client_id |
| Radio Javan | Display | ✓ | ✓ | Direct MP3 | Iran host, no proxy needed |
| Nex1Music | Display | ✓ | ✓ | MP3 320kbps | Direct |
| RozMusic | Display | ✓ | ✓ | MP3 320kbps | Direct |
| MusicDel | Display | ✓ | ✓ | MP3 320kbps | Direct |
| BehMelody | Display | ✓ | ✓ | MP3/FLAC | Direct |
| Melody98 | Display | ✓ | ✓ | MP3 320kbps | Direct |
| BiaMusic | Display | ✓ | ✓ | MP3 320kbps | Direct |
| BeatMastering | DownloadOnly | ✓ | ✓ | MP3 320kbps | CDN-based |
| MusicsFa | DownloadOnly | ✓ | ✓ | MP3 320kbps | Direct |
| Aparat | DownloadOnly | ✓ | ✓ | MP3 (FFmpeg extract) | Video platform |
| PersianSites | DownloadOnly | ✓ | ✓ | Scraped MP3 | aimusicall/music-fa/upmusics |
| PersianIndex | DownloadOnly | ✓ | ✓ | Via Python sidecar | curl_cffi impersonation |
| YouTube | Display | ✓ | via yt-dlp | Varies | Requires proxy |
| YtDlp | DownloadOnly | — | Universal | Varies | Last-resort fallback |

### Smart Network Routing
- Per-host direct/proxy/dead detection
- Auto-recovery when network changes
- TLS certificate relaxation for Iranian CDN hosts
- Browser User-Agent headers to avoid blocks

### Tagged MP3 Output
- ID3v2 tags: title, artist, album, year
- Embedded artwork (cover art from provider)
- Crash-safe: tags written to temp file first, then atomically moved
- Customizable filename template

### WPF Dark Theme UI
- 5 accent color palettes (live swap)
- Preview playback (30s samples)
- Download queue with progress
- Download history
- System tray integration
- Clipboard watcher (auto-detect pasted URLs)
- Toast notifications
- Batch download from playlists

## Quick Start

Requires .NET 8 SDK.

```bash
# Build
cd D:\MusicEngine\v2
dotnet build MusicEngine.sln -c Release

# Run the app
dotnet run --project src\MusicEngine.App -c Release --no-build

# Run tests (offline)
cd tests\MusicEngine.Tests
dotnet run

# Live network tests
dotnet run -- live

# 50-query batch correctness run
dotnet run -- fifty
```

## Configuration

- `appsettings.json` next to the exe (copied from `src/MusicEngine.App/appsettings.json`)
- **Proxy**: `socks5://127.0.0.1:10808` (v2ray SOCKS5) — required for YouTube/SoundCloud/Deezer
- **YouTube cookies**: if yt-dlp fails with "Sign in to confirm you're not a bot", set **Browser for YouTube cookies** in Settings (e.g. `chrome` — close browser first), or export `cookies.txt` with a browser extension
- **Output directory**: configurable in Settings (default: `%USERPROFILE%\Music\MusicEngine`)
- **Filename template**: `{Artist} - {Title}.mp3` (customizable)
- Crash log: `%APPDATA%\MusicEngine\crash.log`
- App logs: `%APPDATA%\MusicEngine\logs\app-{date}.log`
- Persistent state: `%APPDATA%\MusicEngine\state.json`

## Download Flow

```
User clicks Download
  ↓
DownloadManager.RunJobAsync()
  ↓
ResolveCandidatesAsync()
  → Get DownloadableVersions from TrackWork
  → Rank by quality (320kbps > 128kbps > preview)
  → Return ordered candidate list
  ↓
BuildChain(candidate)
  → Match by provider ID (not CanDownload — avoids yt-dlp poisoning)
  → Yield native provider first, then yt-dlp as fallback
  ↓
foreach provider in chain
  → provider.DownloadAsync()
  → On success: return DownloadResult
  → On failure: log, try next provider
  ↓
TrackTagger.TagAsync()
  → Copy to .tagtmp (crash-safe)
  → Fetch artwork if missing
  → Write ID3v2 tags
  → Atomic move back to original
  ↓
File added to LibraryIndex
```

## Tech Stack

- **.NET 8** / C# 12
- **WPF** (hand-rolled MVVM, no framework)
- **TagLib#** for ID3v2 tagging
- **HtmlAgilityPack** for HTML parsing
- **System.Text.Json** for serialization
- **curl_cffi** (Python sidecar) for impersonation requests
- **yt-dlp** (CLI) for universal fallback downloads
- **FFmpeg** (CLI) for audio extraction from video platforms

## License

MIT — do whatever, no warranty.
