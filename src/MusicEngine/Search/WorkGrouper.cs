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

        // Merge pass: groups with close durations (±5s) and overlapping text
        // tokens are the same song from different providers with slightly
        // different metadata.
        var groups = buckets.Values.ToList();
        bool merged;
        do
        {
            merged = false;
            for (int i = 0; i < groups.Count; i++)
            {
                for (int j = i + 1; j < groups.Count; j++)
                {
                    if (ShouldMerge(groups[i], groups[j]))
                    {
                        groups[i] = groups[i].Concat(groups[j]).ToList();
                        groups.RemoveAt(j);
                        merged = true;
                        break;
                    }
                }
                if (merged) break;
            }
        } while (merged);

        var works = new List<TrackWork>();
        foreach (var items in groups)
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

    /// <summary>Two groups should merge if they share text tokens AND have
    /// durations within 5 seconds of each other.</summary>
    private static bool ShouldMerge(List<SearchResult> a, List<SearchResult> b)
    {
        // Check duration proximity: any item in A within 5s of any item in B
        var durationsA = a.Where(r => r.Metadata.Duration is { TotalSeconds: > 0 })
                          .Select(r => r.Metadata.Duration!.Value.TotalSeconds).ToList();
        var durationsB = b.Where(r => r.Metadata.Duration is { TotalSeconds: > 0 })
                          .Select(r => r.Metadata.Duration!.Value.TotalSeconds).ToList();
        bool durClose = durationsA.Count == 0 || durationsB.Count == 0
            ? true // no duration info → rely on text only
            : durationsA.Any(da => durationsB.Any(db => Math.Abs(da - db) <= 5));
        if (!durClose) return false;

        // Check text overlap: at least one shared artist or title token
        var titlesA = a.Select(r => TrackTextNormalizer.Normalize(r.Metadata.Title ?? "")).ToList();
        var titlesB = b.Select(r => TrackTextNormalizer.Normalize(r.Metadata.Title ?? "")).ToList();
        var artistsA = a.Select(r => TrackTextNormalizer.Normalize(r.Metadata.Artist ?? "")).ToList();
        var artistsB = b.Select(r => TrackTextNormalizer.Normalize(r.Metadata.Artist ?? "")).ToList();

        bool titleOverlap = titlesA.Any(ta => titlesB.Any(tb =>
            TrackTextNormalizer.KeysOverlap(ta, tb) || TrackTextNormalizer.ContainsAllTokens(ta, tb)));
        bool artistOverlap = artistsA.Any(aa => artistsB.Any(ab =>
            TrackTextNormalizer.KeysOverlap(aa, ab) || TrackTextNormalizer.ContainsAllTokens(aa, ab)));

        return titleOverlap && artistOverlap;
    }

    /// <summary>Cross-script, token-order-independent artist+title+duration key.
    /// Duration is bucketed to 5-second windows so results within ±5s collide.</summary>
    private static string BaseKey(SearchResult r)
    {
        var title = QueryParser.StripVersion(r.Metadata.Title ?? "");
        var artist = TrackTextNormalizer.NormalizeForFuzzy(r.Metadata.Artist ?? "");
        var titleKey = PickPersian(TrackTextNormalizer.MatchKeys(title));
        var artistKey = PickPersian(TrackTextNormalizer.MatchKeys(artist));
        var titleTokens = string.Join(' ', titleKey.Split(' ', StringSplitOptions.RemoveEmptyEntries).OrderBy(t => t, StringComparer.Ordinal));
        var artistTokens = string.Join(' ', artistKey.Split(' ', StringSplitOptions.RemoveEmptyEntries).OrderBy(t => t, StringComparer.Ordinal));
        // Duration bucket: round to 5-second windows so ±5s matches group together
        var durBucket = r.Metadata.Duration is { TotalSeconds: > 0 } d
            ? ((int)d.TotalSeconds / 5).ToString()
            : "?";
        return $"{artistTokens}::{titleTokens}::d{durBucket}";
    }

    private static string PickPersian(string[] keys)
    {
        foreach (var k in keys)
            if (TrackTextNormalizer.HasPersian(k))
                return k;
        return keys.Length > 0 ? keys[0] : "";
    }
}
