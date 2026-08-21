# Streaming Service Download Research

Deep research into downloading from Deezer, Qobuz, Tidal, and alternative sources.
Last updated: August 2026.

---

## Table of Contents

1. [Executive Summary](#executive-summary)
2. [Deezer](#deezer)
3. [Qobuz](#qobuz)
4. [Tidal](#tidal)
5. [Alternative Sources](#alternative-sources)
6. [Tool Comparison Matrix](#tool-comparison-matrix)
7. [Integration Recommendations](#integration-recommendations)

---

## Executive Summary

| Service | Free Account? | Best Free Quality | Tool Available? | Integration Difficulty |
|---------|--------------|-------------------|-----------------|----------------------|
| **Deezer** | ✅ Yes | MP3 128kbps | ✅ Multiple | **Easy** — just need ARL cookie |
| **Qobuz** | ⚠️ 30-day trial only | FLAC 24/192 during trial | ✅ qobuz-dl2 | **Medium** — token-based auth |
| **Tidal** | ✅ Yes | AAC 128kbps (web) / 160kbps (desktop) | ⚠️ tidal-dl (maintenance mode) | **Hard** — API changes frequently |
| **Spotify** | ✅ Yes | OGG 160kbps (via YouTube) | ✅ spotDL, Zotify | **Easy** — but YouTube quality varies |
| **Soulseek** | ✅ Free P2P | Whatever users share (often FLAC) | ✅ py-soulseek-lib | **Medium** — need credentials |
| **Bandcamp** | ✅ Free tracks | MP3 128kbps (name-your-price) | ⚠️ bandcampdownloader (C#) | **Easy** — but limited catalog |

---

## Deezer

### How It Works

Deezer uses an **ARL (Authentication Resource Locator)** cookie — a ~200-character alphanumeric token stored in your browser after login. This token:
- Does NOT expire (lasts indefinitely unless you log out)
- Is tied to your account (free or premium)
- Is all you need to authenticate programmatically

### Free Account Capabilities

| Quality | Bitrate | Free? |
|---------|---------|-------|
| MP3 128 | 128 kbps | ✅ **Yes** |
| MP3 320 | 320 kbps | ❌ Premium only |
| FLAC | 16-bit/44.1kHz | ❌ Premium only |

### Download Flow

```
1. Get ARL cookie from browser DevTools (Storage → Cookies → arl)
2. Use ARL to get JWT token from Deezer's Pipe GraphQL API
3. Use JWT to query track metadata (title, artist, album, etc.)
4. Get CDN download URL from track metadata
5. Download audio file directly from CDN
6. Apply ID3 tags and album art
```

### Available Tools

#### 1. deezer-downloader (kmille) — ⭐ RECOMMENDED
- **Repo**: https://github.com/kmille/deezer-downloader
- **Language**: Python
- **License**: MIT
- **Free account**: ✅ Works for 128kbps
- **Features**: REST API, web frontend, Docker, MPD integration
- **Install**: `pip install deezer-downloader`
- **How to use**: Set ARL cookie in config, run server, use REST API

```bash
# Get your ARL cookie from browser DevTools
# Then:
deezer-downloader --show-config-template > config.ini
# Edit config.ini, set cookie_arl
deezer-downloader --config config.ini
# REST API at http://localhost:5000
```

#### 2. deezspot (jakiepari) — ⭐ RECOMMENDED for Library Use
- **Repo**: https://github.com/jakiepari/deezspot
- **Language**: Python
- **License**: AGPL-3.0
- **Free account**: ✅ Works for MP3_128
- **Features**: Python library, download tracks/albums/playlists, ID3 tags
- **Install**: `pip install git+https://github.com/jakiepari/deezspot`

```python
from deezspot.deezloader import DeeLogin

dl = DeeLogin(arl='YOUR_ARL_TOKEN')
dl.download_trackdee(
    link_track='https://www.deezer.com/track/123456789',
    output_dir='./downloads',
    quality_download='MP3_128'
)
```

#### 3. streamrip (nathom) — All-in-One
- **Repo**: https://github.com/nathom/streamrip
- **Language**: Python
- **License**: GPL-3.0
- **Free account**: ✅ Deezer 128kbps, SoundCloud free
- **Note**: Qobuz and Tidal require paid subscriptions
- **Install**: `pip3 install streamrip --upgrade`

```bash
# Search and download from Deezer
rip search deezer album 'daft punk'
rip url https://deezer.com/album/12345
```

#### 4. deezer-python-gql (music-assistant) — For Advanced Use
- **Repo**: https://github.com/music-assistant/deezer-python-gql
- **Language**: Python (async, typed)
- **License**: Apache-2.0
- **Use case**: Building custom integrations
- **Features**: Full GraphQL API client, auto-JWT refresh

```python
from deezer_python_gql import DeezerGQLClient

client = DeezerGQLClient(arl="YOUR_ARL")
track = await client.get_track(track_id="3135556")
# track has media URLs, lyrics, contributors
```

### Deezer Integration Verdict

**EASIEST to integrate.** Free accounts work perfectly for 128kbps. The ARL cookie approach is stable and well-documented. Multiple Python libraries available.

**Recommended approach**: Use `deezspot` as a Python sidecar (similar to `persian_fetch.py`). User provides their free Deezer ARL cookie, and the app can download any track/album/playlist at 128kbps.

---

## Qobuz

### How It Works

Qobuz uses a **user_auth_token** (JWT) obtained from a logged-in browser session. In 2024-2025, Qobuz removed direct email/password login and moved behind OAuth with reCAPTCHA.

### Account Tiers

| Tier | Price | Quality | Download? |
|------|-------|---------|-----------|
| Free trial (30 days) | $0 | FLAC 24/192 | ✅ During trial |
| Studio Premier | $12.99/mo | FLAC 24/192 | ✅ |
| Studio Sublime+ | $14.99/mo | FLAC 24/192 + discounts | ✅ |
| No subscription | — | Streaming only (30s clips) | ❌ |

**Key insight**: Qobuz has NO permanent free tier. But:
- 30-day free trial gives full FLAC access
- You can buy individual tracks/albums without subscription (Qobuz Store)
- Purchased tracks can be downloaded without active subscription

### Download Flow

```
1. Log into play.qobuz.com in browser
2. Extract user_auth_token from DevTools (Network → login request → Response)
3. Use token to authenticate with Qobuz API
4. Extract app_id and app_secret from Qobuz web player JavaScript
5. Request track download URL from API
6. Download FLAC/MP3 from Qobuz CDN
```

### Available Tools

#### 1. qobuz-dl2 (pdx-cycle) — ⭐ RECOMMENDED
- **Repo**: https://github.com/pdx-cycle/qobuz-dl2
- **Language**: Python (async)
- **License**: GPL-3.0
- **Requires**: Active subscription (or trial)
- **Features**: Token-based auth, auto-refresh, concurrent downloads
- **Install**: Clone repo, `uv tool install .`

```bash
# Login via browser (captures token automatically)
qobuz-dl2 login

# Download by URL
qobuz-dl2 dl https://open.qobuz.com/album/xxxxxxxxxxxxx

# Interactive search
qobuz-dl2 fun

# Best quality
qobuz-dl2 dl -q 27 https://open.qobuz.com/track/123456
```

#### 2. streamrip — Also supports Qobuz
- Same tool as above, Qobuz support built-in
- Needs subscription + session token

#### 3. qobuz-cli (PyPI)
- Lighter alternative
- Also needs token-based auth

### Qobuz Integration Verdict

**REQUIRES PAID SUBSCRIPTION.** No way around it — Qobuz has no free tier. However:
- 30-day free trial is available
- qobuz-dl2 is well-maintained and modern
- Token auto-refresh makes it semi-persistent
- Could offer as "bring your own subscription" feature

**Recommended approach**: Integrate qobuz-dl2 as an optional sidecar. Users who have Qobuz subscriptions can provide their token and download at full FLAC quality. The app could guide users through the token extraction process.

---

## Tidal

### How It Works

Tidal uses a **device code flow** for authentication:
1. App requests a device code from Tidal API
2. User goes to tidal.com/link and enters the code
3. User authorizes the app
4. App receives access token

Tidal also has an official Developer Portal where you can register apps and get client_id + client_secret.

### Account Tiers

| Tier | Price | Quality | Free? |
|------|-------|---------|-------|
| Free | $0 | AAC 128kbps (web) / 160kbps (desktop) | ✅ |
| HiFi | $10.99/mo | FLAC 16/44.1 (CD quality) | ❌ |
| HiFi Plus | $19.99/mo | FLAC 24/192 (HiRes) | ❌ |

**Key insight**: Tidal has a free tier, but it's limited to 128-160kbps and requires occasional "ad breaks."

### Download Flow

```
1. Register app at developer.tidal.com (get client_id + client_secret)
2. Use device code flow to authenticate user
3. Get access_token from Tidal API
4. Use Tidal API to get stream URL for track
5. Download audio from CDN
```

### Available Tools

#### 1. tidal-dl (yaronzz)
- **Repo**: https://github.com/yaronzz/Tidal-Media-Downloader
- **Language**: Python
- **Status**: ⚠️ Maintenance mode, API keys break frequently
- **Requires**: Paid subscription for quality downloads
- **Free tier**: Can download at 128kbps but tool is unreliable

#### 2. tidal-dl-ng (r3ferrei fork)
- **Repo**: https://github.com/r3ferrei/tidal-dl-ng-1
- **Language**: Python
- **Status**: Original by exislow was DMCA'd; fork exists
- **Requires**: Paid subscription
- **Features**: Multithreaded, FLAC extraction from MP4

#### 3. streamrip — Also supports Tidal
- Free accounts limited to 128kbps AAC
- Paid accounts get FLAC

#### 4. tidal-hifi (Mastermindzh)
- **Repo**: https://github.com/Mastermindzh/tidal-hifi
- **Language**: Electron
- **Use case**: Web player wrapper, not a downloader
- **Note**: Could be used to intercept audio streams

### Tidal Integration Verdict

**HARDER than Deezer but possible with free tier.** Tidal's free tier gives 128kbps, but the download tools are in various states of broken. The official API is available but requires app registration.

**Recommended approach**: Lower priority than Deezer. If integrating:
1. Register an app at developer.tidal.com
2. Implement device code authentication flow
3. Use Tidal API to get stream URLs
4. Download at free tier quality (128kbps)

---

## Alternative Sources

### 1. Soulseek/P2P (FREE, HIGH QUALITY) — ⭐ HIGH VALUE

**What it is**: A peer-to-peer file sharing network specifically for music. Users share their entire music libraries. Often the best source for rare, niche, and lossless music.

**Quality**: Whatever users share — often FLAC, sometimes hi-res. No quality restrictions.

**Cost**: Free. Just need a Soulseek account (free registration).

**Tools**:
- **py-soulseek-lib** (59de44955ebd) — Python library for programmatic access
  - https://github.com/59de44955ebd/py-soulseek-lib
  - Supports search, transfers, and shares
  - Based on Nicotine+ code
  
- **nicotine-plus-plus** (pachiclana) — Headless Docker version
  - https://github.com/pachiclana/nicotine-plus-plus
  - Exposes HTTP API for search and download
  - Perfect for server-side integration

**Integration difficulty**: Medium. Need Soulseek credentials, but once authenticated, search and download are straightforward.

**Value for app**: ⭐⭐⭐⭐⭐ — Massive catalog including rare Iranian music, FLAC quality, completely free.

### 2. Spotify via YouTube (FREE) — ⭐ MEDIUM VALUE

**What it is**: Use Spotify metadata for search/discovery, then extract audio from YouTube.

**Quality**: Varies (128-256kbps depending on YouTube source)

**Tools**:
- **spotDL** — https://github.com/spotDL/spotify-downloader
  - Python, well-maintained
  - Uses YouTube as audio source
  - Free accounts work
  
- **Zotify** — https://github.com/zotify-dev/zotify
  - Python, uses librespot directly
  - Free accounts limited to 160kbps
  - Downloads directly from Spotify (not YouTube)

**Integration difficulty**: Easy. Both tools are pip-installable.

**Value for app**: ⭐⭐⭐ — Good for discovery and casual listening, but quality is limited.

### 3. Bandcamp (FREE for some tracks) — ⭐ LOW-MEDIUM VALUE

**What it is**: Independent music platform. Many tracks are "name your price" (including free).

**Quality**: MP3 128kbps for free downloads, higher for paid.

**Tools**:
- **bandcampdownloader** (otiel) — C# (.NET)
  - https://github.com/otiel/bandcampdownloader
  - Could be adapted directly since MusicEngine is .NET
  - Free, no account needed

**Integration difficulty**: Easy (C# library).

**Value for app**: ⭐⭐ — Limited catalog, mostly indie/underground.

### 4. SoundCloud (FREE) — ⭐ LOW VALUE

**What it is**: User-uploaded music platform.

**Quality**: Varies (128kbps default for free, 256kbps for Go+)

**Tools**: streamrip supports SoundCloud.

**Value for app**: ⭐ — Quality too variable, lots of non-music content.

### 5. Private Trackers (RED/OPS) — ⭐⭐⭐⭐ HIGH VALUE but HARD

**What it is**: Redacted (RED) and Orpheus Network (OPS) are private music trackers with the highest quality music collections in the world.

**Quality**: FLAC, hi-res, everything. Best quality available anywhere.

**Cost**: Free (but requires invite and ratio maintenance)

**Integration difficulty**: Very hard. Requires invite, ongoing seed ratio, and custom API integration.

**Value for app**: ⭐⭐⭐⭐⭐ — Best quality, most comprehensive catalog. But not practical for a consumer app.

---

## Tool Comparison Matrix

### For Deezer (Free Account)

| Tool | Language | Install | API | Quality | Maintained | Recommended |
|------|----------|---------|-----|---------|------------|-------------|
| deezspot | Python | pip | Library | 128kbps | ✅ Yes | ⭐⭐⭐⭐⭐ |
| deezer-downloader | Python | pip | REST | 128kbps | ✅ Yes | ⭐⭐⭐⭐ |
| streamrip | Python | pip | CLI | 128kbps | ⚠️ Slow | ⭐⭐⭐ |
| deezer-python-gql | Python | pip | Library | 128kbps | ✅ Yes | ⭐⭐⭐⭐ |
| Deezy | Rust | Binary | Desktop | 128kbps | ✅ Yes | ⭐⭐ |

### For Qobuz (Subscription Required)

| Tool | Language | Install | Quality | Maintained | Recommended |
|------|----------|---------|---------|------------|-------------|
| qobuz-dl2 | Python | source | FLAC 24/192 | ✅ Yes | ⭐⭐⭐⭐⭐ |
| streamrip | Python | pip | FLAC 24/192 | ⚠️ Slow | ⭐⭐⭐ |

### For Tidal (Free Tier Available)

| Tool | Language | Install | Quality | Maintained | Recommended |
|------|----------|---------|---------|------------|-------------|
| tidal-dl | Python | pip | 128kbps free | ⚠️ Breaks | ⭐⭐ |
| tidal-dl-ng | Python | pip | Paid only | ⚠️ DMCA'd | ⭐⭐ |
| streamrip | Python | pip | 128kbps free | ⚠️ Slow | ⭐⭐ |

### For Spotify (Free Account)

| Tool | Language | Install | Quality | Maintained | Recommended |
|------|----------|---------|---------|------------|-------------|
| Zotify | Python | pip | 160kbps | ✅ Yes | ⭐⭐⭐⭐ |
| spotDL | Python | pip | 128-256kbps | ✅ Yes | ⭐⭐⭐ |

### For P2P (Soulseek)

| Tool | Language | Install | Quality | Maintained | Recommended |
|------|----------|---------|---------|------------|-------------|
| py-soulseek-lib | Python | pip | FLAC | ⚠️ Stable | ⭐⭐⭐⭐ |
| nicotine-plus-plus | Python | Docker | FLAC | ✅ Yes | ⭐⭐⭐⭐⭐ |

---

## Integration Recommendations

### Priority Order

1. **Deezer via deezspot** — Easiest, free account, 128kbps
   - Python sidecar, user provides ARL cookie
   - Stable API, well-documented
   - Multiple libraries available

2. **Soulseek via py-soulseek-lib** — Free, FLAC quality
   - P2P network with massive catalog
   - Great for rare/niche music
   - Need to implement credential management

3. **Qobuz via qobuz-dl2** — Best quality (FLAC 24/192)
   - Requires subscription (or trial)
   - Token-based auth with auto-refresh
   - "Bring your own subscription" model

4. **Spotify via Zotify** — Free, 160kbps
   - Good for discovery
   - Uses Spotify metadata
   - Downloads directly from source

5. **Tidal via tidal-dl** — Free tier 128kbps
   - Lower priority due to tool instability
   - API changes frequently
   - Worth monitoring

### Architecture

```
MusicEngine App
├── Existing domestic providers (Nex1Music, PersianSites, RozMusic, etc.)
├── NEW: Deezer Sidecar (Python, deezspot, free → 128kbps)
│   └── User provides ARL cookie
├── NEW: Soulseek Sidecar (Python, py-soulseek-lib, free → FLAC)
│   └── User provides Soulseek credentials
├── NEW: Qobuz Sidecar (Python, qobuz-dl2, subscription → FLAC 24/192)
│   └── User provides Qobuz token
└── NEW: Spotify Sidecar (Python, Zotify, free → 160kbps)
    └── User provides Spotify credentials
```

### User Experience Flow

1. **Settings page** lets users configure which services to enable
2. Each service has a simple setup wizard:
   - **Deezer**: "Log into deezer.com, open DevTools, copy the 'arl' cookie value"
   - **Soulseek**: "Create free account at soulseek.org, enter credentials"
   - **Qobuz**: "Log into play.qobuz.com, extract token (or sign up for free trial)"
   - **Spotify**: "Enter Spotify username/password"
3. App automatically searches across all enabled services
4. Returns best quality available from each source
5. Downloads in background with progress reporting

### Quality Hierarchy

```
Best → Worst:
1. Qobuz HiRes FLAC (24/192) — needs subscription
2. Soulseek FLAC (16/44.1 or 24/96) — free, if available
3. Deezer FLAC (16/44.1) — needs Premium ($10.99/mo)
4. Deezer MP3 320 — needs Premium
5. Spotify OGG 160 — free
6. Deezer MP3 128 — free ✅
7. Tidal AAC 128 — free
```

### Risk Assessment

| Service | Account Ban Risk | API Stability | Legal Risk |
|---------|-----------------|---------------|------------|
| Deezer | Low (free account) | High | Medium |
| Soulseek | None (P2P) | High | Medium |
| Qobuz | Medium (subscription sharing) | High | Medium |
| Spotify | Low (free account) | Medium | Medium |
| Tidal | Low (free account) | Low | Medium |

---

## Source Code References

### Deezer ARL Token Extraction
```javascript
// In browser DevTools → Application → Cookies → deezer.com
// Find cookie named "arl"
// Value is ~200 character alphanumeric string
// Example: "abc123def456ghi789jkl012mno345pqr678stu901vwx234yz..."
```

### Qobuz Token Extraction
```javascript
// 1. Log into play.qobuz.com
// 2. Open DevTools → Network tab
// 3. Filter by "login" or "user"
// 4. Find the login request
// 5. Response contains user_auth_token
// 6. This token auto-refreshes via partner endpoint
```

### Tidal Device Code Flow
```python
# 1. Register app at developer.tidal.com
# 2. Get client_id and client_secret
# 3. Request device code:
POST https://auth.tidal.com/v1/oauth2/device_authorization
{
    "client_id": "YOUR_CLIENT_ID"
}
# Returns: device_code, user_code, verification_uri

# 4. User visits verification_uri and enters user_code
# 5. Poll for token:
POST https://auth.tidal.com/v1/oauth2/token
{
    "client_id": "YOUR_CLIENT_ID",
    "client_secret": "YOUR_CLIENT_SECRET",
    "device_code": "DEVICE_CODE",
    "grant_type": "urn:ietf:params:oauth:grant-type:urn:ietf:params:oauth:grant-type:device_code"
}
# Returns: access_token, refresh_token
```

---

## Reddit Community Intel (r/Piracy, r/musichoarder, r/Soulseek)

Sourced from Google-indexed Reddit snippets (direct access blocked by Reddit security).

### What the community actually recommends (2025-2026)

**#1 recommendation across threads: Deezer + ARL**
> "Buy 1 month of Deezer, get your ARL into Deemix and get to work"
> "deemix for download you can still use spotify for to listen"
> "deemix-Fix (this is the working version now)"

Community consensus: Deezer ARL + Deemix-Fix is the gold standard for music downloading. Free accounts work for 128kbps. Premium gives FLAC.

**#2: Soulseek for rare/niche/FLAC**
> "Soulseek cannot download Playlists" but "Soulseek for FLACs and ffmpeg to convert"
> "Soulseek, rutracker.org and Redacted is all you need"
> "I use soulseek for FLACs"

Community loves Soulseek for its massive catalog and FLAC quality. Main limitation: no playlist support.

**#3: Qobuz-dl + Streamrip for lossless**
> "1 Month free trial. Get 'Qobuz-dl', and 'Streamrip' (for Deezer). Grab spek-rs."
> "Just checked and Streamrip still working for Qobuz"
> "Need to edit config.toml"

Qobuz free trial is the common hack. Streamrip works for both Deezer and Qobuz.

**#4: Zotify for Spotify**
> "With Zotify I can download 320kbps OGG Vorbis because I have premium account"
> "There's also program called Votify"
> "I gotta recommend zotify to download your spotify!"

Zotify is the community favorite for Spotify. Free accounts limited to 160kbps.

**#5: Tidal — tool chaos**
> "what happened to tidal-dl-ng?" → "The project and the entire account (exislow) was removed"
> "I am using tidal-dl-ng-for-dj" (working fork)
> "Waves: a native, open-source GUI for downloading your TIDAL library (built on Tidal-DL-NG)" (new)

Tidal tools are in flux. Original tidal-dl-ng DMCA'd, forks exist. New Waves GUI just appeared.

**#6: Private trackers (Red/OPS)**
> "The best for private ops=Orpheus red=redacted"
> "Soulseek, rutracker.org and Redacted is all you need"
> "In my opinion, the best way to download music is to use one of the following tools: Apple Music - Apple Music Downloader, Qobuz - OrpheusDL"

Redacted/Orpheus are the highest quality sources but require invites and ratio maintenance.

### Community tools mentioned (not in GitHub research)

| Tool | What | Status |
|------|------|--------|
| **Deemix-Fix** | Working fork of Deemix | Active, community-maintained |
| **Votify** | Alternative to Zotify | Mentioned alongside Zotify |
| **OrpheusDL** | Download from Red/OPS/Qobuz | Active, Python |
| **Waves GUI** | Native Tidal downloader | New, built on tidal-dl-ng |
| **tidal-dl-ng-for-dj** | Fork of tidal-dl-ng | Working, needs poetry lock |
| **DoubleDouble** | Alternative to Lucida | Broken/dead |
| **spek-rs** | Audio spectrum analyzer | Verify download quality |
| **Seeker** | Android Soulseek client | Mobile access |

### Key takeaways from Reddit

1. **Deemix-Fix is the community standard** — not just Deemix, but the working fork
2. **Soulseek is universally loved** — every music downloading thread mentions it
3. **Qobuz free trial + qobuz-dl is the power move** — 30 days of FLAC
4. **Tidal tools are unreliable** — community is moving away
5. **Private trackers are the ultimate source** but not accessible to most users
6. **No single tool covers everything** — the community uses 2-3 tools together

---

## Conclusion

**Deezer is the clear winner** for free, easy integration. ARL cookie auth is simple, stable, and works with free accounts for 128kbps MP3. The `deezspot` library makes it trivial to add as a Python sidecar.

**Soulseek is the dark horse** — completely free, FLAC quality, massive catalog. Worth integrating for users who want the best quality without paying.

**Qobuz is the premium option** — best quality available (FLAC 24/192), but requires a paid subscription. Worth offering as a "bring your own subscription" feature.

**Tidal and Spotify** are lower priority due to tool instability and limited free tier quality.

**The Reddit community uses 2-3 tools together** — the same approach we should take. Deezer for breadth, Soulseek for depth/quality, Qobuz for premium users.
