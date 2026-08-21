# Iranian Domestic Music Sources — Verified Research (no proxy/VPN needed)

Research date: 2026-08-18. **Every endpoint below was probed from the Iranian
network this app runs on** (the same box that reaches `rj-deskcloud.com`).
"✅ reachable" = returned a real HTTP response without a proxy/VPN.

The goal: give MusicEngine domestic search + download sources that keep working
when the user has no proxy. Today only Radio Javan qualifies; the rest die with
the proxy. Below are the verified alternatives, plus fixes for two existing
providers whose targets turned out to be **alive but changed**.

---

## Tier 1 — Fully verified, no proxy (implement these)

### 1. Radio Javan (`rj-deskcloud.com`) — already integrated, keep as the anchor
The app's RJ provider already works. Nothing to do here; listed for the record.

### 2. Aparat (`aparat.com`) — the big new win (Iranian YouTube, fully domestic)
Aparat is Iran's largest video platform, entirely domestic, and hosts enormous
amounts of Persian music (official videos, lyric videos, **full albums uploaded
as playlists**). Both APIs below are plain JSON, no auth, no Cloudflare.

**Search** (verified: `jahanam` → 40 results):
```bash
curl -H "User-Agent: Mozilla/5.0" \
  "https://www.aparat.com/api/fa/v1/video/video/search?text=jahanam"
```
Response shape (JSON:API): `data[0].relationships.video.data[]` = `{type:"Video", id}`,
and the full video objects live in `included[]` (type `Video`) with:
`uid` (the video hash), `title`, `duration` (seconds), `username` (channel = artist),
`small_poster`/`big_poster`. 40 videos per page.

**Direct file** (verified: HTTP 200, `video/mp4`, `Content-Length` set):
```bash
curl -H "User-Agent: Mozilla/5.0" \
  "https://www.aparat.com/api/fa/v1/video/video/show/videohash/<uid>"
```
→ `data.attributes.file_link_all[]` = `{ "profile": "720p", "urls": ["https://<cluster>.cdn.asset.aparat.com/...mp4"] }`
profiles: 144p / 240p / 360p / 480p / 720p. The URLs are direct CDN downloads.

**Integration notes for the app:**
- These are **video files** → download the lowest acceptable profile (360p) and
  convert with ffmpeg (the app already shells out to ffmpeg for yt-dlp conversion).
- **CDN mirror fallback** (from `Mazafard/ap-dl`, a downloader updated 2026-08):
  if a `*.cdn.asset.aparat.com` host is slow/down, swap the cluster for one of
  `persian8, persian9, persian14, persian1, persian2, as1, as2, arvan1, arvan2,
  m1, m2, caspian1, caspian2, caspian12, caspian20` — same path.
- **Album discovery**: Aparat playlists (`aparat.com/playlist/<id>`) are commonly
  full albums; the playlist API is `GET https://www.aparat.com/playlist/<id>` and
  the modern downloader parses it client-side. The `v/<hash>?playlist=<id>` URL
  pattern also works. A future `IAlbumDiscovery` implementation for Aparat would
  expand these playlists exactly like the YouTube one.
- Search text is URL-encoded and must be 2–512 chars (validate before calling).

### 3. nex1music.com — provider EXISTS in the app, site is ALIVE (fix, don't replace)
Verified: home + search + song pages + mp3 CDN all return 200 from this network.

- Search: `https://nex1music.com/?s=<q>` (WordPress; `?s=` returns 200 with
  post links like `nex1music.com/آهنگ-<artist>-<title>/`). Also
  `https://nex1music.com/search/<q>`.
- Song page embeds the direct mp3 in a `data-music` attribute on `div.item`
  (with `data-artist`/`data-track`): `https://dl.nex1music.com/1405/05/27/<Artist> - <Title> [128].mp3`
  (verified: HTTP 200, 3 MB, `application/octet-stream`). The site currently
  serves **128 kbps only** — the old 320/128 `div.lnkdl` download buttons were
  removed in a 2025 redesign.
- ✅ **FIXED in the app (2026-08-18)**: `Nex1MusicProvider` now parses the
  `data-music` items (preferring the item whose artist+title matches the post
  slug), fills real artist/title metadata, and downloads the direct mp3.
  Verified end-to-end through the app pipeline: search → `DownloadAsync` →
  `سیاوش امینی - قرار.mp3` (3 MB) landed on disk. **This is the working
  domestic download source.**
- Related GitHub (for reference): `DevTomy/xMusic`, `tkiew/nex1music-dl`. The
  mobile-app API backend `apin1mservice.com` (search.php / singlemusic.php →
  `music320`/`music128`) is **unreachable from this network** — don't build on it.

### 4. aimusicall.ir — site up, CDN dead (dormant, liveness-guarded)
Verified: the site is up, and `/?s=<q>` **302-redirects to `/search/<q>`**, and
both the SERP and post pages still embed direct mp3s:
`https://dl.aimusicall.ir/musics/1403/09/10/haydeh - jahanam to hasti (128).mp3`.
**But every file on the CDN 404s** — old and brand-new posts alike, with or
without Referer/Range. The site is a zombie: HTML publishing continues, all
audio is gone.

✅ **FIXED in the app (2026-08-18)**: `PersianSitesProvider` now hits
`https://aimusicall.ir/search/<q>` directly and **range-probes every candidate
mp3 before surfacing it** (a ranged GET must return 200/206). Today that yields
zero live rows — the provider is dormant but never emits dead links. If the CDN
comes back, it starts working with no further changes. The stale
music-fa.com/upmusics.com fallbacks were dropped from the docs and code.

### 5. RozMusic (`rozmusic.com`) — new domestic source (search + direct MP3)
Verified: site is up, search works, CDN is live and serves direct MP3s.

**Search** (verified: `jahanam` → results):
```bash
curl -H "User-Agent: Mozilla/5.0" \
  "https://rozmusic.com/?s=jahanam"
```
Returns WordPress-style search results with post links like:
`rozmusic.com/آهنگ-<artist>-<title>.html`

**Song page** contains:
- Artist name, song title, lyrics
- Download buttons for 320 kbps and 128 kbps
- The actual MP3 URL is constructed client-side from the filename

**CDN structure** (verified, indexed by search engines):
```
dl.rozmusic.com/Music/{year}/{month}/{day}/{Artist} - {Title}.mp3
dl.rozmusic.com/Music/{year}/{month}/{day}/{Artist} - {Title} (128).mp3
```
Example: `dl.rozmusic.com/Music/1396/03/20/Hamed%20Zamani%20-%20Delaram.mp3`

**Integration notes:**
- Site is WordPress-based with standard search
- CDN uses Solar Hijri (Jalali) calendar dates
- Direct HTTP download from CDN, no authentication needed
- 320 kbps is the default (no quality suffix), 128 kbps has `(128)` suffix

### 6. MusicDel (`musicdel.ir`) — new domestic source (search + direct MP3)
Verified: site is up, search works, CDN is live and serves direct MP3s.

**Search** (verified: returns results):
```bash
curl -H "User-Agent: Mozilla/5.0" \
  "https://musicdel.ir/?s=jahanam"
```
Returns WordPress-style search results with post links like:
`musicdel.ir/single-tracks/<id>/`

**Song page** contains:
- Artist name, song title, lyrics
- Download buttons for 320, 128, and 64 kbps
- Online player with audio source URL

**CDN structure** (verified, indexed by search engines):
```
dl.musicdel.ir/Music/{year}/{month}/{day}/{Artist} - {Title} (320).mp3
dl.musicdel.ir/Music/{year}/{month}/{day}/{Artist} - {Title} (128).mp3
dl.musicdel.ir/Music/{year}/{month}/{day}/{Artist} - {Title} (64).mp3
```
Example: `dl.musicdel.ir/Music/1400/08/06/%20-%20Instrumental%201%20(320).mp3`

**Integration notes:**
- Site is WordPress-based with standard search
- CDN uses Solar Hijri (Jalali) calendar dates
- Direct HTTP download from CDN, no authentication needed
- Quality suffixes: `(320)`, `(128)`, `(64)`

### 7. BehMelody (`behmelody.in`) — new domestic source (search + direct MP3)
Verified: site is up, search works, CDN is live and serves direct MP3s.

**Search** (verified: returns results):
```bash
curl -H "User-Agent: Mozilla/5.0" \
  "https://behmelody.in/?s=jahanam"
```
Returns WordPress-style search results with post links like:
`behmelody.in/دانلود-آهنگ-<artist>-<title>/`

**Song page** contains:
- Artist name, song title, lyrics
- Download buttons for 320, 128, and FLAC
- Online player

**CDN structure** (verified, indexed by search engines):
```
dl.behmelody.in/{year}/{month}/{day}/{Album}/{Title} (320).mp3
dl.behmelody.in/{year}/{month}/{day}/{Album}/{Title} (128).mp3
dl.behmelody.in/{year}/{month}/{day}/{Album}/{Title} (Flac).flac
```
Example: `dl.behmelody.in/1403/11/18/Hurry%20Up%20Tomorrow%20[Mp3]/Wake%20Me%20Up%20-%20The%20Weeknd%20(320).mp3`

**Integration notes:**
- Site is WordPress-based with standard search
- CDN uses Solar Hijri (Jalali) calendar dates
- Direct HTTP download from CDN, no authentication needed
- Quality suffixes: `(320)`, `(128)`, `(Flac)`
- Also offers FLAC lossless downloads

### 8. Melody98 (`melody98.ir`) — new domestic source (search + direct MP3)
Verified: site is up, search works, CDN is live and serves direct MP3s.

**Search** (verified: returns results):
```bash
curl -H "User-Agent: Mozilla/5.0" \
  "https://melody98.ir/?s=jahanam"
```
Returns search results with post links like:
`melody98.ir/music/<id>/`

**Song page** contains:
- Artist name, song title, lyrics
- Download buttons for 320 and 128 kbps
- Online player

**CDN structure** (verified, indexed by search engines):
```
dl.melody98.ir/music/{year}/{month}/{day}/{Artist} - {Title} [320].mp3
dl.melody98.ir/music/{year}/{month}/{day}/{Artist} - {Title} (320).mp3
dl.melody98.ir/music/{year}/{month}/{day}/{Artist} - {Title} (128).mp3
```
Example: `dl.melody98.ir/music/1405/04/10/Raibod%20%26%20Toba%20Ai%20-%20Ey%20Bala%20Mala%20Ey%20Tata%20Tala%20(Joz%20Baghalet)%20320.mp3`

**Integration notes:**
- Site is WordPress-based with standard search
- CDN uses Solar Hijri (Jalali) calendar dates
- Direct HTTP download from CDN, no authentication needed
- Quality suffixes: `[320]`, `(320)`, `(128)`

---

## Tier 1b — FLAC Lossless Sources (domestic, no proxy)

### 9. BehMelody FLAC (`dl.behmelody.in`) — verified FLAC CDN
**Status**: Free, direct download, no authentication
**CDN Structure**:
```
dl.behmelody.in/{year}/{month}/{day}/{Album} [Flac]/{Track} - {Title}.flac
```
**Example**: `dl.behmelody.in/1403/11/18/Hurry%20Up%20Tomorrow%20[Flac]/01.%20The%20Weeknd%20-%20Wake%20Me%20Up.flac`
**Verified**: Index page shows actual FLAC files (16-bit/44.1kHz)
**Integration notes**:
- WordPress-based site with standard search
- CDN uses Solar Hijri (Jalali) calendar dates
- Also offers MP3 320/128 kbps
- FLAC directory is separate from MP3 directory
- Direct HTTP download, no auth needed

### 10. Songsara FLAC (`dl.songsara.net`) — verified FLAC CDN
**Status**: Free, direct download, no authentication
**CDN Structure**:
```
dl.songsara.net/{category}/{year}/{month}/{Album}/{Track} - {Title}.flac
dl.songsara.net/instrumental/{jalali_date}/{Album}/{Track}.flac
```
**Verified**: Index pages show actual FLAC files
**Integration notes**:
- Large archive of FLAC music (instrumental, soundtracks, etc.)
- Uses Solar Hijri (Jalali) calendar dates
- Direct HTTP download, no auth needed
- Mix of free and potentially paid content

### 11. Tiarin FLAC (`tiarin.ir`) — verified FLAC playlists
**Status**: Free, playlist-based FLAC downloads
**Structure**: Curated playlists of FLAC albums
**Integration notes**:
- Focuses on Iranian pop and traditional music
- FLAC 16-bit quality
- Playlist-based organization
- May require navigating through playlists to find tracks

---

## Tier 2 — Work only WITH a proxy (the current situation)
- YouTube / yt-dlp, SoundCloud, iTunes, Deezer — all filtered on the Iranian
  network. Keep them as proxy-backed tiers; don't rely on them domestically.

## Dead / unreachable from this network (do not build on these)
- `melatify.ir` + `api.melatify.ir` — DNS dead (melatify is gone).
- `apin1mservice.com` (nex1music app API), `music-fa.com` + `dl.`/`s2.`,
  `javanmusic.ir`, `tonmusic.com` (domain parked), `songfto.com`,
  `mo3lyfaat.com`, `tahmusic.com`, `iranmusic.org`, `bia2mp3.com`,
  `persian-music-download.com`, `newsongs.ir`, `mihanweb.net`.
- **Telegram** (`api.telegram.org`, `t.me`) — unreachable from this network
  (state filtering). Do not plan a Telegram-based source for no-proxy mode.
- `radiojavan.com` main site — unreachable, but the **API host
  `rj-deskcloud.com` works** (that's what the app uses; keep using it).

---

## GitHub findings (code references)
| Repo | What it proves |
|---|---|
| `Mazafard/ap-dl` (updated 2026-08-18) | The **current** Aparat API: `show/videohash/{uid}` → `file_link_all`; CDN mirror list. |
| `ytdl-org/youtube-dl` `aparat.py` | Old Aparat recipe (`options = {...}` in page) — **no longer works**, page is Next.js now. |
| `soroushchehresa/radiojavan-downloader` (43★) | RJ download patterns (Chrome ext). |
| `MhdiTaheri/Radiojavan-dl` | RJ downloader Telegram bot. |
| `DevTomy/xMusic`, `tkiew/nex1music-dl` | nex1music API/scraper (API backend dead, site alive). |
| `Cmatrix1/MusicAPI-Scraping-Redis-restframework` | Iranian music API scraper — scaffold only, no usable endpoints. |

## X / Twitter
The OpenCLI browser bridge was not connected, so direct X search wasn't possible;
a web search over x.com surfaced no dedicated "domestic Persian music download
API" threads worth citing. (One notable adjacent fact: Andropay advertises
"upload to YouTube without VPN via the official API" — upload-only, not relevant
to downloads.) Re-run with the bridge connected if X opinions are required.

---

## Recommended implementation order
1. ✅ **Done (2026-08-18): `Nex1MusicProvider` fixed** — `data-music` parser,
   real artist/title, 128 kbps. Verified real download through the app pipeline.
2. ✅ **Done (2026-08-18): `PersianSitesProvider` fixed** — `/search/<q>` URL +
   liveness probe. Dormant while dl.aimusicall.ir 404s everything; self-heals if
   the CDN returns.
3. **Next: add `AparatProvider`** (search + file + ffmpeg audio conversion) — the
   highest-value NEW domestic source, also enables playlist-based album search.
4. **Add `RozMusicProvider`** — search + direct MP3 download from `dl.rozmusic.com`.
   320 kbps default, 128 kbps optional. High-quality Persian music archive.
5. **Add `MusicDelProvider`** — search + direct MP3 download from `dl.musicdel.ir`.
   320/128/64 kbps options. Large catalog with lyrics.
6. **Add `BehMelodyProvider`** — search + direct MP3/FLAC download from `dl.behmelody.in`.
   Offers FLAC lossless option in addition to 320/128.
7. **Add `Melody98Provider`** — search + direct MP3 download from `dl.melody98.ir`.
   320/128 kbps options.
8. **Add FLAC support to BehMelody** — detect FLAC directory on song pages,
   offer FLAC download when available. 16-bit/44.1kHz lossless quality.
9. **Add `SongsaraProvider`** — FLAC archive for instrumental/soundtrack content.
   Large collection of classical and traditional Iranian music.
10. Keep RJ first in tier order; keep yt-dlp/SoundCloud/iTunes/Deezer as
    proxy-optional tiers.

---

## Tier 3 — Streaming Services (require accounts, no proxy needed)

Full research: `STREAMING_SERVICE_DOWNLOAD_RESEARCH.md`.

### 12. Deezer (FREE account → 128kbps MP3)
**Status**: Free account works, no subscription needed
**Tool**: `deezspot` (Python library)
**Quality**: 128kbps MP3 (free), 320kbps/FLAC (premium)
**Auth**: ARL cookie from browser DevTools
**Integration**: Python sidecar, user provides ARL cookie
**Value**: ⭐⭐⭐⭐⭐ — Easiest to integrate, stable API

### 13. Soulseek (FREE P2P → FLAC)
**Status**: Free P2P network, massive catalog
**Tool**: `py-soulseek-lib` or `nicotine-plus-plus`
**Quality**: FLAC (whatever users share)
**Auth**: Free Soulseek account
**Integration**: Python sidecar with credentials
**Value**: ⭐⭐⭐⭐⭐ — Best free quality, rare music

### 14. Qobuz (subscription → FLAC 24/192)
**Status**: Requires paid subscription
**Tool**: `qobuz-dl2` (modernized fork)
**Quality**: FLAC 24/192 (HiRes)
**Auth**: Token from browser session
**Integration**: Python sidecar, "bring your own subscription"
**Value**: ⭐⭐⭐⭐ — Best quality available

### 15. Spotify (FREE account → 160kbps)
**Status**: Free account works
**Tool**: `Zotify` (Python)
**Quality**: 160kbps OGG (free), 320kbps (premium)
**Auth**: Spotify credentials
**Integration**: Python sidecar
**Value**: ⭐⭐⭐ — Good for discovery

### 16. Tidal (FREE tier → 128kbps)
**Status**: Free tier available, tools unstable
**Tool**: `tidal-dl` (maintenance mode)
**Quality**: 128kbps (free), FLAC (paid)
**Auth**: Device code flow
**Integration**: Low priority due to tool instability
**Value**: ⭐⭐ — Monitor only
