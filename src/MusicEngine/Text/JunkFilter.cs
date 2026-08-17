namespace MusicEngine.Text;

using System.Text.RegularExpressions;

/// <summary>
/// Junk detection for result titles: numeric-only, download-page boilerplate,
/// reaction/meme videos, emoji-only titles. Applied before anything reaches the UI.
/// </summary>
public static class JunkFilter
{
    private static readonly string[] JunkPhrases =
    {
        "contact us", "release music", "music library", "subscribe", "full album",
        "best of", "top hits", "mix 2025", "mix 2026", "1 hour", "hour mix",
    };
    private static readonly string[] PersianJunkExact = { "دانلود آهنگ", "ویدیو", "کلیپ", "دانلود", "آلبوم" };
    private static readonly string[] VersionMarkers =
    {
        "music video", "official video", "موزیک ویدیو", "موزیک ویدئو", "اهنگ ویدیو",
        "video clip", "ویدیو کلیپ", "متن آهنگ", "lyrics", "ریمیکس", "remix",
        "live", "زنده", "آکوستیک", "acoustic", "کنسرت", "concert",
    };
    private static readonly string[] PersianJunkContains =
    {
        "آموزش", "تیزر", "تریلر", "مصاحبه", "نوشتن متن", "ضبط", "پشت صحنه", "راستوری", "رستوری",
    };
    private static readonly Regex NumericOnlySuffix = new(@"^\d+(?:\s*[a-zA-Z]{1,3})?$", RegexOptions.Compiled);
    private static readonly Regex MemeReaction = new(@"\b(reaction|ری اکشن|واکنش)\b|reactions", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PersianMemePatterns = new(@"(بغض|گریه|اشک|دلم گرفت|قلبم|واکنش)", RegexOptions.Compiled);

    public static bool IsJunkTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return true;
        var tLower = title.ToLowerInvariant().Trim();

        if (NumericOnlySuffix.IsMatch(tLower)) return true;
        if (JunkPhrases.Any(j => tLower.Contains(j, StringComparison.Ordinal))) return true;
        if (PersianJunkExact.Contains(tLower)) return true;
        if (PersianJunkContains.Any(j => tLower.Contains(j, StringComparison.Ordinal))) return true;
        // Version markers are never the MAIN song title; works carry them as labels.
        if (VersionMarkers.Any(m => tLower.Contains(m, StringComparison.Ordinal))) return true;
        if (MemeReaction.IsMatch(tLower)) return true;
        if (PersianMemePatterns.IsMatch(title)) return true;

        // Titles dominated by emoji/dingbats (💔😭🙏🔥) are reactions, not songs.
        var letters = new string(title.Where(c => char.IsLetter(c) && c < 0x1F000).ToArray());
        if (letters.Length > 0 && letters.Length < 3 && title.Any(c => c >= 0x1F000)) return true;

        if (title.Count(char.IsLetterOrDigit) < 3) return true;
        return false;
    }

    public static bool IsJunkChannel(string channelNameOrUrl)
    {
        if (string.IsNullOrWhiteSpace(channelNameOrUrl)) return false;
        var t = channelNameOrUrl.ToLowerInvariant();
        return t.Contains("reaction") || t.Contains("ری اکشن");
    }
}
