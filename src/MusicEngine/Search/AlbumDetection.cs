namespace MusicEngine.Search;

using Models;
using Text;

/// <summary>
/// Album/artist-mode detection (album search): pure statics shared by the
/// catalog block, the mid-stream fan-out and the second-chance path. Extracted
/// from <see cref="SearchService"/> so the orchestrator stays under the 400-line
/// gate.
/// </summary>
public static class AlbumDetection
{
    /// <summary>
    /// Album-name vs query matching: the album's tokens must all appear in
    /// the query (cross-script), ignoring "- Single"/"- EP"-style suffixes —
    /// "jahanam" ⊆ "tataloo jahanam", "Jahanam - Single" ≈ "jahanam".
    /// </summary>
    public static bool AlbumMatchesQuery(string albumName, string rawQuery)
    {
        if (string.IsNullOrWhiteSpace(albumName) || string.IsNullOrWhiteSpace(rawQuery)) return false;
        var album = TrackTextNormalizer.Normalize(albumName);
        foreach (var suffix in new[] { " single", " ep", " ost", " soundtrack", " deluxe", " edition", " album" })
            if (album.EndsWith(suffix, StringComparison.Ordinal)) album = album[..^suffix.Length].Trim();
        if (album.Length == 0) return false;
        var query = TrackTextNormalizer.Normalize(rawQuery);
        return TrackTextNormalizer.KeysOverlap(album, query)
            || TrackTextNormalizer.ContainsAllTokens(query, album, fuzzy: true, substring: true)
            || TrackTextNormalizer.ContainsAllTokens(album, query, fuzzy: true, substring: true);
    }

    /// <summary>
    /// True when ≥2 rows share one album whose name matches the query — the
    /// signature of an album query ("jahanam") rather than a song query.
    /// </summary>
    public static bool TryDetectAlbumMode(IReadOnlyList<SearchResult> rows, string rawQuery, out AlbumRef album)
    {
        album = null!;
        var best = rows
            .Where(r => GoalResolver.IsSongLikeDuration(r.Metadata.Duration))
            .GroupBy(r => r.Metadata.AlbumId ?? TrackTextNormalizer.Normalize(r.Metadata.Album ?? ""))
            .Where(g => g.Key.Length > 0)
            .Select(g => new
            {
                Key = g.Key,
                Count = g.Count(),
                AlbumName = g.First().Metadata.Album ?? "",
                Artist = g.First().Metadata.Artist ?? "",
                Provider = g.First().Provider,
            })
            .Where(x => x.Count >= 2 && AlbumMatchesQuery(x.AlbumName, rawQuery))
            .OrderByDescending(x => x.Count)
            .FirstOrDefault();
        if (best is null) return false;
        album = new AlbumRef(best.Key, best.AlbumName, best.Artist, best.Provider);
        return true;
    }

    /// <summary>
    /// True when ≥3 rows share one artist matching the bare query token —
    /// the signature of an artist-catalog query rather than a song query.
    /// </summary>
    public static bool TryDetectArtistMode(string? bareToken, IReadOnlyList<SearchResult> rows, out string canonicalArtist)
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

    /// <summary>True when the catalogs confirmed the goal as a real song: some
    /// catalog row carries an album-TRACK identity (not "- Single"/"- EP") for
    /// the goal's artist AND title. When false, the query is either absent from
    /// the catalogs or only known as a single — both cases where a discovered
    /// playlist ("fadaei hagh") should take over.</summary>
    public static bool AlbumTrackConfirmed(GoalSong goal, IReadOnlyList<SearchResult> catalog)
    {
        if (catalog.Count == 0 || string.IsNullOrWhiteSpace(goal.Title)) return false;
        return catalog.Any(r =>
            r.Metadata.Album is { Length: > 0 } a && !IsSingleMarked(a)
            && (TrackTextNormalizer.KeysOverlap(r.Metadata.Artist ?? "", goal.Artist)
                || TrackTextNormalizer.ContainsAllTokens(r.Metadata.Artist ?? "", goal.Artist))
            && (TrackTextNormalizer.KeysOverlap(r.Metadata.Title ?? "", goal.Title)
                || TrackTextNormalizer.ContainsAllTokens(r.Metadata.Title ?? "", goal.Title)));
    }

    /// <summary>"Jahanam - Single"/"- EP" collections are 1-track "albums" —
    /// iTunes marks them so the user knows the full album is elsewhere. The
    /// suffix is checked on the RAW name: Normalize strips "single" as junk.</summary>
    public static bool IsSingleMarked(string album)
    {
        var raw = album.TrimEnd().ToLowerInvariant();
        return raw.EndsWith(" - single") || raw.EndsWith(" - ep")
            || raw.EndsWith(" single") || raw.EndsWith(" ep") || raw.EndsWith(" ost");
    }
}
