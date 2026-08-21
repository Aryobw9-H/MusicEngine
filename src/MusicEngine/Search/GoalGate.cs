namespace MusicEngine.Search;

using Models;
using Text;

/// <summary>
/// Strict/loose goal gating (MODERN-03): decides whether a scraped row is the
/// song the user asked for. Pure and static — the first seam extracted from
/// <see cref="SearchService"/> so gating can be tested in isolation.
/// </summary>
public interface IGoalGate
{
    bool PassesStrict(SearchResult r, GoalSong goal);
    bool PassesLoose(SearchResult r, GoalSong goal);
}

/// <summary>Default gate. Logic moved verbatim from SearchService (no threshold or order changes).</summary>
public sealed class GoalGate : IGoalGate
{
    public bool PassesStrict(SearchResult r, GoalSong goal) => PassesGoalGate(r, goal);

    public bool PassesLoose(SearchResult r, GoalSong goal) => PassesLooseGate(r, goal);

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

    /// <summary>
    /// Ultra-lenient gate for download resolution: the user already picked the
    /// song via iTunes — we just need to find a downloadable copy on domestic
    /// sources. Accepts any result where at least one search-token appears in
    /// the combined artist+title text, and the duration looks song-like.
    /// </summary>
    public static bool PassesDownloadGate(SearchResult r, GoalSong goal, string searchTerm)
    {
        var title = r.Metadata.Title ?? "";
        var artist = r.Metadata.Artist ?? "";
        if (JunkFilter.IsJunkTitle(title) || JunkFilter.IsJunkChannel(r.SourceUrl)) return false;
        // Allow null duration for domestic providers that don't report it.
        // Only reject obviously-clip durations.
        if (r.Metadata.Duration is { TotalSeconds: > 0 and < 30 }) return false;

        var combined = $"{artist} {title}".Trim();
        if (combined.Length == 0) return false;

        // ULTRA-LENIENT: the user already picked the song from search.
        // Domestic providers may title it differently (Finglish vs Persian,
        // different transliterations). Accept if ANY token from the goal
        // artist OR the search term appears anywhere in the result.
        var tokens = searchTerm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var t in tokens)
        {
            if (TrackTextNormalizer.ContainsAllTokens(combined, t)) return true;
        }

        // Cross-script artist check: try the goal artist as a single token.
        if (!string.IsNullOrWhiteSpace(goal.Artist))
        {
            if (TrackTextNormalizer.ContainsAllTokens(combined, goal.Artist)) return true;
            // Also try individual words of the artist name.
            foreach (var word in goal.Artist.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length >= 2 && TrackTextNormalizer.ContainsAllTokens(combined, word))
                    return true;
            }
        }

        // Cross-script title check: try the goal title.
        if (!string.IsNullOrWhiteSpace(goal.Title))
        {
            if (TrackTextNormalizer.ContainsAllTokens(combined, goal.Title)) return true;
            foreach (var word in goal.Title.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length >= 2 && TrackTextNormalizer.ContainsAllTokens(combined, word))
                    return true;
            }
        }

        // Last resort: accept if there is ANY overlap between goal identity
        // and result, using the broadest cross-script check available.
        if (!string.IsNullOrWhiteSpace(goal.Artist) && !string.IsNullOrWhiteSpace(artist))
        {
            if (TrackTextNormalizer.KeysOverlap(artist, goal.Artist)
                || TrackTextNormalizer.ContainsAllTokens(artist, goal.Artist))
                return true;
        }
        if (!string.IsNullOrWhiteSpace(goal.Title) && !string.IsNullOrWhiteSpace(title))
        {
            if (TrackTextNormalizer.KeysOverlap(title, goal.Title)
                || TrackTextNormalizer.ContainsAllTokens(title, goal.Title))
                return true;
        }

        // Absolute last resort: if the goal has no title (artist-only search),
        // accept any non-junk domestic result. The user wants SOMETHING by
        // this artist.
        if (string.IsNullOrWhiteSpace(goal.Title) && !string.IsNullOrWhiteSpace(goal.Artist))
        {
            return true;
        }

        return false;
    }
}