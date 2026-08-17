namespace MusicEngine.Search;

using Microsoft.Extensions.Logging;
using Models;
using Text;

/// <summary>
/// THE search pipeline.
///
/// Query understanding (Finglish expansion + QueryParser) → CONCURRENT fan-out:
/// catalog providers (iTunes/Deezer) resolve the GOAL identity while retrieval
/// providers (YouTube/SoundCloud/RadioJavan) fetch candidate copies at the same
/// time → strict goal gate (cross-script artist+title matching + duration
/// sanity + junk filter) → work grouping + ranking → TrackWork stream.
///
/// Results STREAM: rows are gated and emitted as each provider answers, so the
/// UI shows the first hits at the speed of the fastest source. For Persian-ish
/// queries the slow Iranian sites join the initial fan-out instead of waiting
/// for a later rescue phase, and the zero-result path runs the YouTube fallback
/// and Iranian rescue concurrently instead of back-to-back.
/// </summary>
public sealed class SearchService
{
    /// <summary>Set true (tests/debug) to print phase timings to stdout.</summary>
    public static bool DebugPhases;

    private readonly IReadOnlyList<ISearchProvider> _providers;
    private readonly ProviderHealthMonitor _health;
    private readonly SearchResultCache _cache;
    private readonly ILogger<SearchService> _logger;
    private readonly TimeSpan _providerTimeout;
    private readonly TimeSpan _catalogTimeout;
    private readonly TimeSpan _rescueTimeout;

    public SearchService(
        IEnumerable<ISearchProvider> providers,
        ProviderHealthMonitor? health = null,
        SearchResultCache? cache = null,
        ILogger<SearchService>? logger = null,
        int searchTimeoutSeconds = 6)
    {
        _providers = providers.Where(p => p.IsAvailable).ToArray();
        _health = health ?? new ProviderHealthMonitor();
        _cache = cache ?? new SearchResultCache();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SearchService>.Instance;
        _providerTimeout = TimeSpan.FromSeconds(Math.Max(2, searchTimeoutSeconds));
        _catalogTimeout = TimeSpan.FromSeconds(Math.Min(5, Math.Max(2, searchTimeoutSeconds)));
        // The deep rescue path runs only when nothing faster worked; the scrapers
        // (python spawn + proxied fetches) need a little more headroom.
        _rescueTimeout = TimeSpan.FromSeconds(Math.Min(9, Math.Max(5, searchTimeoutSeconds + 2)));
    }

    /// <summary>Progress callbacks for streaming UI updates.</summary>
    public sealed class Callbacks
    {
        /// <summary>Human-readable status line ("Searching 7 sources…").</summary>
        public Action<string>? Status { get; init; }

        /// <summary>Emitted whenever the current work snapshot changes.</summary>
        public Action<IReadOnlyList<TrackWork>>? Batch { get; init; }

        public static Callbacks None { get; } = new();
    }

    public async IAsyncEnumerable<TrackWork> SearchAsync(
        string query,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var works = await RunAsync(query, null, ct).ConfigureAwait(false);
        foreach (var w in works) yield return w;
    }

    /// <summary>Full pipeline with streaming callbacks; returns the final works.</summary>
    public async Task<List<TrackWork>> RunAsync(string query, Callbacks? cb, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<TrackWork>();
        query = query.Trim();

        // 0. A pasted track URL (Spotify/YouTube/…) becomes a text query first.
        if (Uri.TryCreate(query, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            var resolved = await UrlQueryResolver.ResolveAsync(uri, ct).ConfigureAwait(false);
            if (resolved is null) return new List<TrackWork>();
            query = resolved;
        }

        // 1. Cache hit → instant.
        if (_cache.TryGet(query) is { } cached)
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

        var catalogPlans = BuildCatalogPlans(parsed, query);
        var retrievalPlans = BuildRetrievalPlans(query);
        // Speculative rescue: Iranian sites scrape slowly, so for Persian-ish
        // queries they join the FIRST fan-out instead of a later rescue phase —
        // their rows are just more candidates for the gate.
        var speculated = ShouldSpeculate(query)
            ? _providers.Where(p => p.Tier == SearchTier.DownloadOnly
                                    && p.Id != ProviderId.YtDlp
                                    && !_health.IsQuiesced(p.Id))
                        .Select(p => (p, persianVariant, 5))
                        .ToList()
            : new List<(ISearchProvider, string, int)>();
        retrievalPlans.AddRange(speculated);

        // 3. Concurrent fan-out: catalogs resolve the goal WHILE retrieval runs.
        //    Retrieval rows that land before the goal is known wait in a buffer.
        var t0 = DateTime.UtcNow;
        cb?.Status?.Invoke($"Searching {catalogPlans.Count + retrievalPlans.Count} sources…");

        var retrieval = new List<SearchResult>();
        var pending = new List<SearchResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var gateLock = new object();
        var goalReady = false;
        List<SearchResult>? catalogSnapshot = null;
        GoalSong goal = new(parsed.Artist ?? "", parsed.Title ?? "", null, ProviderId.Unknown);
        ParsedQuery parsedRef = parsed;
        var emittedSignature = "";
        var artistMode = false;

        /// <summary>
        /// True when ≥3 rows share one artist matching the bare query token —
        /// the signature of an artist-catalog query rather than a song query.
        /// </summary>
        static bool TryDetectArtistMode(string? bareToken, IReadOnlyList<SearchResult> rows, out string canonicalArtist)
        {
            canonicalArtist = "";
            if (string.IsNullOrWhiteSpace(bareToken)) return false;
            var best = rows
                .Where(r => GoalResolver.IsSongLikeDuration(r.Metadata.Duration))
                .GroupBy(r => TrackTextNormalizer.Normalize(r.Metadata.Artist ?? ""))
                .Where(g => g.Key.Length > 0
                            && g.Count() >= 3
                            && g.All(r => TrackTextNormalizer.KeysOverlap(r.Metadata.Artist ?? "", bareToken)
                                          || TrackTextNormalizer.ContainsAllTokens(r.Metadata.Artist ?? "", bareToken)
                                          || TrackTextNormalizer.ContainsPhraseSpaceless(r.Metadata.Artist ?? "", bareToken)))
                .OrderByDescending(g => g.Count())
                .FirstOrDefault();
            if (best is null) return false;
            canonicalArtist = best.First().Metadata.Artist ?? bareToken;
            return true;
        }

        void GateInto(IReadOnlyList<SearchResult> rows, bool allowLoose = false, string phase = "")
        {
            foreach (var r in rows)
            {
                if (!r.Downloadable || r.PreviewOnly) continue;
                var strict = PassesGoalGate(r, goal);
                var loose = !strict && allowLoose && PassesLooseGate(r, goal);
                if (!strict && !loose) continue;
                if (seen.Add(r.DedupKey))
                {
                    retrieval.Add(r);
                    if (DebugPhases)
                        Console.WriteLine($"[gate] +{phase} {(strict ? "strict" : "LOOSE")} [{r.Provider}] {r.Metadata.Artist} — {r.Metadata.Title}");
                }
            }
        }

        void OnRetrievalRows(SearchResult[] rows)
        {
            lock (gateLock)
            {
                if (goalReady) GateInto(rows);
                else pending.AddRange(rows);
            }
            if (goalReady) EmitSnapshot("live");
        }

        // Stream: emit a fresh snapshot whenever gated rows change.
        void EmitSnapshot(string phaseNote)
        {
            if (cb is null) return;
            List<SearchResult> retrievalCopy, catalogCopy;
            lock (gateLock)
            {
                if (retrieval.Count == 0) return;
                retrievalCopy = retrieval.ToList();
                catalogCopy = catalogSnapshot?.ToList() ?? new List<SearchResult>();
            }
            var sig = string.Join("|", retrievalCopy.Select(r => r.DedupKey));
            if (sig == emittedSignature) return;
            emittedSignature = sig;
            var works = BuildWorks(catalogCopy, retrievalCopy, parsedRef, goal, artistMode);
            if (works.Count == 0) return;
            cb.Batch?.Invoke(works);
            if (DebugPhases)
                Console.WriteLine($"[phase] stream {phaseNote}: {works.Count} works +{(DateTime.UtcNow - t0).TotalSeconds:0.0}s");
        }

        var catalogTask = CollectAsync(catalogPlans, _catalogTimeout, ct);
        var retrievalTask = CollectAsync(retrievalPlans, _providerTimeout, ct, OnRetrievalRows);

        var catalog = await catalogTask.ConfigureAwait(false);
        if (DebugPhases) Console.WriteLine($"[phase] catalog +{(DateTime.UtcNow - t0).TotalSeconds:0.0}s ({catalog.Count} rows)");
        lock (gateLock)
        {
            catalogSnapshot = catalog.ToList();
            goal = GoalResolver.Resolve(parsed, catalog);
            // ARTIST MODE: a bare 1-token query ("fadaei") whose catalog rows
            // are overwhelmingly songs BY one artist means the user wants the
            // artist's catalog, not one song. The goal becomes the artist with
            // an empty title — the gate then passes every song by that artist.
            if (TryDetectArtistMode(parsed.Title, catalog, out var canonical))
            {
                artistMode = true;
                goal = new GoalSong(canonical, "", null, catalog[0].Provider);
                if (DebugPhases) Console.WriteLine($"[phase] artist mode: {canonical}");
            }
            goalReady = true;
            GateInto(pending);
            pending.Clear();
        }
        if (artistMode)
            cb?.Status?.Invoke($"Artist: “{goal.Artist}” · gathering songs…");
        else if (catalog.Count > 0)
            cb?.Status?.Invoke($"Matched “{goal.Artist} — {goal.Title}” · gathering copies…");
        EmitSnapshot("goal");

        var retrievalCollected = await retrievalTask.ConfigureAwait(false);
        if (DebugPhases) Console.WriteLine($"[phase] retrieval +{(DateTime.UtcNow - t0).TotalSeconds:0.0}s ({retrievalCollected.Count} rows)");
        lock (gateLock)
        {
            GateInto(retrievalCollected);
            // Second-chance artist mode: artists scrubbed from the catalogs
            // (Tataloo) never produced catalog rows. If the retrieval rows
            // themselves are overwhelmingly by one artist matching the query,
            // re-gate in artist mode instead of showing nothing.
            if (!artistMode && retrieval.Count == 0
                && TryDetectArtistMode(parsed.Title, retrievalCollected, out var canonical2))
            {
                artistMode = true;
                goal = new GoalSong(canonical2, "", null, ProviderId.Unknown);
                if (DebugPhases) Console.WriteLine($"[phase] artist mode (retrieval): {canonical2}");
                GateInto(retrievalCollected);
            }
        }
        EmitSnapshot("main");

        // 4. Guarantee: zero goal-matching copies → targeted YouTube fallback AND
        //    the first Iranian-site rescue round AT THE SAME TIME (they used to
        //    run back-to-back and could cost 12+ seconds). Round 1 is skipped
        //    when the Iranian sites already ran speculatively with this query.
        if (retrieval.Count == 0)
        {
            var term = string.Join(" - ", new[] { goal.Artist, goal.Title }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var rescueProviders = _providers
                .Where(p => p.Tier == SearchTier.DownloadOnly
                            && p.Id != ProviderId.YtDlp
                            && !_health.IsQuiesced(p.Id))
                .ToList();

            cb?.Status?.Invoke("Widening search: YouTube fallback + Iranian sources…");
            var fbTask = term.Length == 0
                ? Task.FromResult(new List<SearchResult>())
                : CollectAsync(PlansFor(new[] { ProviderId.YouTube }, $"{goal.Artist} {goal.Title}".Trim(), 12), _providerTimeout, ct);
            var rescueTask = speculated.Count > 0 || rescueProviders.Count == 0 || string.IsNullOrWhiteSpace(persianVariant)
                ? Task.FromResult(new List<SearchResult>())
                : CollectAsync(rescueProviders.Select(p => (p, persianVariant, 5)).ToList(), _rescueTimeout, ct);

            var fb = await fbTask.ConfigureAwait(false);
            if (term.Length > 0 && QueryParser.Parse(term) is { HasExplicitStructure: true } fbParsed)
                parsedRef = fbParsed;
            if (DebugPhases) Console.WriteLine($"[phase] fallback done +{(DateTime.UtcNow - t0).TotalSeconds:0.0}s ({fb.Count})");
            lock (gateLock)
            {
                foreach (var r in fb)
                {
                    if (!r.Downloadable || r.PreviewOnly) continue;
                    if (!PassesLooseGate(r, goal)) continue;
                    if (seen.Add(r.DedupKey)) retrieval.Add(r);
                }
            }
            EmitSnapshot("fallback");

            var rescued = await rescueTask.ConfigureAwait(false);
            if (DebugPhases) Console.WriteLine($"[phase] rescue '{persianVariant}' +{(DateTime.UtcNow - t0).TotalSeconds:0.0}s ({rescued.Count})");
            lock (gateLock) GateInto(rescued, allowLoose: true);
            EmitSnapshot("rescue");

            // 4b. Round 2: the converted TITLE alone — artist conversions are often
            //     imperfect ("mehrzad"→"مهرزد"), titles are distinctive enough alone.
            //     Only pays off when the sites are alive (round 1 or the speculative
            //     pass actually returned rows); a second round against dead sources
            //     would burn time to fail the same way.
            var sitesAnswered = rescued.Count > 0 || (speculated.Count > 0 && retrievalCollected.Count > 0);
            if (retrieval.Count == 0 && sitesAnswered)
            {
                var titleVariant = goal.Title is { Length: > 1 } && !TrackTextNormalizer.HasPersian(goal.Title)
                    ? FinglishConverter.Convert(goal.Title)
                    : goal.Title ?? "";
                if (!string.IsNullOrWhiteSpace(titleVariant) && titleVariant != persianVariant)
                {
                    if (DebugPhases) Console.WriteLine($"[phase] rescue '{titleVariant}' +{(DateTime.UtcNow - t0).TotalSeconds:0.0}s");
                    var rescued2 = await CollectAsync(
                        rescueProviders.Select(p => (p, titleVariant, 5)).ToList(), _rescueTimeout, ct).ConfigureAwait(false);
                    lock (gateLock) GateInto(rescued2, allowLoose: true);
                    if (DebugPhases) Console.WriteLine($"[phase] rescue2 done +{(DateTime.UtcNow - t0).TotalSeconds:0.0}s ({rescued2.Count})");
                }
            }

            // 4c. Mis-split recovery: a HEURISTIC artist/title split that matched
            //     nothing anywhere ("فدایی از کرج تا لنگه رود" split as artist
            //     "فدایی از", "zedbazi tehran jasbi" as artist "zedbazi tehran")
            //     is usually wrong — re-gate every collected row against the
            //     full raw query as one phrase. NOTE: explicitness must be read
            //     from the ORIGINAL parse — the fallback above re-parses its own
            //     "artist - title" term, which is trivially explicit.
            if (retrieval.Count == 0 && !artistMode && !originalExplicit
                && !string.IsNullOrWhiteSpace(parsed.Raw))
            {
                if (DebugPhases) Console.WriteLine($"[phase] mis-split recovery +{(DateTime.UtcNow - t0).TotalSeconds:0.0}s");
                var allRows = new List<SearchResult>(retrievalCollected);
                allRows.AddRange(fb);
                allRows.AddRange(rescued);
                if (DebugPhases)
                    foreach (var row in allRows)
                        Console.WriteLine($"[resplit-candidate] [{row.Provider}] {row.Metadata.Artist} — {row.Metadata.Title}");
                parsedRef = new ParsedQuery(parsed.Raw, null, parsed.Raw, null, null, false);
                lock (gateLock)
                {
                    goal = new GoalSong("", parsed.Raw, null, ProviderId.Unknown);
                    GateInto(allRows, allowLoose: true);
                }
                EmitSnapshot("resplit");
            }
        }

        // 5. Emit: goal-matching catalog rows are the visible works; gated copies
        //    attach as hidden download versions. No catalog → emit the copies.
        List<TrackWork> worksFinal;
        lock (gateLock)
        {
            var catalogFinal = catalogSnapshot?.ToList() ?? new List<SearchResult>();
            worksFinal = BuildWorks(catalogFinal, retrieval.ToList(), parsedRef, goal, artistMode);
        }
        worksFinal = worksFinal.Take(30).ToList();
        if (DebugPhases) Console.WriteLine($"[phase] emit {worksFinal.Count} +{(DateTime.UtcNow - t0).TotalSeconds:0.0}s");
        if (worksFinal.Count > 0)
        {
            _cache.Store(query, worksFinal);
            cb?.Status?.Invoke($"{worksFinal.Count} result{(worksFinal.Count == 1 ? "" : "s")} · {(DateTime.UtcNow - t0).TotalSeconds:0.0}s");
        }
        else
        {
            cb?.Status?.Invoke($"No matches · {(DateTime.UtcNow - t0).TotalSeconds:0.0}s");
        }
        return worksFinal;
    }

    /// <summary>
    /// Persian-ish query → the slow Iranian tiers may pay off, so they run from
    /// the start. Latin queries skip them (scrapers would only return junk that
    /// the gate rejects anyway).
    /// </summary>
    private static bool ShouldSpeculate(string rawQuery) =>
        TrackTextNormalizer.HasPersian(rawQuery)
        || (rawQuery.Any(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z') && LooksLikeFinglish(rawQuery));

    /// <summary>
    /// Cheap heuristic: the Finglish conversion of the whole query must come out
    /// ≥60% Persian letters. "fadaei azkaraj" passes; "coldplay yellow" fails.
    /// </summary>
    private static bool LooksLikeFinglish(string rawQuery)
    {
        var words = rawQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words.Any(w => w.Length > 12)) return false;
        var converted = FinglishConverter.Convert(rawQuery);
        if (!TrackTextNormalizer.HasPersian(converted)) return false;
        var letters = converted.Replace(" ", "").Length;
        if (letters == 0) return false;
        return converted.Count(TrackTextNormalizer.IsPersianChar) * 100 / letters >= 60;
    }

    /// <summary>The strict gate: only the exact searched song passes.</summary>
    public static bool PassesGoalGate(SearchResult r, GoalSong goal)
    {
        var title = r.Metadata.Title ?? "";
        var artist = r.Metadata.Artist ?? "";
        if (JunkFilter.IsJunkTitle(title) || JunkFilter.IsJunkChannel(r.SourceUrl)) return false;
        if (string.IsNullOrWhiteSpace(goal.Artist) && string.IsNullOrWhiteSpace(goal.Title)) return false;

        var direct = FieldMatch(artist, goal.Artist) && FieldMatch(title, goal.Title);
        // Swapped (site-style "title - artist") must be EXACT: fuzzy/substring
        // in the cross-field direction lets "deejay benyamin — …shadmehr…"
        // pass for the goal (shadmehr, deejad) via deejay≈deejad.
        var swapped = FieldMatchExact(artist, goal.Title) && FieldMatchExact(title, goal.Artist);
        // Iranian index posts carry everything in the title ("مهرزاد منو نترسون",
        // no artist field) — match the combined text as a last resort. The TITLE
        // side must be non-fuzzy: the deejay-benjy "…shadmehr… deejad…" mixes
        // slip through when a one-edit title match is enough.
        var combined = $"{artist} {title}".Trim();
        var combinedPass = combined.Length > 0
            && FieldMatch(combined, goal.Artist)
            && FieldMatchExact(combined, goal.Title);
        if (!direct && !swapped && !combinedPass) return false;

        return DurationPasses(r, goal);

        // KeysOverlap = equality across scripts; token containment handles
        // "Amir Tataloo, Sami" (Radio Javan style) and compound titles;
        // spaceless phrases catch glued-word queries ("azkaraj" → "ازکرج").
        // An empty NEEDLE is a wildcard, but an empty HAYSTACK can never match —
        // otherwise every empty-artist scraper row passes the swapped check.
        static bool FieldMatch(string haystack, string needle)
        {
            if (string.IsNullOrWhiteSpace(needle)) return true;
            if (string.IsNullOrWhiteSpace(haystack)) return false;
            return TrackTextNormalizer.KeysOverlap(haystack, needle)
                || TrackTextNormalizer.ContainsAllTokens(haystack, needle)
                || TrackTextNormalizer.ContainsAllTokens(needle, haystack)
                || TrackTextNormalizer.ContainsPhraseSpaceless(haystack, needle)
                || TrackTextNormalizer.ContainsPhraseSpaceless(needle, haystack);
        }

        static bool FieldMatchExact(string haystack, string needle)
        {
            if (string.IsNullOrWhiteSpace(needle)) return true;
            if (string.IsNullOrWhiteSpace(haystack)) return false;
            return TrackTextNormalizer.KeysOverlap(haystack, needle)
                || TrackTextNormalizer.ContainsAllTokens(haystack, needle, fuzzy: false, substring: false)
                || TrackTextNormalizer.ContainsPhraseSpaceless(haystack, needle);
        }
    }

    /// <summary>
    /// Loose gate for the guaranteed fallback/rescue: junk + duration sanity, and
    /// the TITLE must be textually relevant to the goal — with EXACT token
    /// semantics (equality/conversion-equality/phrase only, no substrings, no
    /// fuzzy): "fereshte" must not ride in on "Fereshteh", and a single drifting
    /// token must not admit an unrelated song.
    /// </summary>
    public static bool PassesLooseGate(SearchResult r, GoalSong goal)
    {
        var title = r.Metadata.Title ?? "";
        if (JunkFilter.IsJunkTitle(title) || JunkFilter.IsJunkChannel(r.SourceUrl)) return false;
        if (!GoalResolver.IsSongLikeDuration(r.Metadata.Duration)) return false;
        if (r.Metadata.Duration is { TotalSeconds: < 60 }) return false;
        if (!string.IsNullOrWhiteSpace(goal.Title))
        {
            return TrackTextNormalizer.KeysOverlap(title, goal.Title)
                || TrackTextNormalizer.ContainsPhraseSpaceless(title, goal.Title)
                || TrackTextNormalizer.ContainsAllTokens(title, goal.Title, fuzzy: false, substring: false);
        }
        // Artist-only query: fall back to artist relevance.
        return TrackTextNormalizer.KeysOverlap(r.Metadata.Artist ?? "", goal.Artist)
            || TrackTextNormalizer.ContainsAllTokens(r.Metadata.Artist ?? "", goal.Artist, fuzzy: false, substring: false);
    }

    private static bool DurationPasses(SearchResult r, GoalSong goal)
    {
        var rd = r.Metadata.Duration;
        if (goal.Duration is { } g && rd is { } d)
        {
            var gSec = g.TotalSeconds;
            return gSec <= 0 || Math.Abs(d.TotalSeconds - gSec) / gSec <= 0.35;
        }
        // No goal duration (Persian track absent from catalogs): reject clips (<60s)
        // and absurd uploads (>20min).
        if (rd is { } dur)
            return dur.TotalSeconds >= 60 && dur.TotalSeconds <= 20 * 60;
        return true;
    }

    private static TimeSpan? MedianDuration(IReadOnlyList<SearchResult> results)
    {
        var durations = results
            .Select(r => r.Metadata.Duration)
            .Where(d => d is { TotalSeconds: > 0 })
            .Select(d => d!.Value.TotalSeconds)
            .OrderBy(x => x)
            .ToArray();
        return durations.Length > 0 ? TimeSpan.FromSeconds(durations[durations.Length / 2]) : null;
    }

    // ---------- emit ----------

    private static List<TrackWork> BuildWorks(
        IReadOnlyList<SearchResult> catalog,
        List<SearchResult> retrieval,
        ParsedQuery parsed,
        GoalSong goal,
        bool artistMode = false)
    {
        var works = new List<TrackWork>();
        var versions = retrieval
            .Select(r => new TrackVersion(r, Ranker.VersionLabel(r), Ranker.Score(r, parsed, MedianDuration(retrieval))))
            .OrderByDescending(v => v.Score)
            .ToList();

        if (artistMode && !string.IsNullOrWhiteSpace(goal.Artist))
        {
            // Artist catalog: every distinct song by the artist becomes a work;
            // each version attaches to the work whose TITLE it matches (in song
            // mode the gate guarantees single-song versions; here it doesn't).
            var seenWorks = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in catalog
                         .Where(r => TrackTextNormalizer.KeysOverlap(r.Metadata.Artist ?? "", goal.Artist)
                                     && GoalResolver.IsSongLikeDuration(r.Metadata.Duration)))
            {
                var key = TrackTextNormalizer.Normalize(row.Metadata.Title ?? "");
                if (key.Length == 0 || !seenWorks.Add(key)) continue;
                var mine = versions
                    .Where(v => TrackTextNormalizer.KeysOverlap(v.Result.Metadata.Title ?? "", row.Metadata.Title ?? "")
                                || TrackTextNormalizer.ContainsAllTokens(v.Result.Metadata.Title ?? "", row.Metadata.Title ?? ""))
                    .ToList();
                works.Add(new TrackWork(
                    row.Metadata.Title,
                    row.Metadata.Artist,
                    row,
                    mine.Count > 0 ? mine : new List<TrackVersion> { new(row, "Catalog", 0.5) },
                    goal));
                if (works.Count >= 25) break;
            }
            // Gated copies whose song isn't in the catalog (SC-only tracks for a
            // scrubbed artist) still deserve rows.
            var attached = works.SelectMany(w => w.Versions.Select(v => v.Result.DedupKey))
                .ToHashSet(StringComparer.Ordinal);
            var orphans = retrieval.Where(r => !attached.Contains(r.DedupKey)).ToList();
            if (orphans.Count > 0)
                works.AddRange(WorkGrouper.Group(orphans, parsed, MedianDuration(orphans), goal));
            return works;
        }

        var matchingCatalog = catalog
            .Where(r => TrackTextNormalizer.KeysOverlap(r.Metadata.Artist ?? "", goal.Artist)
                     && TrackTextNormalizer.KeysOverlap(r.Metadata.Title ?? "", goal.Title)
                     && GoalResolver.IsSongLikeDuration(r.Metadata.Duration))
            .ToList();

        if (matchingCatalog.Count > 0)
        {
            foreach (var row in matchingCatalog)
            {
                works.Add(new TrackWork(
                    row.Metadata.Title,
                    row.Metadata.Artist,
                    row,
                    versions.Count > 0 ? versions : new List<TrackVersion> { new(row, "Catalog", 0.5) },
                    goal));
            }
        }
        else if (retrieval.Count > 0)
        {
            works.AddRange(WorkGrouper.Group(retrieval, parsed, MedianDuration(retrieval), goal));
            // Grouping is title-similarity only — when the goal names an
            // artist, works BY that artist must outrank covers of it.
            if (!string.IsNullOrWhiteSpace(goal.Artist))
                works = works
                    .OrderByDescending(w => TrackTextNormalizer.KeysOverlap(w.Artist ?? "", goal.Artist)
                                            || TrackTextNormalizer.ContainsAllTokens(w.Artist ?? "", goal.Artist))
                    .ToList();
        }
        return works;
    }

    // ---------- planning ----------

    private List<(ISearchProvider Provider, string Query, int Max)> BuildCatalogPlans(ParsedQuery parsed, string raw)
    {
        var plans = new List<(ISearchProvider, string, int)>();
        foreach (var p in _providers.Where(p => p.Tier == SearchTier.Catalog && !_health.IsQuiesced(p.Id)))
        {
            // Fielded queries when the query parsed into artist+title; iTunes/Deezer
            // rank fielded queries far better than a bag of words.
            var query = parsed.Artist is { } a && parsed.Title is { } t
                ? (p.Id == ProviderId.Deezer ? $"artist:\"{a}\" track:\"{t}\"" : $"\"{a}\" \"{t}\"")
                : raw;
            plans.Add((p, query, 25));
        }
        return plans;
    }

    private List<(ISearchProvider Provider, string Query, int Max)> BuildRetrievalPlans(string raw) =>
        _providers
            .Where(p => p.Tier == SearchTier.Display && !_health.IsQuiesced(p.Id))
            .Select(p => (p, raw, p.Id == ProviderId.RadioJavan ? 25 : 15))
            .ToList();

    private List<(ISearchProvider Provider, string Query, int Max)> PlansFor(
        IReadOnlyList<ProviderId> ids, string query, int max) =>
        _providers
            .Where(p => ids.Contains(p.Id) && !_health.IsQuiesced(p.Id))
            .Select(p => (p, query, max))
            .ToList();

    // ---------- fan-out ----------

    private async Task<List<SearchResult>> CollectAsync(
        IReadOnlyList<(ISearchProvider Provider, string Query, int Max)> plans,
        TimeSpan timeout,
        CancellationToken ct,
        Action<SearchResult[]>? onBatch = null)
    {
        var results = new List<SearchResult>();
        var tasks = plans.Select(plan => Task.Run(async () =>
        {
            var itemResults = new List<SearchResult>();
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(timeout);
                await foreach (var item in plan.Provider.SearchAsync(plan.Query, plan.Max, timeoutCts.Token)
                                   .ConfigureAwait(false))
                    itemResults.Add(item);
                _health.RecordSuccess(plan.Provider.Id);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _health.RecordFailure(plan.Provider.Id, "timeout");
                _logger.LogDebug("Provider {Provider} timed out after {Sec}s ({Query})",
                    plan.Provider.Id, timeout.TotalSeconds, plan.Query);
            }
            catch (Exception ex)
            {
                _health.RecordFailure(plan.Provider.Id, ex.Message);
                _logger.LogWarning("Provider {Provider} search failed: {Msg}", plan.Provider.Id, ex.Message);
            }
            lock (results)
            {
                results.AddRange(itemResults);
                if (onBatch is not null && itemResults.Count > 0)
                    onBatch(results.ToArray());
            }
        }, ct)).ToArray();

        if (tasks.Length == 0) return results;

        // HARD DEADLINE: a provider library that ignores its cancellation token
        // (observed with YoutubeExplode under flaky proxies) must never stall the
        // whole search — abandon stragglers and keep whatever already landed.
        var grace = timeout + timeout + TimeSpan.FromSeconds(4);
        try
        {
            await Task.WhenAll(tasks).WaitAsync(grace, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Fan-out grace deadline hit after {Sec}s; continuing with partial results",
                grace.TotalSeconds);
        }

        lock (results) return results.ToList();
    }
}
