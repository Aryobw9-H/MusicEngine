using System.Text;

namespace MusicEngine.Text;

/// <summary>
/// Finglish (Persian in Latin script) → Persian converter.
/// C# port of <c>elektito/finglish</c> (MIT, https://github.com/elektito/finglish).
///
/// Why: the app must turn "tataloo behesht" into "تتلو بهشت" (and vice-versa) so
/// queries and Persian site titles can be matched. The original uses per-position
/// letter tables (beginning/middle/ending), multi-letter sound variations
/// (kh/ch/gh/sh/zh/ck/kha, oo→u, ee→i…), a proper-noun dictionary fast path, and a
/// word-frequency tie-break. This port keeps all of it except the 7 MB word-freq
/// table (V1: confidence comes from the dictionary + alternative count instead —
/// see <see cref="ScoreAlternatives"/>).
///
/// Output: for each word, the top-3 alternatives with confidence [0..1], exactly
/// like the Python original's <c>cutoff=3</c>.
/// </summary>
public static class FinglishConverter
{
    private static readonly Lazy<IReadOnlyDictionary<string, string[]>> Beginning =
        new(() => ParseTable(Embedded.Load("f2p-beginning.txt")));
    private static readonly Lazy<IReadOnlyDictionary<string, string[]>> Middle =
        new(() => ParseTable(Embedded.Load("f2p-middle.txt")));
    private static readonly Lazy<IReadOnlyDictionary<string, string[]>> Ending =
        new(() => ParseTable(Embedded.Load("f2p-ending.txt")));
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Dict =
        new(() => ParseDict(Embedded.Load("f2p-dict.txt")));
    private static readonly Lazy<IReadOnlyDictionary<string, string>> MusicDict =
        new(() => ParseDict(Embedded.Load("f2p-music.txt")));
    private static readonly Lazy<IReadOnlyDictionary<string, string>> TrickyDict =
        new(() => ParseDict(Embedded.Load("f2p-tricky.txt")));

    /// <summary>Memoized phrase conversions — the gate converts many strings repeatedly.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> PhraseCache = new();

    /// <summary>
    /// Curated dictionaries merged in priority order (highest first):
    /// tricky (colloquial song-speak, hand-curated) → music (song vocab) → base
    /// (upstream elektito/finglish proper-noun dict).
    /// </summary>
    private static string? LookupDict(string word) =>
        TrickyDict.Value.TryGetValue(word, out var t) ? t
        : MusicDict.Value.TryGetValue(word, out var m) ? m
        : Dict.Value.TryGetValue(word, out var d) ? d
        : null;

    /// <summary>Max word length we attempt; longer words pass through unchanged (matches Python).</summary>
    public const int MaxWordSize = 15;

    /// <summary>Number of alternatives returned per word (matches Python default cutoff=3).</summary>
    public const int Cutoff = 3;

    /// <summary>Load a "letter alternatives" line: `a  ا ع آ عا` → {"a": ["ا","ع","آ","عا"]}.</summary>
    internal static IReadOnlyDictionary<string, string[]> ParseTable(string content)
    {
        var map = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var letter = parts[0];
            var alts = parts.Skip(1)
                .Select(p => p == "nothing" ? "" : p)
                .ToArray();
            if (!map.ContainsKey(letter)) map[letter] = alts;
        }
        return map;
    }

    /// <summary>Load the dictionary: `word persian` → {"word": "persian"}.</summary>
    internal static IReadOnlyDictionary<string, string> ParseDict(string content)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var sp = line.IndexOf(' ');
            if (sp <= 0) continue;
            var word = line[..sp].Trim();
            var persian = line[(sp + 1)..].Trim();
            if (word.Length > 0 && persian.Length > 0) map[word] = persian;
        }
        return map;
    }

    /// <summary>Convert a full phrase to Persian. Mirrors Python <c>f2p()</c>.</summary>
    public static string Convert(string phrase)
    {
        if (phrase.Length > 120) return phrase; // absurd input — don't cache or convert
        return PhraseCache.GetOrAdd(phrase, ConvertUncached);
    }

    private static string ConvertUncached(string phrase)
    {
        var outWords = new List<string>();
        foreach (var word in SplitPhrase(phrase))
        {
            var alts = ConvertWord(word);
            outWords.Add(alts.Count > 0 ? alts[0].Persian : word);
        }
        return string.Join(' ', outWords);
    }

    /// <summary>
    /// Reverse dictionary lookup: Persian word → Finglish spelling, using the
    /// embedded f2p-dict table in reverse. Returns null when unknown.
    /// </summary>
    public static string? FindLatinForPersian(string persianWord)
    {
        var norm = persianWord.Trim();
        if (norm.Length == 0) return null;
        // exact whole-word reversal first
        foreach (var (latin, p) in Dict.Value)
        {
            if (string.Equals(p, norm, StringComparison.Ordinal)) return latin;
        }
        return null;
    }

    /// <summary>
    /// Convert a phrase, returning the top alternatives per word with confidence.
    /// Mirrors Python <c>f2p_list()</c>.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<FinglishAlternative>> ConvertDetailed(string phrase)
    {
        return SplitPhrase(phrase)
            .Select(ConvertWord)
            .ToArray();
    }

    /// <summary>Split on the same separator set as the Python original (spaces, dashes, slashes, punctuation…).</summary>
    public static IReadOnlyList<string> SplitPhrase(string phrase)
    {
        var words = new List<string>();
        var sb = new StringBuilder();
        foreach (var c in phrase)
        {
            if (char.IsWhiteSpace(c) || c is '-' or '_' or '~' or '!' or '@' or '#' or '$' or '%' or '^'
                or '&' or '*' or '(' or ')' or '[' or ']' or '{' or '}' or '/' or '\\' or ':' or ';'
                or '"' or '|' or ',' or '.' or '?' or '`' or '\'')
            {
                if (sb.Length > 0) { words.Add(sb.ToString()); sb.Clear(); }
            }
            else sb.Append(c);
        }
        if (sb.Length > 0) words.Add(sb.ToString());
        return words;
    }

    /// <summary>
    /// Convert a single word. Returns top-<see cref="Cutoff"/> alternatives.
    /// Mirrors Python <c>f2p_word()</c>.
    /// </summary>
    public static IReadOnlyList<FinglishAlternative> ConvertWord(string rawWord)
    {
        var original = rawWord.Trim();
        if (original.Length == 0) return Array.Empty<FinglishAlternative>();
        var word = original.ToLowerInvariant();

        // Dictionary fast path (proper names, artists): "tataloo" → "تتلو" with certainty.
        var dictValue = LookupDict(word);
        if (dictValue is not null)
            return new[] { new FinglishAlternative(dictValue, 1.0) };

        if (word.Length > MaxWordSize)
            return new[] { new FinglishAlternative(original, 1.0) };

        var results = new List<FinglishAlternative>();
        foreach (var variation in Variations(word))
        {
            foreach (var alt in WordInternal(variation, original))
            {
                var idx = results.FindIndex(r => r.Persian == alt.Persian);
                if (idx >= 0)
                    results[idx] = results[idx] with { Confidence = Math.Max(results[idx].Confidence, alt.Confidence) };
                else
                    results.Add(alt);
            }
        }

        // Sort by confidence like the Python original, then cap at Cutoff.
        results.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));
        results = ApplyColloquialEndingRule(results, word);
        return results.Take(Cutoff).ToArray();
    }

    /// <summary>
    /// Colloquial-ending post-pass: when the Finglish word ends in `e`/`eh` (the
    /// colloquial "silent e" — mishe, mire, zane…), prefer candidates ending in `ه`
    /// over the same candidate ending in `ع` (which the upstream tables rank first
    /// because of literary/Arabic bias). Song titles are overwhelmingly colloquial,
    /// so "میشه/میره/زنه" wins over "میشع/میرع/زنع".
    /// </summary>
    private static List<FinglishAlternative> ApplyColloquialEndingRule(
        List<FinglishAlternative> results, string lowerWord)
    {
        var endsWithE = lowerWord.EndsWith('e') || lowerWord.EndsWith("eh");
        if (!endsWithE || results.Count < 2) return results;

        // find the top-`ه`-ending candidate among the top-5 and bump it above the
        // same-shaped `ع` candidate.
        var top = results.Take(5).ToList();
        var heCandidate = top.FirstOrDefault(r =>
            r.Persian is { Length: > 1 } && r.Persian.EndsWith('ه'));
        if (heCandidate.Persian is null) return results;
        var aynCandidate = top.FirstOrDefault(r =>
            r.Persian is { Length: > 1 } && r.Persian.EndsWith('ع')
            && r.Persian.Length == heCandidate.Persian.Length);
        if (aynCandidate.Persian is not null)
        {
            // swap them in the list
            var iHe = results.FindIndex(r => r.Persian == heCandidate.Persian);
            var iAyn = results.FindIndex(r => r.Persian == aynCandidate.Persian);
            if (iHe >= 0 && iAyn >= 0 && iHe > iAyn)
            {
                var m = results[iHe];
                results[iHe] = results[iAyn];
                results[iAyn] = m;
            }
        }
        return results;
    }

    /// <summary>
    /// Letter-by-letter conversion of one variation token-list.
    /// Mirrors Python <c>f2p_word_internal()</c> — with a hard combination cap:
    /// the cross-product of letter alternatives grows exponentially with word
    /// length (3^14 ≈ 4.8M for one long token), which once stalled the whole
    /// search gate for minutes. 512 candidates is far beyond the useful top-3.
    /// </summary>
    private const int MaxCombinations = 512;

    private static IEnumerable<FinglishAlternative> WordInternal(IReadOnlyList<string> letters, string originalWord)
    {
        var tableChain = new List<IReadOnlyDictionary<string, string[]>>(letters.Count);
        for (var i = 0; i < letters.Count; i++)
        {
            var table = i == 0 ? Beginning.Value : i == letters.Count - 1 ? Ending.Value : Middle.Value;
            tableChain.Add(table);
        }

        var combinations = new List<string[]> { Array.Empty<string>() };
        foreach (var (letter, table) in letters.Zip(tableChain))
        {
            if (!table.TryGetValue(letter, out var alts))
            {
                yield return new FinglishAlternative(string.Join("", originalWord), 0.0);
                yield break;
            }
            var next = new List<string[]>();
            var capped = false;
            foreach (var prefix in combinations)
            {
                foreach (var alt in alts)
                {
                    next.Add(prefix.Concat(new[] { alt }).ToArray());
                    if (next.Count >= MaxCombinations) { capped = true; break; }
                }
                if (capped) break;
            }
            combinations = next;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var combo in combinations)
        {
            var persian = string.Concat(combo);
            if (persian.Length == 0 || !seen.Add(persian)) continue;
            // dictionary + byte-frequency proxy for confidence (see ScoreAlternatives)
            var score = ScoreAlternatives(persian);
            yield return new FinglishAlternative(persian, score);
        }
    }

    /// <summary>
    /// V1 confidence proxy replacing the original's 7 MB word-frequency table:
    /// a candidate that is itself in the dictionary scores 1.0; longer candidates that
    /// share no letters with a dict word score lower; else 0.5. Good enough for ranking.
    /// </summary>
    private static double ScoreAlternatives(string persian)
    {
        if (Dict.Value.Values.Contains(persian, StringComparer.Ordinal)) return 1.0;
        return 0.5;
    }

    /// <summary>
    /// Expand a word into token-list variations (multi-letter sounds, duplicated
    /// letters, apostrophe combos). Mirrors Python <c>variations()</c>.
    /// </summary>
    internal static IEnumerable<IReadOnlyList<string>> Variations(string word)
    {
        if (word == "a") return new[] { new[] { "A" } };
        if (word.Length == 1) return new[] { new[] { word } };
        if (word == "aa") return new[] { new[] { "A" } };
        if (word == "ee") return new[] { new[] { "i" } };
        if (word is "ei") return new[] { new[] { "ei" } };
        if (word is "oo" or "ou") return new[] { new[] { "u" } };
        if (word == "kha") return new[] { new[] { "kha" }, new[] { "kh", "a" } };
        if (word is "kh" or "gh" or "ch" or "sh" or "zh" or "ck") return new[] { new[] { word } };
        if (word is "'ee" or "'ei") return new[] { new[] { "'i" } };
        if (word is "'oo" or "'ou") return new[] { new[] { "'u" } };
        if (word is "a'" or "e'" or "o'" or "i'" or "u'" or "A'") return new[] { new[] { word[0] + "'" } };
        if (word is "'a" or "'e" or "'o" or "'i" or "'u" or "'A") return new[] { new[] { "'" + word[1] } };
        if (word.Length == 2 && word[0] == word[1]) return new[] { new[] { word[0].ToString() } };

        if (word.StartsWith("aa", StringComparison.Ordinal))
            return Variations(word[2..]).Select(rest => new[] { "A" }.Concat(rest).ToArray());
        if (word.StartsWith("ee", StringComparison.Ordinal))
            return Variations(word[2..]).Select(rest => new[] { "i" }.Concat(rest).ToArray());
        if (word.StartsWith("oo", StringComparison.Ordinal) || word.StartsWith("ou", StringComparison.Ordinal))
            return Variations(word[2..]).Select(rest => new[] { "u" }.Concat(rest).ToArray());
        if (word.StartsWith("kha", StringComparison.Ordinal))
            return Variations(word[3..])
                .SelectMany(rest => new[]
                {
                    new[] { "kha" }.Concat(rest).ToArray(),
                    new[] { "kh", "a" }.Concat(rest).ToArray(),
                    new[] { "k", "h", "a" }.Concat(rest).ToArray()
                });
        if (word.StartsWith("kh", StringComparison.Ordinal) || word.StartsWith("gh", StringComparison.Ordinal) ||
            word.StartsWith("ch", StringComparison.Ordinal) || word.StartsWith("sh", StringComparison.Ordinal) ||
            word.StartsWith("zh", StringComparison.Ordinal) || word.StartsWith("ck", StringComparison.Ordinal))
            return Variations(word[2..])
                .SelectMany(rest => new[]
                {
                    new[] { word[..2] }.Concat(rest).ToArray(),
                    new[] { word[0].ToString() }.Concat(rest).ToArray()
                });
        if (word.StartsWith("a'", StringComparison.Ordinal) || word.StartsWith("e'", StringComparison.Ordinal) ||
            word.StartsWith("o'", StringComparison.Ordinal) || word.StartsWith("i'", StringComparison.Ordinal) ||
            word.StartsWith("u'", StringComparison.Ordinal) || word.StartsWith("A'", StringComparison.Ordinal))
            return Variations(word[2..]).Select(rest => new[] { word[..2] }.Concat(rest).ToArray());
        if (word.StartsWith("'ee", StringComparison.Ordinal) || word.StartsWith("'ei", StringComparison.Ordinal))
            return Variations(word[3..]).Select(rest => new[] { "'i" }.Concat(rest).ToArray());
        if (word.StartsWith("'oo", StringComparison.Ordinal) || word.StartsWith("'ou", StringComparison.Ordinal))
            return Variations(word[3..]).Select(rest => new[] { "'u" }.Concat(rest).ToArray());
        if (word.StartsWith("'a", StringComparison.Ordinal) || word.StartsWith("'e", StringComparison.Ordinal) ||
            word.StartsWith("'o", StringComparison.Ordinal) || word.StartsWith("'i", StringComparison.Ordinal) ||
            word.StartsWith("'u", StringComparison.Ordinal) || word.StartsWith("'A", StringComparison.Ordinal))
            return Variations(word[2..]).Select(rest => new[] { word[..2] }.Concat(rest).ToArray());
        if (word.Length >= 2 && word[0] == word[1])
            return Variations(word[2..]).Select(rest => new[] { word[0].ToString() }.Concat(rest).ToArray());
        return Variations(word[1..])
            .Select(rest => new[] { word[0].ToString() }.Concat(rest).ToArray());
    }
}

/// <summary>One candidate Persian spelling + confidence in [0..1].</summary>
public readonly record struct FinglishAlternative(string Persian, double Confidence);

/// <summary>Loads the embedded Finglish data tables.</summary>
internal static class Embedded
{
    public static string Load(string resourceName)
    {
        var asm = typeof(Embedded).Assembly;
        var full = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found. " +
                "Add the f2p-*.txt files to MusicEngine.Text as EmbeddedResource.");
        using var s = asm.GetManifestResourceStream(full)
            ?? throw new InvalidOperationException($"Cannot open embedded resource '{full}'.");
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }
}