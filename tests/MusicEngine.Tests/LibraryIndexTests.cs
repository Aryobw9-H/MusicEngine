namespace MusicEngine.Tests;

using Audio;
using Configuration;
using Models;
using Text;
using Xunit;

/// <summary>FEAT-03: the on-disk library index — tags first, filename fallback,
/// cross-script keys, and Add/rebuild behaviour. Fully offline (temp dirs).</summary>
public class LibraryIndexTests
{
    [Fact]
    public async Task TaggedFilesAreIndexedCrossScript()
    {
        using var fx = new Fixture();
        WriteTagged(fx.Path("tataloo-behesht.mp3"), "تتلو", "بهشت");

        using var index = new LibraryIndex(fx.Settings);
        await index.BuildAsync();

        Assert.True(index.Contains("تتلو", "بهشت"));
        // The stored keys include the Finglish→Persian conversion of the Latin
        // spelling, so querying however the user types it matches.
        var latinQuery = TrackTextNormalizer.Normalize(FinglishConverter.Convert("tataloo behesht"));
        Assert.True(index.Contains("tataloo", "behesht") || index.Contains("", latinQuery));
    }

    [Fact]
    public async Task UntaggedFilesFallBackToFilenameParsing()
    {
        using var fx = new Fixture();
        // Garbage bytes — not a real MP3, so tag reading fails and the
        // "Artist - Title" filename split must carry the index.
        File.WriteAllBytes(fx.Path("Sijal - Bargard.mp3"), new byte[] { 0x00, 0xFF, 0xFE, 0x42 });

        using var index = new LibraryIndex(fx.Settings);
        await index.BuildAsync();

        Assert.True(index.Contains("Sijal", "Bargard"));
        Assert.False(index.Contains("Sijal", "Other"));
    }

    [Fact]
    public async Task AddRegistersWithoutWaitingForRescan()
    {
        using var fx = new Fixture();
        using var index = new LibraryIndex(fx.Settings);
        await index.BuildAsync();
        Assert.False(index.Contains("مهرزاد", "منو نترسون"));

        WriteTagged(fx.Path("new.mp3"), "مهرزاد", "منو نترسون");
        index.Add(fx.Path("new.mp3")); // the RecordHistory path

        Assert.True(index.Contains("مهرزاد", "منو نترسون"));
    }

    [Fact]
    public async Task RebuildDropsDeletedFiles()
    {
        using var fx = new Fixture();
        var path = fx.Path("Sijal - Bargard.mp3");
        File.WriteAllBytes(path, new byte[] { 0x00, 0xFF, 0xFE, 0x42 });

        using var index = new LibraryIndex(fx.Settings);
        await index.BuildAsync();
        Assert.True(index.Contains("Sijal", "Bargard"));

        File.Delete(path);
        await index.RebuildAsync();
        Assert.False(index.Contains("Sijal", "Bargard"));
    }

    private static void WriteTagged(string path, string artist, string title)
    {
        // Minimal valid MPEG-1 Layer III frames so TagLib can open the file,
        // then tag it the same way TrackTagger tags a finished download.
        var header = new byte[] { 0xFF, 0xFB, 0x90, 0x64 }; // MPEG1 L3, 128 kbps, 44.1 kHz
        using (var fs = File.Create(path))
        {
            for (var i = 0; i < 3; i++)
            {
                fs.Write(header, 0, 4);
                fs.Write(new byte[417]); // one full frame body
            }
        }
        using var f = TagLib.File.Create(path);
        f.Tag.Performers = new[] { artist };
        f.Tag.Title = title;
        f.Save();
    }

    private sealed class Fixture : IDisposable
    {
        public string Dir { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "musicengine-lib-" + Guid.NewGuid().ToString("N")[..8]);

        public TestSettings Settings { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Dir);
            Settings = new TestSettings { OutputDirectory = Dir };
        }

        public string Path(string name) => System.IO.Path.Combine(Dir, name);

        public void Dispose()
        {
            try { Directory.Delete(Dir, true); } catch { }
        }
    }
}
