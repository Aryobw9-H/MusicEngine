namespace MusicEngine.Text;

/// <summary>
/// Turns a raw user query into the spellings that should be searched:
/// Persian, Latin (Finglish), and a combined "both" variant.
///
/// Example: "tataloo behesht" → [ "تتلو بهشت", "tataloo behesht", "تتلو بهشت tataloo behesht" ].
/// The whole point: Iranian sites tag music pages in Persian OR in Latin —
/// firing all three maximizes recall.
/// </summary>
public static class FinglishQueryExpander
{
    /// <summary>
    /// Expanded query variants. Index 0 is the best guess (Persian if the query
    /// was Latin; the query as-is if it was already Persian).
    /// </summary>
    public static IReadOnlyList<string> Expand(string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0) return Array.Empty<string>();

        var hasPersian = trimmed.Any(IsPersianChar);
        var hasLatin = trimmed.Any(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z');

        var list = new List<string>();
        if (hasPersian)
        {
            list.Add(trimmed); // already Persian: use it as-is
            if (hasLatin)
                list.Add(ToFinglishish(trimmed)); // mixed → also try dropping the Persian to Latin-ish
        }
        else
        {
            var persian = FinglishConverter.Convert(trimmed);
            list.Add(persian);
            list.Add(trimmed);
            if (!string.Equals(persian, trimmed, StringComparison.OrdinalIgnoreCase))
                list.Add($"{persian} {trimmed}");
        }
        return list.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>True if the char is in the Arabic/Persian Unicode block.</summary>
    public static bool IsPersianChar(char c) => c is >= (char)0x0600 and <= (char)0x06FF;

    /// <summary>
    /// Best-effort Persian→Latin when the query is mixed (rare). In V1 this is a
    /// minimal reverse pass (per-word inverted dictionary hits only); the real
    /// direction (Persian→Finglish) can be added later with the same tables.
    /// </summary>
    private static string ToFinglishish(string s)
    {
        // If we have a dict reversal for any whole word, use it; otherwise leave as-is.
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>();
        foreach (var w in words)
        {
            var hit = FinglishConverter.FindLatinForPersian(w);
            result.Add(hit ?? w);
        }
        return string.Join(' ', result);
    }
}