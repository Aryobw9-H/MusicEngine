# MusicEngine v2 — Handoff Prompt

> You are taking over development of MusicEngine, a Windows music search &
> downloader built for Iranian network conditions. This document is your
> session brief: read it fully, then read `v2/README.md` (the complete
> technical reference), then look at code. Everything below was true at the
> time of handoff (2026-08-17).

## Project location & how to work on it

- Root: `D:\MusicEngine - Copy\v2` (NOT a git repo — no commits exist; be
  careful, there is no version history to fall back on).
- Shell is Git Bash on Windows. .NET 8 SDK is NOT on PATH — invoke it as
  `/d/dotnet-sdk/dotnet.exe`.
- Build: `cd "D:/MusicEngine - Copy/v2" && /d/dotnet-sdk/dotnet.exe build
  MusicEngine.sln -c Release`
- Run app: `v2\run.bat` (or the exe at
  `src/MusicEngine.App/bin/Release/net8.0-windows/MusicEngine.App.exe`).
- Tests (`tests/MusicEngine.Tests`, console harness):
  - `dotnet run` — offline unit tests (13, all pass)
  - `dotnet run -- live` — + live network tests (all pass as of handoff)
  - `dotnet run -- fifty` — 50-query batch correctness run (see OPEN PROBLEM 1)
  - `dotnet run -- fifty <file.txt>` — re-run queries from a UTF-8 file
    (existing example: `v2/recheck-queries.txt`)
  - `dotnet run -- debugapp "query"` — app-exact construction (Reachability +
    routing + registry) with per-phase timing prints; THE diagnosis tool
  - `dotnet run -- debugpersian` / `debugsc` / `bisect` — narrower diagnostics
- Config: `appsettings.json` next to the exe. The user's proxy is v2ray SOCKS5
  at `socks5://127.0.0.1:10808` — it must be RUNNING for YouTube/SoundCloud/
  Deezer/music-fa (filtered in Iran; iTunes/nex1music.com/musics-fa.com/
  upmusics.com/rj-deskcloud.com work direct). Full host matrix: README §10.
- Crash log: `%APPDATA%\MusicEngine\crash.log`. State: same folder, `state.json`.

## What the app is (30-second version)

User types a song in any spelling (Finglish / Persian / English) or pastes a
Spotify/YouTube link. The engine identifies the real song via iTunes/Deezer
(the "goal"), gates every candidate copy against that identity (cross-script
text matching), streams results into a WPF UI as sources answer, and downloads
via a fallback chain (native provider → yt-dlp) as tagged MP3s with artwork.
Full architecture: `v2/README.md` — it is kept current and detailed; trust it.

## Verified working (do not re-litigate these)

- Full pipeline: streaming search (first results ~1s), goal gate, deep rescue
  path (~14s worst case), result cache, health monitor quiescing.
- Native SoundCloud (client_id scrape + progressive MP3 download), Radio Javan,
  Nex1Music (.com domain — the .ir is dead), PersianIndex python sidecar
  (music-fa/musics-fa/upmusics via curl_cffi impersonation), iTunes, Deezer.
- Reachability: per-host direct/proxy/dead probes cached until local IP
  changes; smart per-request routing (RoutingHandler); auto-disable of
  unreachable sources incl. dead download CDNs (aimusicall's dl host).
- Downloads: 4-segment parallel Range downloader, yt-dlp with speed flags,
  queue with cancellation, ID3 tagging (verified E2E: tagged 7.9MB Persian MP3).
- UI: fluent dark theme, artist mode, sort/filter, history, tray, toasts,
  clipboard watcher, global exception handler logging to crash.log.
- **Artist mode** (new, works): a bare 1-token query like `fadaei` whose
  catalog rows are ≥3 songs by one artist emits that artist's catalog —
  `fadaei` → 30 songs, `tataloo` → 30 (second-chance detection from retrieval
  rows covers catalog-scrubbed artists), `coldplay` → 25, `eminem` → 30.
- Live test suite green, including the "fadaei azkaraj" regression (glued
  Finglish → spaced Persian title must match; the imposter
  "ای دختر کرجی از ترکاشوند" must not).

## OPEN PROBLEM 1 (top priority): batch correctness — now 45/48, three edge cases left

UPDATE (same day, later session): the wrong-song flood was NOT the fuzzy
matching — it was an **empty-haystack hole in `FieldMatch`**
(SearchService.cs): `FieldMatch("", needle)` returned TRUE via the reversed
token call, so after mis-split recovery set an empty-artist goal, EVERY
empty-artist scraper row passed the swapped check. Fixed (empty haystack →
false). Also fixed since the original handoff:

- Fuzzy matching is now **alef-insensitive for Persian** and stricter
  (≤1 edit up to 8 chars, ≤2 only ≥9): `TrackTextNormalizer.FuzzyEq`.
- Homophone folds extended (آ→ا, ح→ه added): `UnifyPersian`.
- `PassesLooseGate` and the swapped-field check use **exact-token semantics**
  (`ContainsAllTokens(..., fuzzy:false, substring:false)`); the combined-field
  check requires a non-fuzzy TITLE side.
- `GoalResolver` (no-hijack, kept) no longer adopts a same-artist row's
  DURATION when the title didn't match (it gated out the real song).
- Gate tracing: `[gate]` / `[resplit-candidate]` prints under `DebugPhases`,
  plus a `-- gatecheck <goalArtist> <goalTitle> <rowArtist> <rowTitle>` mode
  that decomposes one decision — use these first when debugging matching.

Batch state (`-- fifty`, verified-real 48-query list): **45/48 with correct
top hits** (artist mode 30/30/26/30, both-script hits, international clean).
IMPORTANT: the original 50-list contained misremembered songs ("tehran
jasbi", "bahram khatarnak", "dariush ala deyi", "sohrab mj tadavom",
"aslan hayalde", "rashel fereshte", "tataloo baroonam" — verified absent
from SC + the Iranian index). An honest zero is CORRECT for those; don't
"fix" them.

Remaining three (all return 0, deep path exhausted):

1. `shadmehr deejad` — the real song is "دیداد" (Dejad). The correct row must
   exist on SC/iTunes; the deejay-benjy mix is now correctly rejected, but
   the real one doesn't pass either. Trace with
   `-- debugapp "shadmehr deejad"` + `[gate]` prints.
2. `masoud roh nikan ashobe ghalbam` — real (verified on upmusics as
   "مسعود روح نیکان آشوب قلبم"). Folds should bridge روح/روه and آشوب/اشوب —
   check whether the rows even arrive (SC search latin may miss him).
3. `فدایی از کرج تا لنگه رود` (Persian full-title) — PASSES sometimes
   ("Fadaei — Az Karaj Ta Langerud" seen in one run), 0 in others: the 4c
   re-gate depends on which YT/SC rows happen to land. Flaky, not logic-dead.
4. `macan band adat` — fixed by the duration fix (verify it stays green).
   Known acceptable warts: `mehrzad` cover (Reza Raad) can still outrank the
   original (goal artist is polluted by the bad split "mehrzad mano"); the
   works-artist-priority ordering in BuildWorks doesn't fire because the
   needle's second token fails. A smarter fix: match the work artist against
   the goal artist with fuzzy when ordering.

## OPEN PROBLEM 2: ranking polish (after problem 1)

- Covers/remixes can outrank originals (Reza Raad cover above Mehrzad's).
  `Ranker.VersionLike` now includes edit/mix/bootleg/sped up/slowed — the
  Eminem case is fixed; the mehrzad case is the work-representative issue
  described above.

## Working conventions that matter

- Match the existing code style: file-scoped namespaces, explicit types,
  XML doc comments on public members explaining WHY (constraints, network
  realities), Persian-literate matching logic in `Text/`.
- Every behavioral change to matching/gating should get an offline unit test
  in `tests/MusicEngine.Tests/Program.cs` (the fadaei azkaraj tests are the
  template — construct SearchResult/GoalSong, assert both directions).
- Keep `SearchService.DebugPhases` prints working; they are the latency
  diagnosis tool. The phase budget invariants: provider 6s, catalog 5s,
  rescue min(9, timeout+2), hard grace 2×timeout+4s.
- Never block requests on slow probes (RoutingHandler's 1.5s optimistic-proxy
  rule) and never wrap inner handlers in HttpClient (the "request already
  sent" trap — README §3).
- The python sidecar is the ONLY thing that can scrape music-fa family sites
  (.NET TLS fingerprint is blocked); keep its JSON protocol stable
  (`search|links|dl` modes, `--proxy` with dead-proxy fallback).
- UI crash-safety: the global handler in App.xaml.cs keeps the app alive;
  check `%APPDATA%\MusicEngine\crash.log` after any UI bug report.
- When done with changes: run offline tests, live tests, the 50-batch, and a
  UI smoke (launch exe, search, confirm results render — UIAutomation
  PowerShell scripts were used for this; SearchBox automation id, "Search"
  button name).

## Immediate task list (in order)

1. Fix OPEN PROBLEM 1 (loose-gate over-merging). Re-run batch until top hits
   are correct and count ≥42/50.
2. Verify OPEN PROBLEM 2 ranking polish with `mehrzad mano natarsoon`,
   `eminem lose yourself`, `mohsen chavoshi hobab`.
3. Re-run `-- live` suite + offline suite; all green.
4. Update `v2/README.md` §1/§2 if gate semantics changed, and this file's
   open-problems section if new ones appear.
