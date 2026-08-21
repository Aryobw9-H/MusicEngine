namespace MusicEngine.Search;

using Microsoft.Extensions.Logging;
using Models;
using Text;

/// <summary>
/// Mutable state + gating/emitting helpers of one <see cref="SearchService.RunAsync"/>
/// pass. The old code kept these as closures over ~15 locals; once album search
/// (detection + discovery + expansion) grew the helper set, the closures had to
/// become a class to keep <see cref="SearchService"/> under the 400-line gate.
/// </summary>
internal sealed class SearchRunContext
{
    private readonly IGoalGate _gate;
    private readonly IReadOnlyList<ISearchProvider> _providers;
    private readonly IReadOnlyList<IAlbumDiscovery> _discovery;
    private readonly SearchService.Callbacks? _cb;
    private readonly DateTime _t0;
    private readonly CancellationToken _ct;

    public List<SearchResult> Retrieval { get; } = new();
    public List<SearchResult> Pending { get; } = new();
    public List<SearchResult> PendingAlbum { get; } = new();
    public HashSet<string> Seen { get; } = new(StringComparer.Ordinal);
    public GoalSong Goal { get; set; }
    public ParsedQuery ParsedRef { get; set; }
    public List<SearchResult>? CatalogSnapshot { get; set; }
    public bool ArtistMode { get; set; }
    public bool AlbumMode { get; set; }
    public AlbumRef? AlbumRef { get; set; }
    public Task<IReadOnlyList<SearchResult>>? AlbumExpansion { get; set; }
    public List<string> KnownAlbumTitles { get; } = new();
    public bool GoalReady { get; set; }
    private string _emittedSignature = "";

    public SearchRunContext(
        IGoalGate gate,
        IEnumerable<ISearchProvider> providers,
        SearchService.Callbacks? cb,
        GoalSong goal,
        ParsedQuery parsedRef,
        CancellationToken ct)
    {
        _gate = gate;
        _providers = providers.ToArray();
        _discovery = _providers.OfType<IAlbumDiscovery>().ToArray();
        _cb = cb;
        Goal = goal;
        ParsedRef = parsedRef;
        _ct = ct;
        _t0 = DateTime.UtcNow;
    }

    public TimeSpan Elapsed => DateTime.UtcNow - _t0;

    /// <summary>Probe every discovery source (YouTube playlists) for the query.
    /// Lenient mode retries with album-oriented variants ("hagh album",
    /// "هق آلبوم", "hagh full album") — a raw playlist search often misses the
    /// album when the title doesn't repeat the artist. Each variant is bounded
    /// so a slow search can't stall the pipeline.</summary>
    public async Task<AlbumCandidate?> DiscoverAsync(string query, bool lenient, TimeSpan bound)
    {
        var variants = lenient
            ? new[] { query, $"{query} album", $"{query} آلبوم", $"{query} full album" }
            : new[] { query };
        foreach (var v in variants)
        {
            var probe = Task.WhenAll(_discovery.Select(p => p.FindAlbumAsync(v, _ct)).ToArray());
            var winner = await Task.WhenAny(probe, Task.Delay(bound, _ct)).ConfigureAwait(false);
            if (winner != probe) return null; // this variant exceeded the budget
            var hit = (await probe.ConfigureAwait(false)).FirstOrDefault(c => c is not null);
            if (hit is not null) return hit;
        }
        return null;
    }

    public void GateInto(IReadOnlyList<SearchResult> rows, bool allowLoose = false, string phase = "")
    {
        foreach (var r in rows)
        {
            if (!r.Downloadable || r.PreviewOnly) continue;
            var strict = _gate.PassesStrict(r, Goal);
            var loose = !strict && allowLoose && _gate.PassesLoose(r, Goal);
            if (!strict && !loose) continue;
            if (Seen.Add(r.DedupKey))
            {
                Retrieval.Add(r);
                if (SearchService.DebugPhases)
                    Console.WriteLine($"[gate] +{phase} {(strict ? "strict" : "LOOSE")} [{r.Provider}] {r.Metadata.Artist} — {r.Metadata.Title}");
            }
        }
    }

    /// <summary>Album gate: rows pass by album identity (id/name), or — for
    /// scraped rows with no album field — by artist + known album track title.</summary>
    public void GateAlbumInto(IReadOnlyList<SearchResult> rows)
    {
        var album = AlbumRef!;
        foreach (var r in rows)
        {
            if (!r.Downloadable || r.PreviewOnly) continue;
            var albumOk = !string.IsNullOrWhiteSpace(r.Metadata.Album)
                && WorkAssembler.AlbumMatches(r.Metadata.Album, album.Name);
            var artistOk = !string.IsNullOrWhiteSpace(r.Metadata.Artist)
                && (TrackTextNormalizer.KeysOverlap(r.Metadata.Artist, album.Artist)
                    || TrackTextNormalizer.ContainsAllTokens(r.Metadata.Artist, album.Artist));
            // The row must BE an album track: equal (cross-script), or the
            // album track's full title contained in the row (scraped rows add
            // junk suffixes). The reverse direction (row title is a TOKEN of
            // the track) is what let a single "Man" masquerade as the track
            // "Man Bahet Ghahram".
            var titleOk = KnownAlbumTitles.Count > 0
                && KnownAlbumTitles.Any(t => TrackTextNormalizer.KeysOverlap(t, r.Metadata.Title ?? "")
                    || TrackTextNormalizer.ContainsAllTokens(r.Metadata.Title ?? "", t, fuzzy: false, substring: true));
            if (!albumOk && !(artistOk && titleOk)) continue;
            if (Seen.Add(r.DedupKey)) Retrieval.Add(r);
        }
    }

    public void StartAlbumMode(AlbumRef album, IReadOnlyList<SearchResult> triggerRows)
    {
        AlbumMode = true;
        AlbumRef = album;
        Goal = new GoalSong(album.Artist, "", null, album.Provider, album.Name);
        if (_providers.OfType<IAlbumProvider>().FirstOrDefault(p => p.Id == album.Provider) is { } ap)
            AlbumExpansion = ap.GetAlbumTracksAsync(album, _ct);
        // Rows buffered before the flip are album candidates too.
        PendingAlbum.AddRange(Pending);
        Pending.Clear();
        // Seed known titles from the trigger rows so scraped copies can
        // attach even before the expansion lands.
        if (KnownAlbumTitles.Count == 0)
            KnownAlbumTitles.AddRange(triggerRows
                .Where(r => string.Equals(r.Metadata.AlbumId, album.Id, StringComparison.OrdinalIgnoreCase)
                            || TrackTextNormalizer.Normalize(r.Metadata.Album ?? "") == TrackTextNormalizer.Normalize(album.Name))
                .Select(r => r.Metadata.Title ?? "")
                .Where(t => t.Length > 0));
    }

    /// <summary>Discovery flip: the playlist IS the album — no expansion round,
    /// the tracks are the catalog, and they're gated in directly.</summary>
    public void StartAlbumModeFromDiscovery(AlbumCandidate cand)
    {
        AlbumMode = true;
        AlbumRef = cand.Album;
        Goal = new GoalSong(cand.Album.Artist, "", null, cand.Album.Provider, cand.Album.Name);
        CatalogSnapshot = cand.Tracks.ToList();
        if (KnownAlbumTitles.Count == 0)
            KnownAlbumTitles.AddRange(cand.Tracks
                .Select(t => t.Metadata.Title ?? "")
                .Where(t => t.Length > 0));
        foreach (var t in cand.Tracks)
            if (Seen.Add(t.DedupKey)) Retrieval.Add(t);
    }

    /// <summary>Expand the album (bounded budget — the detection rows are the
    /// fallback track list) and gate everything buffered so far.</summary>
    public async Task ExpandAlbumAsync()
    {
        IReadOnlyList<SearchResult> expanded = new List<SearchResult>();
        if (AlbumExpansion is not null)
        {
            var winner = await Task.WhenAny(AlbumExpansion,
                Task.Delay(TimeSpan.FromSeconds(5), _ct)).ConfigureAwait(false);
            if (winner == AlbumExpansion)
                expanded = await AlbumExpansion.ConfigureAwait(false);
        }
        lock (this)
        {
            if (expanded.Count > 0)
            {
                CatalogSnapshot = expanded.ToList();
                if (KnownAlbumTitles.Count == 0)
                    KnownAlbumTitles.AddRange(expanded
                        .Select(r => r.Metadata.Title ?? "")
                        .Where(t => t.Length > 0));
            }
            GateAlbumInto(PendingAlbum);
            PendingAlbum.Clear();
            GateAlbumInto(Pending);
            Pending.Clear();
        }
        EmitSnapshot("album");
        if (SearchService.DebugPhases)
            Console.WriteLine($"[phase] album expanded +{Elapsed.TotalSeconds:0.0}s ({CatalogSnapshot?.Count ?? 0} tracks)");
    }

    public void OnRetrievalRows(SearchResult[] rows, string query)
    {
        lock (this)
        {
            if (!GoalReady) { Pending.AddRange(rows); return; }
            // Album queries flip mid-stream too: catalogs lack most Persian
            // albums, so the RJ fan-out is what surfaces them. Skip once
            // artist mode claimed the query.
            if (!AlbumMode && !ArtistMode && Retrieval.Count == 0
                && AlbumDetection.TryDetectAlbumMode(rows, query, out var album))
            {
                StartAlbumMode(album, rows);
                if (SearchService.DebugPhases) Console.WriteLine($"[phase] album mode (stream): {album.Name} ({album.Id})");
            }
            if (AlbumMode) PendingAlbum.AddRange(rows);
            else GateInto(rows);
        }
        if (GoalReady) EmitSnapshot("live");
    }

    /// <summary>Stream a fresh snapshot whenever gated rows change.</summary>
    public void EmitSnapshot(string phaseNote)
    {
        if (_cb is null) return;
        List<SearchResult> retrievalCopy, catalogCopy;
        lock (this)
        {
            if (Retrieval.Count == 0) return;
            retrievalCopy = Retrieval.ToList();
            catalogCopy = CatalogSnapshot?.ToList() ?? new List<SearchResult>();
        }
        var sig = string.Join("|", retrievalCopy.Select(r => r.DedupKey));
        if (sig == _emittedSignature) return;
        _emittedSignature = sig;
        var works = WorkAssembler.BuildWorks(catalogCopy, retrievalCopy, ParsedRef, Goal, ArtistMode, AlbumMode);
        if (works.Count == 0) return;
        _cb.Batch?.Invoke(works);
        if (SearchService.DebugPhases)
            Console.WriteLine($"[phase] stream {phaseNote}: {works.Count} works +{Elapsed.TotalSeconds:0.0}s");
    }
}
