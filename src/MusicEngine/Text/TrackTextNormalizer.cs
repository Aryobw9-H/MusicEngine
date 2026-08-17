namespace MusicEngine.Text;

using System.Text;

/// <summary>
/// Text normalization for music search: Persian orthography unification, ZWNJ
/// handling, junk stripping (official audio/live/lyrics suffixes) and cross-script
/// (Persian ↔ Finglish) comparison keys.
///
/// Why: every provider returns a different string for the same song
/// ("Sijal — Bargard (Ft Sami)" vs "bargard (sijal x sami beigi)" vs
/// "Sijal Bargard — با کیفیت 320"). Normalization is the first step of gating,
/// dedup and ranking — nothing else works without it.
/// </summary>
public static class TrackTextNormalizer
{
    // ---------- Persian / Arabic unification ----------

    /// <summary>
    /// Unify the most common Persian/Arabic orthography mismatches. The
    /// homophone folds (ط→ت, ث/ص→س, ذ/ض/ظ→ز) apply to MATCH KEYS ONLY — never to
    /// displayed text — because Finglish conversion cannot distinguish them
    /// ("khatarnak" converts to ختناک while the real title is خطناک).
    /// </summary>
    public static string UnifyPersian(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                'ي' => 'ی',   // Arabic yeh → Persian yeh
                'ك' => 'ک',   // Arabic kaf → Persian kaf
                'ة' or 'ۀ' => 'ه',
                'ؤ' => 'و',
                'أ' or 'إ' or 'ٱ' or 'آ' => 'ا',
                'ى' => 'ی',
                'ط' => 'ت',
                'ث' or 'ص' => 'س',
                'ذ' or 'ض' or 'ظ' => 'ز',
                'ح' => 'ه',   // ح/ه confusion is endemic in Persian typing
                '\u200c' => ' ', // ZWNJ (half-space) → space so "میخوام"=="می خوام"
                '\u200f' or '\u200e' or '\u200d' => ' ', // bidi marks, ZWJ
                _ => c
            });
        }
        return sb.ToString();
    }

    /// <summary>Collapse whitespace, lowercase, trim. Keeps Persian chars intact.</summary>
    public static string Collapse(string s)
    {
        var parts = s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts).ToLowerInvariant();
    }

    /// <summary>Bracketed/parenthesized junk that adds no search value.</summary>
    private static readonly string[] JunkTokens =
    {
        "official audio", "official video", "official music video", "audio", "video",
        "lyrics", "lyric", "with lyrics", "hd", "4k", "8k", "full hd", "high quality",
        "remaster", "remastered", "clip", "music video", "official",
        "single", "mp3", "320", "128", "کیفیت", "دانلود", "اهنگ", "آهنگ", "با کیفیت"
    };

    /// <summary>
    /// Strip bracketed junk like "[Official Audio]", "(Lyrics)", " با کیفیت 320".
    /// Keeps meaningful bracketed content (remix, feat.) intact.
    /// </summary>
    public static string StripJunk(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var chunk in SplitBalanced(s))
        {
            var inner = chunk.Trim();
            var lower = inner.ToLowerInvariant();
            if (IsJunk(lower)) continue;
            sb.Append(inner).Append(' ');
        }
        return sb.ToString().Trim();

        static bool IsJunk(string lower)
        {
            // Persian/Arabic words are NEVER junk — only ASCII-ish quality labels are.
            if (lower.Any(c => c is >= '\u0600' and <= '\u06FF')) return false;
            if (JunkTokens.Any(t => lower.Contains(t, StringComparison.Ordinal))) return true;
            if (lower.All(char.IsDigit)) return true; // "320"
            if (lower.Length <= 5 && lower.All(char.IsLetterOrDigit)
                && (lower.EndsWith("kbps") || lower.EndsWith("k"))) return true; // "320k"
            return false;
        }
    }

    /// <summary>
    /// Split into outer text + balanced bracket groups:
    /// "Sijal - Bargard (Ft Sami) [Official Audio]" → outer + both groups.
    /// </summary>
    private static IEnumerable<string> SplitBalanced(string s)
    {
        var start = 0;
        var depth = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] is '(' or '[' or '{') { if (depth == 0) { yield return s[start..i]; start = i; } depth++; }
            else if (s[i] is ')' or ']' or '}') { depth--; if (depth == 0) { yield return s[start..(i + 1)]; start = i + 1; } }
        }
        if (start < s.Length) yield return s[start..];
    }

    /// <summary>Full normalization used for comparison keys.</summary>
    public static string Normalize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        return Collapse(UnifyPersian(StripJunk(s)));
    }

    /// <summary>
    /// Two-way match keys: the plain normalized form AND the Finglish→Persian
    /// translated form, so Persian and Latin spellings of the same song compare equal.
    /// </summary>
    public static string[] MatchKeys(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return Array.Empty<string>();
        var plain = Normalize(s);
        var fromFinglish = Normalize(FinglishConverter.Convert(s));
        return plain == fromFinglish ? new[] { plain } : new[] { plain, fromFinglish };
    }

    /// <summary>Cross-script equality: "تتلو بهشت" == "tataloo behesht".</summary>
    public static bool KeysOverlap(string a, string b)
    {
        var keysA = MatchKeys(a);
        if (keysA.Length == 0) return false;
        foreach (var kb in MatchKeys(b))
            if (keysA.Contains(kb, StringComparer.Ordinal))
                return true;
        return false;
    }

    /// <summary>
    /// Token-complete cross-script containment — the STRICT goal gate. EVERY token
    /// (length ≥2) of <paramref name="needle"/> must appear in at least one match key
    /// of <paramref name="haystack"/>. Latin needle tokens are also tried in their
    /// Persian conversion so "mehrzad" matches "مهرزاد" (the converter is
    /// one-directional; Persian haystacks never gain Latin keys).
    /// </summary>
    public static bool ContainsAllTokens(string haystack, string needle, bool fuzzy = true, bool substring = true)
    {
        if (string.IsNullOrWhiteSpace(needle)) return true;
        var hayKeys = MatchKeys(haystack ?? string.Empty);
        if (hayKeys.Length == 0) return false;
        var needleTokens = needle.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 2)
            .ToArray();
        if (needleTokens.Length == 0) return true;
        return needleTokens.All(nt =>
        {
            var ntPersian = Normalize(FinglishConverter.Convert(nt));
            return hayKeys.Any(hk =>
                hk.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                  .Any(t => t == nt
                         || (substring && nt.Length >= 3 && t.Contains(nt, StringComparison.Ordinal))
                         || (ntPersian.Length >= 2 && t == ntPersian)
                         // long Persian conversions may safely substring ("لنگهرود");
                         // short ones ("کرج") must NOT match inside "کرجی".
                         || (substring && ntPersian.Length >= 4 && t.Contains(ntPersian, StringComparison.Ordinal))
                         || (fuzzy && (FuzzyEq(t, nt) || FuzzyEq(t, ntPersian)))));
        });
    }

    /// <summary>
    /// Near-equality for spelling drift ("dejad"≈"deejad", "دیداد"≈"دیدجاد").
    /// Persian comparison runs on ALEF-STRIPPED forms — alef insertion/placement
    /// is the single most common Finglish conversion error ("khatarnak" →
    /// "خاترنک" vs the real "خطناک", "bahrām" → "بهرم" vs "بهرام"). Threshold:
    /// ≤1 edit for tokens up to 8 chars, ≤2 only for ≥9 — "baroonam"(8) must
    /// NOT match "aroomam"(7). Short tokens stay exact.
    /// </summary>
    private static bool FuzzyEq(string a, string b)
    {
        if (a.Length < 4 || b.Length < 4 || Math.Abs(a.Length - b.Length) > 2) return false;
        if (HasPersian(a) && HasPersian(b))
        {
            a = a.Replace("ا", "").Replace("آ", "");
            b = b.Replace("ا", "").Replace("آ", "");
            if (a.Length < 3 || b.Length < 3) return false;
        }
        var allowed = Math.Max(a.Length, b.Length) >= 9 ? 2 : 1;
        return EditDistance(a, b) <= allowed;
    }

    private static int EditDistance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>
    /// Space-insensitive contiguous containment across scripts: the Finglish
    /// conversion of a glued Latin word ("azkaraj" → "ازکرج") must match the real
    /// spaced title ("از کرج تا لنگه رود") — and must NOT match "کرجی از …".
    /// Only phrases of length ≥4 use this path so "az"/"از" can't fire everywhere.
    /// </summary>
    public static bool ContainsPhraseSpaceless(string haystack, string needle)
    {
        if (string.IsNullOrWhiteSpace(needle)) return true;
        if (string.IsNullOrWhiteSpace(haystack ?? "")) return false;
        foreach (var nk in MatchKeys(needle))
        {
            var n = Spaceless(nk);
            if (n.Length < 4) continue;
            foreach (var hk in MatchKeys(haystack))
                if (Spaceless(hk).Contains(n, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static string Spaceless(string s) => s.Replace(" ", "").Replace("\u200c", "");

    /// <summary>
    /// Fuzzy-compare normalization: additionally drops ALL bracketed groups
    /// ("Bargard (Live)" → "bargard") so version suffixes don't fragment matching.
    /// </summary>
    public static string NormalizeForFuzzy(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder();
        foreach (var chunk in SplitBalanced(s))
        {
            var cut = new[] { chunk.IndexOf('('), chunk.IndexOf('['), chunk.IndexOf('{') }
                .Where(i => i >= 0).DefaultIfEmpty(-1).Min();
            sb.Append(cut < 0 ? chunk : chunk[..cut]);
        }
        return Collapse(UnifyPersian(StripJunk(sb.ToString())));
    }

    public static bool IsPersianChar(char c) => c is >= '\u0600' and <= '\u06FF';

    public static bool HasPersian(string s) => s.Any(IsPersianChar);
}
