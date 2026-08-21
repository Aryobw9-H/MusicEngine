namespace MusicEngine.Search;

using Microsoft.Extensions.Logging;
using Models;
using Text;

/// <summary>Lifecycle of one provider within a search — drives the chip strip (PERF-07).</summary>
public enum ProviderState
{
    Pending,
    Responded,
    TimedOut,
    Failed,
    Offline,
}

/// <summary>
/// THE search pipeline: query understanding → concurrent provider fan-out →
/// strict goal gate → work grouping/ranking → streaming TrackWork. Persian-ish
/// queries join the slow Iranian tiers up front; rescue rounds and the YouTube
/// fallback run concurrently.
/// </summary>
public sealed class SearchService
{
    /// <summary>Set true (tests/debug) to print phase timings to stdout.</summary>
    public static bool DebugPhases;

    private readonly IReadOnlyList<ISearchProvider> _providers;
    private readonly IReadOnlyList<IAlbumDiscovery> _discovery;
    private readonly ProviderHealthMonitor _health;
    private readonly SearchResultCache _cache;
    private readonly ProviderResponseCache? _providerCache;
    private readonly IGoalGate _gate;
    private readonly ProviderFanOut _fanOut;
    private readonly Http.SharedHttpClient? _http;
    private readonly TimeSpan _providerTimeout;
    private readonly TimeSpan _catalogTimeout;
    private readonly TimeSpan _rescueTimeout;

    public SearchService(
        IEnumerable<ISearchProvider> providers,
        ProviderHealthMonitor? health = null,
        SearchResultCache? cache = null,
        ProviderResponseCache? providerCache = null,
        IGoalGate? gate = null,
        ILogger<SearchService>? logger = null,
        int searchTimeoutSeconds = 6,
        Http.SharedHttpClient? http = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.Where(p => p.IsAvailable).ToArray();
        _discovery = _providers.OfType<IAlbumDiscovery>().ToArray();
        _health = health ?? new ProviderHealthMonitor();
        _cache = cache ?? new SearchResultCache();
        _providerCache = providerCache;
        _gate = gate ?? new GoalGate();
        _fanOut = new ProviderFanOut(_providers, _health, _providerCache);
        _http = http;
        _providerTimeout = TimeSpan.FromSeconds(Math.Max(2, searchTimeoutSeconds));
        _catalogTimeout = TimeSpan.FromSeconds(Math.Min(5, Math.Max(2, searchTimeoutSeconds)));
        // The deep rescue path runs only when nothing faster worked; the scrapers
        // (python spawn + proxied fetches) need a little more headroom.
        _rescueTimeout = TimeSpan.FromSeconds(Math.Min(9, Math.Max(5, searchTimeoutSeconds + 2)));
    }

    /// <summary>Canonical cache key (PERF-03): delegated to <see cref="SearchResultCache.CanonicalKey"/>.</summary>
    internal static string CanonicalCacheKey(string query) => SearchResultCache.CanonicalKey(query);


    /// <summary>Progress callbacks for streaming UI updates.</summary>
    public sealed class Callbacks
    {
        /// <summary>Human-readable status line ("Searching 7 sources…").</summary>
        public Action<string>? Status { get; init; }

        /// <summary>Emitted whenever the current work snapshot changes.</summary>
        public Action<IReadOnlyList<TrackWork>>? Batch { get; init; }

        /// <summary>Per-provider phase update for the status chip strip (PERF-07).</summary>
        public Action<ProviderId, ProviderState>? ProviderStatus { get; init; }

        public static Callbacks None { get; } = new();
    }

    public async IAsyncEnumerable<TrackWork> SearchAsync(
        string query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var works = await RunAsync(query, null, ct).ConfigureAwait(false);
        foreach (var w in works) yield return w;
    }

    /// <summary>Full pipeline with streaming callbacks; returns the final works.
    /// <paramref name="albumsOnly"/> forces album mode: discovery runs even for
    /// single tokens and the song rescue rounds become an album widen.</summary>
    public async Task<List<TrackWork>> RunAsync(string query, Callbacks? cb, CancellationToken ct = default, bool albumsOnly = false)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<TrackWork>();
        query = query.Trim();

        // 0. A pasted track URL (Spotify/YouTube/…) becomes a text query first.
        if (Uri.TryCreate(query, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            var resolved = await UrlQueryResolver.ResolveAsync(uri, ct, _http).ConfigureAwait(false);
            if (resolved is null) return new List<TrackWork>();
            query = resolved;
        }

        // 1. Cache hit → instant, keyed on the canonical expanded form (PERF-03).
        //    Album-mode results live in a separate slot — the toggle must not
        //    serve stale song-mode singles.
        var cacheKey = albumsOnly ? CanonicalCacheKey(query) + "|album" : CanonicalCacheKey(query);
        if (_cache.TryGet(cacheKey) is { } cached)
        {
            cb?.Status?.Invoke("cached result");
            cb?.Batch?.Invoke(cached);
            return cached.ToList();
        }

        // 2. Query understanding. The RAW query is parsed (iTunes/Deezer rank the
        //    user's own script far better than a machine transliteration); the
        //    Persian expansion feeds the Iranian-site tiers.
        var expanded = FinglishQueryExpander.Expand(query);
        if (expanded.Count == 0) return new List<TrackWork>();
        var persianVariant = TrackTextNormalizer.HasPersian(query)
            ? query
            : expanded.FirstOrDefault(TrackTextNormalizer.HasPersian) ?? query;
        var parsed = QueryParser.Parse(query);
        var originalExplicit = parsed.HasExplicitStructure;

        var catalogPlans = _fanOut.BuildCatalogPlans(parsed, query);
        var retrievalPlans = _fanOut.BuildRetrievalPlans(query, expanded);
        var onProviderStatus = cb?.ProviderStatus;
        // Speculative rescue: Iranian sites scrape slowly, so for Persian-ish
        // queries they join the FIRST fan-out; their rows are more gate candidates.
        var speculated = QueryHeuristics.ShouldSpeculate(query)
            ? _providers.Where(p => p.Tier == SearchTier.DownloadOnly
                                    && p.Id != ProviderId.YtDlp
                                    && !_health.IsQuiesced(p.Id))
                        .Select(p => (p, persianVariant, 5))
                        .ToList()
            : new List<(ISearchProvider, string, int)>();
        retrievalPlans.AddRange(speculated);

        // 3. Concurrent fan-out: catalogs resolve the goal while retrieval runs (rows landing before it wait in a buffer).
        var t0 = DateTime.UtcNow;
        cb?.Status?.Invoke($"Searching {catalogPlans.Count + retrievalPlans.Count} sources…");

        var ctx = new SearchRunContext(
            _gate, _providers, cb,
            new GoalSong(parsed.Artist ?? "", parsed.Title ?? "", null, ProviderId.Unknown),
            parsed, ct);

        var catalogTask = _fanOut.CollectAsync(catalogPlans, _catalogTimeout, ct, null, onProviderStatus);
        var retrievalTask = _fanOut.CollectAsync(retrievalPlans, _providerTimeout, ct, rows => ctx.OnRetrievalRows(rows, query), onProviderStatus);
        // Album discovery (YouTube playlists) runs in PARALLEL with the fan-out —
        // Persian albums ("fadaei hagh") aren't in the catalogs. Auto mode probes
        // only multi-token queries; the Albums toggle probes always.
        var discoveryTask = _discovery.Count > 0
            && (albumsOnly || query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 2)
            ? ctx.DiscoverAsync(query, lenient: albumsOnly, _catalogTimeout)
            : null;

        var catalog = await catalogTask.ConfigureAwait(false);
        if (DebugPhases) Console.WriteLine($"[phase] catalog +{(DateTime.UtcNow - t0).TotalSeconds:0.0}s ({catalog.Count} rows)");
        // The probe ran alongside the fan-out (self-bounded inside DiscoverAsync).
        AlbumCandidate? discovery = discoveryTask is null ? null : await discoveryTask.ConfigureAwait(false);
        lock (ctx)
        {
            ctx.CatalogSnapshot = catalog.ToList();
            ctx.Goal = GoalResolver.Resolve(parsed, catalog);
            // ARTIST MODE: a bare 1-token query ("fadaei") whose catalog rows
            // are overwhelmingly songs BY one artist means the user wants the
            // artist's catalog, not one song. Skipped in the forced Albums toggle.
            if (!albumsOnly && AlbumDetection.TryDetectArtistMode(parsed.Title, catalog, out var canonical))
            {
                ctx.ArtistMode = true;
                ctx.Goal = new GoalSong(canonical, "", null, catalog[0].Provider);
                if (DebugPhases) Console.WriteLine($"[phase] artist mode: {canonical}");
            }
            // ALBUM MODE: rows sharing an album that matches the query ("jahanam")
            // mean the user wants the whole album, not one song. The goal becomes
            // the album; every track of it passes the gate.
            else if (AlbumDetection.TryDetectAlbumMode(catalog, query, out var album))
            {
                ctx.StartAlbumMode(album, catalog);
                if (DebugPhases) Console.WriteLine($"[phase] album mode: {album.Name} ({album.Id})");
            }
            // The retrieval fan-out may have landed BEFORE the catalogs answered
            // (Persian albums live on RJ, not the catalogs) — their rows were
            // buffered pre-goal, so detect album mode on the buffer too.
            else if (AlbumDetection.TryDetectAlbumMode(ctx.Pending, query, out var albumPre))
            {
                ctx.StartAlbumMode(albumPre, ctx.Pending);
                if (DebugPhases) Console.WriteLine($"[phase] album mode (pre-goal): {albumPre.Name} ({albumPre.Id})");
            }
            // ALBUM DISCOVERY: catalogs lack Persian albums, so a YouTube playlist
            // probe is the last structured chance. Auto mode only flips when the
            // catalog did NOT confirm the goal as an album-track song ("coldplay
            // yellow") — empty, single-marked ("Jahanam - Single"), or artist
            // mismatch. The Albums toggle flips unconditionally; the discovered
            // playlist IS the album and its tracks gate in directly.
            else if (discovery is { } cand && cand.Tracks.Count >= (albumsOnly ? 2 : 3)
                     && (albumsOnly || !AlbumDetection.AlbumTrackConfirmed(ctx.Goal, catalog)))
            {
                ctx.StartAlbumModeFromDiscovery(cand);
                if (DebugPhases) Console.WriteLine($"[phase] album mode (discovery): {cand.Album.Name} ({cand.Tracks.Count} tracks)");
            }
            ctx.GoalReady = true;
            if (ctx.AlbumMode) ctx.PendingAlbum.AddRange(ctx.Pending); // StartAlbumMode already moved it; nothing new under the lock
            else ctx.GateInto(ctx.Pending);
            ctx.Pending.Clear();
        }
        if (ctx.AlbumMode)
            cb?.Status?.Invoke($"Album: “{ctx.Goal.Album}” · gathering tracks…");
        else if (ctx.ArtistMode)
            cb?.Status?.Invoke($"Artist: “{ctx.Goal.Artist}” · gathering songs…");
        else if (catalog.Count > 0)
            cb?.Status?.Invoke($"Matched “{ctx.Goal.Artist} — {ctx.Goal.Title}” · gathering copies…");
        ctx.EmitSnapshot("goal");

        // Album mode: expand the full track list (concurrent with the retrieval
        // fan-out) and gate the buffered rows against the album.
        if (ctx.AlbumMode)
            await ctx.ExpandAlbumAsync().ConfigureAwait(false);

        var retrievalCollected = await retrievalTask.ConfigureAwait(false);
        if (DebugPhases) Console.WriteLine($"[phase] retrieval +{(DateTime.UtcNow - t0).TotalSeconds:0.0}s ({retrievalCollected.Count} rows)");
        lock (ctx)
        {
            if (ctx.AlbumMode)
            {
                ctx.GateAlbumInto(retrievalCollected);
                // Second-chance album mode: catalogs empty AND no stream flip
                // fired (e.g. the album rows only appeared in the last batch).
                if (ctx.Retrieval.Count == 0
                    && AlbumDetection.TryDetectAlbumMode(retrievalCollected, query, out var album2))
                {
                    ctx.StartAlbumMode(album2, retrievalCollected);
                    if (DebugPhases) Console.WriteLine($"[phase] album mode (retrieval): {album2.Name} ({album2.Id})");
                }
            }
            else
            {
                ctx.GateInto(retrievalCollected);
                // Second-chance artist mode: artists scrubbed from the catalogs
                // (Tataloo) never produced catalog rows. If the retrieval rows
                // themselves are overwhelmingly by one artist matching the query,
                // re-gate in artist mode instead of showing nothing.
                if (!ctx.ArtistMode && ctx.Retrieval.Count == 0
                    && AlbumDetection.TryDetectArtistMode(parsed.Title, retrievalCollected, out var canonical2))
                {
                    ctx.ArtistMode = true;
                    ctx.Goal = new GoalSong(canonical2, "", null, ProviderId.Unknown);
                    if (DebugPhases) Console.WriteLine($"[phase] artist mode (retrieval): {canonical2}");
                    ctx.GateInto(retrievalCollected);
                }
            }
        }
        if (ctx.AlbumMode)
            await ctx.ExpandAlbumAsync().ConfigureAwait(false);
        ctx.EmitSnapshot("main");

        // 4. Guarantee: zero goal-matching copies → targeted YouTube fallback AND
        //    the first Iranian-site rescue round at the same time (round 1 is
        //    skipped when the sites already ran speculatively with this query).
        if (ctx.Retrieval.Count == 0)
        {
            // Album mode has no song rescue rounds — re-probe discovery with
            // album-oriented variants ("hagh album", "هق آلبوم").
            if (albumsOnly)
            {
                cb?.Status?.Invoke("Widening album search…");
                var cand2 = await ctx.DiscoverAsync(query, lenient: true, _rescueTimeout).ConfigureAwait(false);
                lock (ctx)
                {
                    if (cand2 is { } c2 && c2.Tracks.Count >= 2 && !ctx.AlbumMode)
                    {
                        ctx.StartAlbumModeFromDiscovery(c2);
                        if (DebugPhases) Console.WriteLine($"[phase] album mode (widen): {c2.Album.Name} ({c2.Tracks.Count} tracks)");
                    }
                }
                ctx.EmitSnapshot("album-widen");
            }
            else
            {
            var term = string.Join(" - ", new[] { ctx.Goal.Artist, ctx.Goal.Title }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var rescueProviders = _providers
                .Where(p => p.Tier == SearchTier.DownloadOnly
                            && p.Id != ProviderId.YtDlp
                            && !_health.IsQuiesced(p.Id))
                .ToList();

            cb?.Status?.Invoke("Widening search: YouTube fallback + Iranian sources…");
            var fbTask = term.Length == 0
                ? Task.FromResult(new List<SearchResult>())
                : _fanOut.CollectAsync(_fanOut.PlansFor(new[] { ProviderId.YouTube }, $"{ctx.Goal.Artist} {ctx.Goal.Title}".Trim(), 12), _providerTimeout, ct, null, onProviderStatus);
            var rescueTask = speculated.Count > 0 || rescueProviders.Count == 0 || string.IsNullOrWhiteSpace(persianVariant)
                ? Task.FromResult(new List<SearchResult>())
                : _fanOut.CollectAsync(rescueProviders.Select(p => (p, persianVariant, 5)).ToList(), _rescueTimeout, ct, null, onProviderStatus);

            var fb = await fbTask.ConfigureAwait(false);
            if (term.Length > 0 && QueryParser.Parse(term) is { HasExplicitStructure: true } fbParsed)
                ctx.ParsedRef = fbParsed;
            if (DebugPhases) Console.WriteLine($"[phase] fallback done +{(DateTime.UtcNow - t0).TotalSeconds:0.0}s ({fb.Count})");
            lock (ctx)
            {
                foreach (var r in fb)
                {
                    if (!r.Downloadable || r.PreviewOnly) continue;
                    if (!_gate.PassesLoose(r, ctx.Goal)) continue;
                    if (ctx.Seen.Add(r.DedupKey)) ctx.Retrieval.Add(r);
                }
            }
            ctx.EmitSnapshot("fallback");

            var rescued = await rescueTask.ConfigureAwait(false);
            if (DebugPhases) Console.WriteLine($"[phase] rescue '{persianVariant}' +{(DateTime.UtcNow - t0).TotalSeconds:0.0}s ({rescued.Count})");
            lock (ctx) ctx.GateInto(rescued, allowLoose: true);
            ctx.EmitSnapshot("rescue");

            // 4b. Round 2: the converted TITLE alone — artist conversions are
            //     often imperfect, titles are distinctive enough alone. Only pays
            //     off when the sites actually answered (round 1 or speculative).
            var sitesAnswered = rescued.Count > 0 || (speculated.Count > 0 && retrievalCollected.Count > 0);
            if (ctx.Retrieval.Count == 0 && sitesAnswered)
            {
                var titleVariant = ctx.Goal.Title is { Length: > 1 } && !TrackTextNormalizer.HasPersian(ctx.Goal.Title)
                    ? FinglishConverter.Convert(ctx.Goal.Title)
                    : ctx.Goal.Title ?? "";
                if (!string.IsNullOrWhiteSpace(titleVariant) && titleVariant != persianVariant)
                {
                    if (DebugPhases) Console.WriteLine($"[phase] rescue '{titleVariant}' +{(DateTime.UtcNow - t0).TotalSeconds:0.0}s");
                    var rescued2 = await _fanOut.CollectAsync(
                        rescueProviders.Select(p => (p, titleVariant, 5)).ToList(), _rescueTimeout, ct, null, onProviderStatus).ConfigureAwait(false);
                    lock (ctx) ctx.GateInto(rescued2, allowLoose: true);
                    if (DebugPhases) Console.WriteLine($"[phase] rescue2 done +{(DateTime.UtcNow - t0).TotalSeconds:0.0}s ({rescued2.Count})");
                }
            }

            // 4c. Mis-split recovery: a HEURISTIC artist/title split that matched
            //     nothing anywhere ("فدایی از کرج تا لنگه رود" split as artist
            //     "فدایی از") is usually wrong — re-gate every collected row
            //     against the full raw query as one phrase. Explicitness must be
            //     read from the ORIGINAL parse — the fallback re-parses its own
            //     "artist - title" term, which is trivially explicit.
            if (ctx.Retrieval.Count == 0 && !ctx.ArtistMode && !originalExplicit
                && !string.IsNullOrWhiteSpace(parsed.Raw))
            {
                if (DebugPhases) Console.WriteLine($"[phase] mis-split recovery +{(DateTime.UtcNow - t0).TotalSeconds:0.0}s");
                var allRows = new List<SearchResult>(retrievalCollected);
                allRows.AddRange(fb);
                allRows.AddRange(rescued);
                if (DebugPhases)
                    foreach (var row in allRows)
                        Console.WriteLine($"[resplit-candidate] [{row.Provider}] {row.Metadata.Artist} — {row.Metadata.Title}");
                ctx.ParsedRef = new ParsedQuery(parsed.Raw, null, parsed.Raw, null, null, false);
                lock (ctx)
                {
                    ctx.Goal = new GoalSong("", parsed.Raw, null, ProviderId.Unknown);
                    ctx.GateInto(allRows, allowLoose: true);
                }
                ctx.EmitSnapshot("resplit");
            }
            } // end else (song-mode rescue rounds)
        }

        // 5. Emit: goal-matching catalog rows are the visible works; gated copies
        //    attach as hidden download versions. No catalog → emit the copies.
        List<TrackWork> worksFinal;
        lock (ctx)
        {
            var catalogFinal = ctx.CatalogSnapshot?.ToList() ?? new List<SearchResult>();
            worksFinal = WorkAssembler.BuildWorks(catalogFinal, ctx.Retrieval.ToList(), ctx.ParsedRef, ctx.Goal, ctx.ArtistMode, ctx.AlbumMode);
        }
        worksFinal = worksFinal.Take(ctx.AlbumMode ? 40 : 30).ToList(); // albums run long — let the full track list through
        if (DebugPhases) Console.WriteLine($"[phase] emit {worksFinal.Count} +{(DateTime.UtcNow - t0).TotalSeconds:0.0}s");
        if (worksFinal.Count > 0)
        {
            _cache.Store(cacheKey, worksFinal);
            cb?.Status?.Invoke(ctx.AlbumMode
                ? $"Album: “{ctx.Goal.Album}” · {worksFinal.Count} track{(worksFinal.Count == 1 ? "" : "s")} · {(DateTime.UtcNow - t0).TotalSeconds:0.0}s"
                : $"{worksFinal.Count} result{(worksFinal.Count == 1 ? "" : "s")} · {(DateTime.UtcNow - t0).TotalSeconds:0.0}s");
        }
        else
        {
            cb?.Status?.Invoke(albumsOnly
                ? $"No albums found · {(DateTime.UtcNow - t0).TotalSeconds:0.0}s — try \"artist album\", or check the proxy (YouTube needs it on filtered networks)"
                : $"No matches · {(DateTime.UtcNow - t0).TotalSeconds:0.0}s");
        }
        return worksFinal;
    }

    /// <summary>Strict gate (MODERN-03): delegated to <see cref="GoalGate"/>.</summary>
    public static bool PassesGoalGate(SearchResult r, GoalSong goal) => GoalGate.PassesGoalGate(r, goal);

    /// <summary>Loose gate (MODERN-03): delegated to <see cref="GoalGate"/>.</summary>
    public static bool PassesLooseGate(SearchResult r, GoalSong goal) => GoalGate.PassesLooseGate(r, goal);

}
