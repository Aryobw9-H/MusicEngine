namespace MusicEngine.Text;

using System.Text.RegularExpressions;

/// <summary>Structured interpretation of a free-text music query.</summary>
public sealed record ParsedQuery(
    string Raw,
    string? Artist,
    string? Title,
    string? Feature,
    string? Version,
    bool HasExplicitStructure);

/// <summary>
/// Query understanding: turns free text into fields before fan-out.
/// "behesht - amir tataloo" → Artist=amir tataloo, Title=behesht
/// "amir tataloo behesht"   → Artist=amir tataloo, Title=behesht (heuristic)
/// "behesht feat. sami"     → Title=behesht, Feature=sami
/// Persian is normalized FIRST (ی/ک/ZWNJ) so parsing is stable.
/// </summary>
public static class QueryParser
{
    private static readonly Regex DashSeparator = new(@"\s*[–—\-]\s*", RegexOptions.Compiled);
    private static readonly Regex BySeparator = new(@"\s+by\s+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FeatureMarker = new(
        @"\b(?:feat\.?|ft\.?|featuring|فیت|همراه)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex VersionMarker = new(
        @"\b(remix|live|cover|acoustic|original|clean|explicit|instrumental|karaoke|unplugged|official\s*video|میکس|ریمیکس|زنده)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static ParsedQuery Parse(string raw)
    {
        var s = TrackTextNormalizer.NormalizeForFuzzy(raw).Trim();
        if (s.Length == 0) return new ParsedQuery(raw, null, null, null, null, false);

        // 1. Explicit structure: "A - T", "A – T", "T by A"
        var dash = DashSeparator.Split(s, 2);
        if (dash.Length == 2)
        {
            var a = dash[0].Trim();
            var t = dash[1].Trim();
            if (a.Length > 0 && t.Length > 0)
                return new ParsedQuery(raw, a, StripVersion(t), ExtractFeature(t), FindVersion(t), true);
        }
        var by = BySeparator.Split(s, 2);
        if (by.Length == 2)
        {
            var t = by[0].Trim();
            var a = by[1].Trim();
            if (t.Length > 0 && a.Length > 0)
                return new ParsedQuery(raw, a, StripVersion(t), ExtractFeature(t), FindVersion(t), true);
        }

        // 2. No structure: first 1-2 tokens are the most likely artist when the
        //    query reads "NAME NAME TITLE…". Songs have short titles — use that.
        var tokens = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 2)
        {
            // "tataloo behesht" is overwhelmingly "artist title" in this domain;
            // splitting lets the goal gate match artist and title separately.
            return new ParsedQuery(raw, tokens[0], StripVersion(tokens[1]),
                ExtractFeature(s), FindVersion(s), false);
        }
        if (tokens.Length >= 3)
        {
            var joined = string.Join(' ', tokens);
            var feature = ExtractFeature(joined);
            var version = FindVersion(joined);

            for (var k = Math.Min(2, tokens.Length - 1); k >= 1; k--)
            {
                var candArtist = string.Join(' ', tokens[..k]);
                var candTitle = string.Join(' ', tokens[k..]);
                if (candTitle.Length <= 24)
                    return new ParsedQuery(raw, candArtist, StripVersion(candTitle), feature, version, false);
            }
        }

        // 3. 1-2 tokens: title-only.
        return new ParsedQuery(raw, null, StripVersion(s), ExtractFeature(s), FindVersion(s), false);
    }

    /// <summary>Remove version/quality noise from a title: "(Remix)" → "" etc.</summary>
    public static string StripVersion(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return title;
        var t = VersionMarker.Replace(title, " ").Trim();
        t = Regex.Replace(t, @"\s*[\(\[\{].*?[\)\]\}]\s*", " ");
        t = TrackTextNormalizer.NormalizeForFuzzy(t).Trim();
        return t.Length == 0 ? title : t;
    }

    public static string? ExtractFeature(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = FeatureMarker.Match(text);
        if (!m.Success) return null;
        var after = text[m.Index..];
        after = VersionMarker.Replace(after, " ").Trim(' ', '(', ')', '[', ']', ',', '&', '+');
        return after.Length > 0 ? after : null;
    }

    public static string? FindVersion(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = VersionMarker.Match(text);
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }
}
