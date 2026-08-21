namespace MusicEngine.Search;

using Models;
using Text;

/// <summary>
/// Result assembler (MODERN-03/Phase-4 gate): turns the gated catalog + retrieval
/// rows into the final <see cref="TrackWork"/> list — work grouping, version
/// attachment, artist-mode handling and ranking. Pure and static, moved verbatim
/// from <see cref="SearchService"/> so the orchestrator stays under 400 lines.
/// </summary>
public static class WorkAssembler
{
    /// <summary>Album-name equality across scripts/junk: "Jahanam - Single" ≈ "jahanam".</summary>
    public static bool AlbumMatches(string album, string goalAlbum)
    {
        if (string.IsNullOrWhiteSpace(album) || string.IsNullOrWhiteSpace(goalAlbum)) return false;
        // iTunes appends "- Single"/"- EP" to collection names — strip those
        // edition suffixes before comparing so "Jahanam - Single" matches "jahanam".
        var a = TrackTextNormalizer.Normalize(album);
        var g = TrackTextNormalizer.Normalize(goalAlbum);
        foreach (var suffix in new[] { " single", " ep", " ost", " soundtrack", " deluxe", " edition" })
        {
            if (a.EndsWith(suffix, StringComparison.Ordinal)) a = a[..^suffix.Length].Trim();
            if (g.EndsWith(suffix, StringComparison.Ordinal)) g = g[..^suffix.Length].Trim();
        }
        if (a.Length == 0 || g.Length == 0) return false;
        return TrackTextNormalizer.KeysOverlap(a, g)
            || TrackTextNormalizer.ContainsAllTokens(a, g, fuzzy: true, substring: true)
            || TrackTextNormalizer.ContainsAllTokens(g, a, fuzzy: true, substring: true);
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

    public static List<TrackWork> BuildWorks(
        IReadOnlyList<SearchResult> catalog,
        List<SearchResult> retrieval,
        ParsedQuery parsed,
        GoalSong goal,
        bool artistMode = false,
        bool albumMode = false)
    {
        var works = new List<TrackWork>();
        var versions = retrieval
            .Select(r => new TrackVersion(r, Ranker.VersionLabel(r), Ranker.Score(r, parsed, MedianDuration(retrieval))))
            .OrderByDescending(v => v.Score)
            .ToList();

        if (albumMode && goal.Album is { Length: > 0 })
        {
            // Album catalog: every distinct track of the album becomes a work,
            // in track-number order; gated copies attach by title match (scraped
            // rows carry no album field, so identity is the song itself).
            var seenWorks = new HashSet<string>(StringComparer.Ordinal);
            var albumRows = catalog
                .Where(r => AlbumMatches(r.Metadata.Album ?? "", goal.Album)
                            && GoalResolver.IsSongLikeDuration(r.Metadata.Duration))
                .OrderBy(r => r.Metadata.TrackNumber ?? int.MaxValue)
                .ThenBy(r => r.Metadata.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var row in albumRows)
            {
                var key = TrackTextNormalizer.Normalize(row.Metadata.Title ?? "");
                if (key.Length == 0 || !seenWorks.Add(key)) continue;
                var mine = versions
                    .Where(v => TrackTextNormalizer.KeysOverlap(v.Result.Metadata.Title ?? "", row.Metadata.Title ?? "")
                                || TrackTextNormalizer.ContainsAllTokens(v.Result.Metadata.Title ?? "", row.Metadata.Title ?? ""))
                    .ToList();
                works.Add(new TrackWork(
                    row.Metadata.Title ?? "",
                    row.Metadata.Artist,
                    row,
                    mine.Count > 0 ? mine : new List<TrackVersion> { new(row, "Album", 0.5) },
                    goal));
                if (works.Count >= 40) break;
            }
            // Gated copies whose song isn't in the expanded track list still
            // deserve rows (a search surfaced a track the expansion missed).
            var attached = works.SelectMany(w => w.Versions.Select(v => v.Result.DedupKey))
                .ToHashSet(StringComparer.Ordinal);
            var orphans = retrieval.Where(r => !attached.Contains(r.DedupKey)).ToList();
            if (orphans.Count > 0)
                works.AddRange(WorkGrouper.Group(orphans, parsed, MedianDuration(orphans), goal));
            return works;
        }

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
                    row.Metadata.Title ?? "",
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
}
