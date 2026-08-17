namespace MusicEngine.Search;

using Models;
using Text;

/// <summary>
/// Groups results into canonical works: "Behesht", "Behesht (Remix)" collapse into
/// ONE work with versions under it. Grouping keys are script-neutral (Persian ↔
/// Finglish collide) and token-order independent ("امیر تتلو بهشت" == "بهشت امیر تتلو").
/// Only Original/Remix/Alternate survive as versions; junk labels were already
/// filtered upstream.
/// </summary>
public static class WorkGrouper
{
    public static IReadOnlyList<TrackWork> Group(
        IEnumerable<SearchResult> results,
        ParsedQuery q,
        TimeSpan? medianDuration,
        GoalSong goal)
    {
        var buckets = new Dictionary<string, List<SearchResult>>(StringComparer.Ordinal);
        foreach (var r in results)
        {
            var key = BaseKey(r);
            if (!buckets.TryGetValue(key, out var list))
                buckets[key] = list = new List<SearchResult>();
            list.Add(r);
        }

        var works = new List<TrackWork>();
        foreach (var items in buckets.Values)
        {
            var versions = items
                .Select(r => new TrackVersion(r, Ranker.VersionLabel(r), Ranker.Score(r, q, medianDuration)))
                .OrderByDescending(v => v.Score)
                .ToList();

            var rep = versions[0];
            works.Add(new TrackWork(
                rep.Result.Metadata.Title,
                rep.Result.Metadata.Artist,
                rep.Result,
                versions,
                goal));
        }
        return works.OrderByDescending(w => w.Versions[0].Score).ToArray();
    }

    /// <summary>Cross-script, token-order-independent artist+title key.</summary>
    private static string BaseKey(SearchResult r)
    {
        var title = QueryParser.StripVersion(r.Metadata.Title ?? "");
        var artist = TrackTextNormalizer.NormalizeForFuzzy(r.Metadata.Artist ?? "");
        var titleKey = PickPersian(TrackTextNormalizer.MatchKeys(title));
        var artistKey = PickPersian(TrackTextNormalizer.MatchKeys(artist));
        var titleTokens = string.Join(' ', titleKey.Split(' ', StringSplitOptions.RemoveEmptyEntries).OrderBy(t => t, StringComparer.Ordinal));
        var artistTokens = string.Join(' ', artistKey.Split(' ', StringSplitOptions.RemoveEmptyEntries).OrderBy(t => t, StringComparer.Ordinal));
        return $"{artistTokens}::{titleTokens}";
    }

    private static string PickPersian(string[] keys)
    {
        foreach (var k in keys)
            if (TrackTextNormalizer.HasPersian(k))
                return k;
        return keys.Length > 0 ? keys[0] : "";
    }
}
