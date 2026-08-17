namespace MusicEngine.Search;

using Models;
using Text;

/// <summary>
/// Ranking v2: artist exactness dominates (a different artist scores near zero),
/// duration consistency vs the candidate-set median crushes reaction/live uploads,
/// source trust differentiates providers, canonical (non-remix) titles get a bonus.
/// Score ∈ [0,1]; the representative of a work is its highest-scoring version.
/// </summary>
public static class Ranker
{
    private const double WQuery = 0.30;
    private const double WArtist = 0.25;
    private const double WDuration = 0.20;
    private const double WTrust = 0.15;
    private const double WCanonical = 0.10;

    public static double Score(SearchResult r, ParsedQuery q, TimeSpan? medianDuration)
    {
        var t = r.Metadata.Title ?? "";
        var a = r.Metadata.Artist ?? "";
        var tn = TrackTextNormalizer.NormalizeForFuzzy(t);
        var an = TrackTextNormalizer.NormalizeForFuzzy(a);

        // -- query match (30): title tokens present in title; artist tokens in artist
        var queryScore = 0.0;
        if (q.Title is { } qt)
        {
            var qtWords = qt.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var hits = qtWords.Count(w => tn.Contains(w, StringComparison.Ordinal)
                                          || TrackTextNormalizer.KeysOverlap(w, tn));
            queryScore = qtWords.Length == 0 ? 0 : (double)hits / qtWords.Length;
        }
        if (q.Artist is { } qa)
        {
            var qaWords = qa.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var artistHits = qaWords.Count(w => an.Contains(w, StringComparison.Ordinal)
                                                || TrackTextNormalizer.KeysOverlap(w, an));
            queryScore = 0.6 * queryScore + 0.4 * (qaWords.Length == 0 ? 0 : (double)artistHits / qaWords.Length);
        }

        // -- artist exactness (25): exact or cross-script = 1.0; containment = 0.5
        var artistScore = 0.5;
        if (q.Artist is { } qa2)
        {
            var qn = TrackTextNormalizer.NormalizeForFuzzy(qa2);
            if (qn.Length > 0)
            {
                artistScore = an == qn || TrackTextNormalizer.KeysOverlap(qn, an) ? 1.0
                    : an.Contains(qn, StringComparison.Ordinal) || qn.Contains(an, StringComparison.Ordinal) ? 0.5
                    : 0.0;
            }
        }

        // -- duration consistency (20) vs the set median
        var durScore = 0.5;
        if (r.Metadata.Duration is { } d && medianDuration is { } md && md.TotalSeconds > 0)
        {
            var rel = Math.Abs(d.TotalSeconds - md.TotalSeconds) / Math.Max(md.TotalSeconds, 1);
            durScore = rel <= 0.05 ? 1.0 : rel <= 0.15 ? 0.7 : rel <= 0.30 ? 0.3 : 0.05;
        }

        // -- source trust (15)
        var trust = r.Provider switch
        {
            ProviderId.Deezer => 1.0,
            ProviderId.ITunes => 1.0,
            ProviderId.RadioJavan => 0.7,
            ProviderId.YouTube => 0.55,
            ProviderId.PersianSites => 0.5,
            ProviderId.PersianIndex => 0.5,
            ProviderId.Nex1Music => 0.5,
            ProviderId.SoundCloud => 0.4,
            _ => 0.4,
        };

        // -- canonical bonus (10): no version words in title
        var canonicalScore = VersionLike.IsMatch(tn) ? 0.3 : 1.0;

        return Math.Clamp(
            WQuery * queryScore + WArtist * artistScore + WDuration * durScore + WTrust * trust + WCanonical * canonicalScore,
            0, 1);
    }

    public static string VersionLabel(SearchResult r)
    {
        var t = TrackTextNormalizer.NormalizeForFuzzy(r.Metadata.Title ?? "");
        if (t.Contains("remix") || t.Contains("میکس") || t.Contains("ریمیکس")) return "Remix";
        if (t.Contains("live") || t.Contains("زنده") || t.Contains("کنسرت") || t.Contains("concert")) return "Live";
        if (t.Contains("cover") || t.Contains("کاور")) return "Cover";
        if (t.Contains("reaction") || t.Contains("ریاکشن")) return "Reaction";
        if (t.Contains("acoustic") || t.Contains("instrumental") || t.Contains("karaoke")) return "Alternate";
        if (t.Contains("official video") || t.Contains("موزیک ویدیو")) return "Video";
        if (t.Contains("lyrics") || t.Contains("متن آهنگ")) return "Lyrics";
        return "Original";
    }

    private static readonly System.Text.RegularExpressions.Regex VersionLike = new(
        @"\b(remix|live|cover|acoustic|reaction|official\s*video|instrumental|karaoke|unplugged|clean|explicit|edit|mix|bootleg|refix|sped\s*up|slowed)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
}
