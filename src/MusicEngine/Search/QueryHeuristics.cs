namespace MusicEngine.Search;

using Text;

/// <summary>
/// Query-shape heuristics (Phase-4 gate): whether a query is Persian-ish enough
/// that the slow Iranian download tiers may pay off from the first fan-out.
/// Moved verbatim from <see cref="SearchService"/>.
/// </summary>
public static class QueryHeuristics
{
    /// <summary>
    /// Persian-ish query → the slow Iranian tiers may pay off, so they run from
    /// the start. Latin queries skip them (scrapers would only return junk that
    /// the gate rejects anyway).
    /// </summary>
    public static bool ShouldSpeculate(string rawQuery) =>
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
}
