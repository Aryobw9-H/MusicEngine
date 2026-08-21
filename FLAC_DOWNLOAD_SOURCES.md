# FLAC Lossless Download Sources — Research

Research date: 2026-08-18. Documenting domestic Iranian and international FLAC sources.

---

## Domestic Iranian FLAC Sources

### 1. BehMelody (`dl.behmelody.in`) — ✅ Verified working FLAC CDN
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

### 2. Songsara (`dl.songsara.net`) — ✅ Verified working FLAC CDN
**Status**: Free, direct download, no authentication
**CDN Structure**:
```
dl.songsara.net/{category}/{year}/{month}/{Album}/{Track} - {Title}.flac
dl.songsara.net/instrumental/{jalali_date}/{Album}/{Track}.flac
dl.songsara.net/hamid/{jalali_year}/{jalali_month}/{Album}/
```
**Verified**: Index pages show actual FLAC files
**Integration notes**:
- Large archive of FLAC music (instrumental, soundtracks, etc.)
- Uses Solar Hijri (Jalali) calendar dates
- Direct HTTP download, no auth needed
- Mix of free and potentially paid content

### 3. Tiarin (`tiarin.ir`) — ✅ Verified FLAC playlists
**Status**: Free, playlist-based FLAC downloads
**Structure**: Curated playlists of FLAC albums
**Integration notes**:
- Focuses on Iranian pop and traditional music
- FLAC 16-bit quality
- Playlist-based organization
- May require navigating through playlists to find tracks

### 4. FlacSong (`flacsong.ir`) — ✅ Dedicated FLAC site
**Status**: Free, direct download
**Structure**: FLAC files organized by genre/artist
**Integration notes**:
- Dedicated to FLAC format specifically
- Iranian and international music
- May have smaller catalog than general music sites

### 5. Iranian CDN (`edge05.405891.ir.cdn.ir`) — ✅ Verified FLAC hosting
**Status**: Free, direct download
**CDN Structure**:
```
edge05.405891.ir.cdn.ir/{year}/{jalali_month}/{Artist}/{Album} [Flac]/
```
**Verified**: Hosts FLAC files for various albums
**Integration notes**:
- Iranian CDN hosting FLAC files
- Solar Hijri (Jalali) calendar dates
- Direct HTTP download

---

## International FLAC Sources (May require proxy from Iran)

### 6. Bandcamp — ✅ Verified FLAC downloads
**Status**: Paid (with some free albums), direct download
**Structure**: Artist/album pages with download options
**Integration notes**:
- Many artists offer FLAC downloads
- Some albums are free or "name your price"
- Requires account for purchases
- May be filtered from Iran (proxy needed)

### 7. Qobuz — ✅ Verified Hi-Res FLAC
**Status**: Paid, streaming + download
**Integration notes**:
- 24-bit Hi-Res FLAC available
- Large catalog
- Requires subscription
- May be filtered from Iran

### 8. Tidal — ✅ Verified Hi-Res FLAC
**Status**: Paid, streaming + download
**Integration notes**:
- HiFi quality FLAC
- Large catalog
- Requires subscription
- May be filtered from Iran

---

## Integration Strategy

### For MusicEngine App

**Priority 1: BehMelody FLAC** (domestic, free, verified)
- Add FLAC directory detection to existing BehMelody provider
- When user requests high quality, check for FLAC directory first
- Fallback to MP3 320 if FLAC not available

**Priority 2: Songsara FLAC** (domestic, free, verified)
- Add Songsara as new provider
- Focus on instrumental/soundtrack FLAC content
- Large archive of classical and traditional music

**Priority 3: Tiarin FLAC** (domestic, free, verified)
- Add Tiarin as discovery layer for FLAC albums
- Curated playlists of high-quality FLAC content

**Priority 4: Bandcamp** (international, may need proxy)
- Add Bandcamp provider for FLAC downloads
- Focus on free/"name your price" albums
- Proxy required from Iran

### Quality Tiers

```
Tier 1: FLAC 16-bit/44.1kHz (CD quality)
  Sources: BehMelody, Songsara, Tiarin, FlacSong

Tier 2: FLAC 24-bit/Hi-Res (studio quality)
  Sources: Songsara (some), Qobuz, Tidal

Tier 3: MP3 320 kbps (high quality)
  Sources: All existing providers

Tier 4: MP3 128 kbps (standard)
  Sources: All existing providers
```

---

## Technical Notes

### FLAC vs MP3
- FLAC: Lossless, ~10-15x larger than MP3 320
- MP3 320: Lossy, smaller files, good enough for most listeners
- Use case: Audiophiles, car audio systems, archival

### CDN Patterns (Solar Hijri Calendar)
All Iranian FLAC CDNs use Solar Hijri (Jalali) calendar dates:
- Year: e.g., 1403, 1404, 1405
- Month: e.g., Farvardin, Ordibehesht, Tir
- Day: e.g., 18, 22

### File Naming Conventions
- BehMelody: `{Track}. {Artist} - {Title}.flac`
- Songsara: `{Track} - {Title}.flac` or `{Artist} - {Title}.flac`
- Tiarin: Varies by playlist

---

## Recommendations

1. **Add FLAC support to existing BehMelody provider** — lowest effort, highest impact
2. **Create Songsara provider** — large FLAC archive, domestic, free
3. **Add Tiarin discovery layer** — curated FLAC playlists
4. **Consider Bandcamp integration** — international FLAC with proxy

5. **Quality preference in settings**: Let user choose preferred quality
   - "FLAC preferred" → try FLAC first, fallback to MP3 320
   - "MP3 320" → always MP3 320
   - "Auto" → FLAC when available, MP3 320 otherwise
