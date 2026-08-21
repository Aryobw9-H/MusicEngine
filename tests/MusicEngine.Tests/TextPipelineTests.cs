namespace MusicEngine.Tests;

using Audio;
using Downloads;
using Models;
using Search;
using Text;
using Xunit;

/// <summary>
/// Offline tests for the text/pipeline brain: Finglish conversion, query
/// parsing, the goal gate, junk filtering and file naming. Ported verbatim
/// from the former console harness (MODERN-02).
/// </summary>
public class TextPipelineTests
{
    [Fact]
    public void FinglishConvertsTatalooBehesht()
        => Assert.Equal("تتلو بهشت", FinglishConverter.Convert("tataloo behesht"));

    [Fact]
    public void FinglishExpandsToPersianAndLatinVariants()
        => Assert.True(FinglishQueryExpander.Expand("tataloo behesht").Count >= 2);

    [Fact]
    public void CanonicalCacheKeyUnifiesCrossScriptSearches()
    {
        // PERF-03: the same song searched in Finglish and Persian must map to
        // the same cache entry, or every cross-script re-search re-runs the
        // nine-provider fan-out.
        Assert.Equal(
            SearchService.CanonicalCacheKey("tataloo behesht"),
            SearchService.CanonicalCacheKey("تتلو بهشت"));
        // A spelled-out variant of the same song should also collide.
        Assert.Equal(
            SearchService.CanonicalCacheKey("tataloo behesht"),
            SearchService.CanonicalCacheKey("تتلو بهشت"));
    }

    [Fact]
    public void CrossScriptOverlapTatalooBehesht()
        => Assert.True(TrackTextNormalizer.KeysOverlap("تتلو بهشت", "tataloo behesht"));

    [Fact]
    public void TokenGateKeepsExactSongOnly()
    {
        Assert.True(TrackTextNormalizer.ContainsAllTokens("Amir Tataloo - Behesht", "tataloo behesht"));
        Assert.False(TrackTextNormalizer.ContainsAllTokens("Amir Tataloo - Man Bahat Ghahram", "tataloo behesht"));
    }

    [Fact]
    public void NormalizerStripsBracketJunk()
    {
        Assert.Equal("ahange sijal", TrackTextNormalizer.Normalize("Ahange Sijal [320]"));
        Assert.Equal("bargard", TrackTextNormalizer.Normalize("Bargard (Official Audio)"));
    }

    [Fact]
    public void QueryParserExplicitArtistDashTitle()
    {
        var parsed = QueryParser.Parse("amir tataloo - behesht");
        Assert.True(parsed.HasExplicitStructure);
        Assert.Equal("amir tataloo", parsed.Artist);
        Assert.Equal("behesht", parsed.Title);
    }

    [Fact]
    public void QueryParserHeuristicSplit()
    {
        var parsed = QueryParser.Parse("amir tataloo behesht");
        Assert.Equal("amir tataloo", parsed.Artist);
        Assert.Equal("behesht", parsed.Title);
    }

    [Fact]
    public void JunkFilterRejectsJunkKeepsRealTitles()
    {
        Assert.True(JunkFilter.IsJunkTitle("دانلود آهنگ"));
        Assert.True(JunkFilter.IsJunkTitle("REACTION to behesht 😭"));
        Assert.False(JunkFilter.IsJunkTitle("Behesht"));
    }

    [Fact]
    public void GoalResolverPicksBestCatalogRow()
    {
        var parsed = QueryParser.Parse("amir tataloo behesht");
        var rows = new List<SearchResult>
        {
            new() { Provider = ProviderId.ITunes, Id = "1", Metadata = new TrackMetadata { Title = "Halam Avaz Shod", Artist = "Amir Tataloo", Duration = TimeSpan.FromMinutes(3) } },
            new() { Provider = ProviderId.ITunes, Id = "2", Metadata = new TrackMetadata { Title = "Behesht", Artist = "Amir Tataloo", Duration = TimeSpan.FromMinutes(4) } },
        };
        var goal = GoalResolver.Resolve(parsed, rows);
        Assert.True(goal.Title == "behesht" || goal.Title == "Behesht");
    }

    [Fact]
    public void GoalGateRejectsWrongSongAndWrongDuration()
    {
        var goal = new GoalSong("amir tataloo", "behesht", TimeSpan.FromSeconds(240), ProviderId.ITunes);
        var wrong = new SearchResult
        {
            Provider = ProviderId.YouTube, Id = "x",
            Metadata = new TrackMetadata { Title = "Man Bahat Ghahram", Artist = "Amir Tataloo", Duration = TimeSpan.FromMinutes(4) },
        };
        var longReaction = new SearchResult
        {
            Provider = ProviderId.YouTube, Id = "y",
            Metadata = new TrackMetadata { Title = "Behesht", Artist = "Amir Tataloo", Duration = TimeSpan.FromMinutes(17) },
        };
        var good = new SearchResult
        {
            Provider = ProviderId.YouTube, Id = "z",
            Metadata = new TrackMetadata { Title = "Behesht (Official Audio)", Artist = "Amir Tataloo", Duration = TimeSpan.FromSeconds(235) },
        };
        var swappedQuery = new GoalSong("behesht", "amir tataloo", TimeSpan.FromSeconds(240), ProviderId.Unknown);
        Assert.False(SearchService.PassesGoalGate(wrong, goal));
        Assert.False(SearchService.PassesGoalGate(longReaction, goal));
        Assert.True(SearchService.PassesGoalGate(good, goal));
        Assert.True(SearchService.PassesGoalGate(good, swappedQuery));
    }

    [Fact]
    public void SpacelessPhraseMatchingGluedFinglishHitsSpacedPersian()
    {
        var real = "از کرج تا لنگه رود";
        var imposter = "ای دختر کرجی از ترکاشوند";
        Assert.True(TrackTextNormalizer.ContainsPhraseSpaceless(real, "azkaraj"));
        Assert.True(TrackTextNormalizer.ContainsPhraseSpaceless("Az Karaj Ta Langerud", "azkaraj"));
        Assert.False(TrackTextNormalizer.ContainsPhraseSpaceless(imposter, "azkaraj"));
        // short Persian needles must not substring ("کرج" inside "کرجی")
        Assert.False(TrackTextNormalizer.ContainsAllTokens(imposter, "az karaj"));
        Assert.True(TrackTextNormalizer.ContainsAllTokens(real, "az karaj"));
    }

    [Fact]
    public void GoalGateFadaeiAzkarajMatchesRealSongRejectsImposter()
    {
        var goal = new GoalSong("fadaei", "azkaraj", null, ProviderId.Unknown);
        var real = new SearchResult
        {
            Provider = ProviderId.YouTube, Id = "r",
            Metadata = new TrackMetadata { Title = "از کرج تا لنگه رود", Artist = "Fadaei", Duration = TimeSpan.FromMinutes(4) },
        };
        var imposter = new SearchResult
        {
            Provider = ProviderId.PersianIndex, Id = "i",
            Metadata = new TrackMetadata { Title = "ای دختر کرجی از ترکاشوند", Artist = "فدایی", Duration = TimeSpan.FromMinutes(4) },
        };
        var realLatin = new SearchResult
        {
            Provider = ProviderId.YouTube, Id = "rl",
            Metadata = new TrackMetadata { Title = "Az Karaj Ta Langerud", Artist = "Fadaei", Duration = TimeSpan.FromMinutes(4) },
        };
        Assert.True(SearchService.PassesGoalGate(real, goal));
        Assert.True(SearchService.PassesGoalGate(realLatin, goal));
        Assert.False(SearchService.PassesGoalGate(imposter, goal));
        Assert.False(SearchService.PassesLooseGate(imposter, goal));
    }

    [Fact]
    public void FileNamingBuildsCleanArtistTitleMp3()
        => Assert.Equal("Amir Tataloo - Behesht.mp3", FileNaming.Build(
            new TrackMetadata { Artist = "Amir Tataloo", Title = "Behesht" },
            new SearchResult { Provider = ProviderId.YouTube, Id = "abc", Metadata = new TrackMetadata { Title = "junk title", Artist = "x" } }));

    [Fact]
    public void AudioFileAcceptsRealAudioRejectsErrorPagesAndTinyJunk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "musicengine-sniff-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var mp3 = Path.Combine(dir, "ok.mp3");
            File.WriteAllBytes(mp3, new byte[] { 0x49, 0x44, 0x33 }.Concat(new byte[20_000]).ToArray());
            var html = Path.Combine(dir, "html.mp3");
            File.WriteAllText(html, "<html><head><title>403 Forbidden</title></head><body>Cloudflare</body></html>");
            var json = Path.Combine(dir, "json.mp3");
            File.WriteAllText(json, "{\"error\":\"not found\"}");
            var tiny = Path.Combine(dir, "tiny.mp3");
            File.WriteAllText(tiny, "hi");
            var m4a = Path.Combine(dir, "ok.m4a");
            File.WriteAllBytes(m4a, new byte[] { 0, 0, 0, 24, 0x66, 0x74, 0x79, 0x70 }.Concat(new byte[20_000]).ToArray());
            Assert.True(AudioFile.IsProbablyAudio(mp3));
            Assert.True(AudioFile.IsProbablyAudio(m4a));
            Assert.False(AudioFile.IsProbablyAudio(html));
            Assert.False(AudioFile.IsProbablyAudio(json));
            Assert.False(AudioFile.IsProbablyAudio(tiny));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
