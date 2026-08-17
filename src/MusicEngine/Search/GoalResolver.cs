namespace MusicEngine.Search;

using Models;
using Text;

/// <summary>
/// Resolves the GOAL identity ("the real song the user wants") from catalog rows
/// (iTunes/Deezer). When no catalog row matches (Persian music is often absent
/// from western catalogs), the parsed query becomes the goal.
/// </summary>
public static class GoalResolver
{
    public static GoalSong Resolve(ParsedQuery parsed, IReadOnlyList<SearchResult> catalogResults)
    {
        var hasArtist = !string.IsNullOrWhiteSpace(parsed.Artist);
        var candidates = catalogResults.Where(r => IsSongLikeDuration(r.Metadata.Duration)).ToList();

        // NEVER adopt another artist's row: when the query names an artist,
        // prefer rows by that artist; a title-only match by someone else (a
        // cover, a tribute) may supply the TITLE at best, not the identity.
        if (hasArtist)
        {
            var byArtist = candidates
                .Where(r => ArtistMatch(r.Metadata.Artist ?? "", parsed.Artist!))
                .ToList();
            if (byArtist.Count > 0) candidates = byArtist;
        }

        var best = candidates
            .Select(r => new
            {
                Result = r,
                TitleMatch = TitleMatch(r.Metadata.Title ?? "", parsed.Title ?? parsed.Raw),
                ArtistMatch = !hasArtist || ArtistMatch(r.Metadata.Artist ?? "", parsed.Artist!),
            })
            .Where(x => x.TitleMatch || x.ArtistMatch)
            .OrderByDescending(x => (x.TitleMatch ? 1 : 0) + (x.ArtistMatch ? 1 : 0))
            .ThenByDescending(x => x.Result.Metadata.Duration.HasValue)
            .FirstOrDefault();

        if (best is not null)
        {
            var artist = hasArtist
                ? best.ArtistMatch ? best.Result.Metadata.Artist ?? parsed.Artist : parsed.Artist
                : best.Result.Metadata.Artist ?? "";
            return new GoalSong(
                artist ?? "",
                best.TitleMatch ? best.Result.Metadata.Title ?? parsed.Title ?? parsed.Raw
                                : parsed.Title ?? parsed.Raw,
                // Without a TITLE match the chosen row is some other song by the
                // same artist — its duration must not gate the real one.
                best.TitleMatch ? best.Result.Metadata.Duration : null,
                best.Result.Provider);
        }

        return new GoalSong(parsed.Artist ?? "", parsed.Title ?? parsed.Raw, null, ProviderId.Unknown);
    }

    private static bool ArtistMatch(string artist, string needle)
    {
        if (needle.Length == 0) return true;
        return TrackTextNormalizer.KeysOverlap(artist, needle)
            || TrackTextNormalizer.ContainsAllTokens(artist, needle)
            || TrackTextNormalizer.ContainsAllTokens(needle, artist);
    }

    private static bool TitleMatch(string title, string needle)
    {
        if (needle.Length == 0) return false;
        return TrackTextNormalizer.KeysOverlap(title, needle)
            || TrackTextNormalizer.ContainsAllTokens(title, needle)
            || TrackTextNormalizer.ContainsAllTokens(needle, title);
    }

    /// <summary>Durations a real song can have: 1s–20min (null = unknown, allowed).</summary>
    public static bool IsSongLikeDuration(TimeSpan? d)
        => d is null || (d.Value.TotalSeconds > 0 && d.Value.TotalSeconds <= 20 * 60);
}
