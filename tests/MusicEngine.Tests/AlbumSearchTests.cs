namespace MusicEngine.Tests;

using System.Runtime.CompilerServices;
using Models;
using Search;
using Xunit;

/// <summary>Album search (album mode): \"tataloo jahanam\" expands to the whole
/// album instead of one song. Fully offline — fake catalog + retrieval providers.</summary>
public class AlbumSearchTests
{
    private static readonly List<SearchResult> JahanamTracks = new()
    {
        AlbumTrack(1, "Jahanam", "Amir Tataloo", "1001", "Jahanam"),
        AlbumTrack(2, "Man Bahet Ghahram", "Amir Tataloo", "1001", "Jahanam"),
        AlbumTrack(3, "Nakon Deleto Tang", "Amir Tataloo", "1001", "Jahanam"),
    };

    [Fact]
    public async Task AlbumQueryExpandsToAllTracksInOrder()
    {
        var catalog = new FakeProvider(SearchTier.Catalog, ProviderId.ITunes, JahanamTracks, isAlbumProvider: true);
        // RJ-like retrieval: the album's tracks plus unrelated singles by the
        // same artist — the singles must NOT leak into the album.
        var singles = new[]
        {
            AlbumTrack(0, "Behesht", "Amir Tataloo", "9001", "Behesht - Single"),
            AlbumTrack(0, "Man", "Amir Tataloo", "9002", null),
        };
        var rj = new FakeProvider(SearchTier.Display, ProviderId.RadioJavan,
            JahanamTracks.Concat(singles).ToList(), isAlbumProvider: true);

        var search = new SearchService(new ISearchProvider[] { catalog, rj });
        var works = await search.RunAsync("tataloo jahanam", null);

        Assert.Equal(3, works.Count);
        Assert.Equal("Jahanam", works[0].Title);
        Assert.Equal("Man Bahet Ghahram", works[1].Title);
        Assert.Equal("Nakon Deleto Tang", works[2].Title);
        // The unrelated singles ("Behesht", "Man") must NOT leak in — "Man" is
        // a token of "Man Bahet Ghahram", which used to slip it past the gate.
        Assert.DoesNotContain(works, w => w.Title == "Behesht" || w.Title == "Man");
        Assert.All(works, w => Assert.Equal("Jahanam", w.Goal.Album));
        Assert.All(works, w => Assert.Equal("Amir Tataloo", w.Goal.Artist));
    }

    [Fact]
    public async Task PersianAlbumDetectedFromRetrievalRowsWhenCatalogsLackIt()
    {
        var catalog = new FakeProvider(SearchTier.Catalog, ProviderId.ITunes, new List<SearchResult>());
        var sokoot = new List<SearchResult>
        {
            AlbumTrack(1, "Sokoot", "Bahram", "2002", "Sokoot"),
            AlbumTrack(2, "Hame Javoonan", "Bahram", "2002", "Sokoot"),
            AlbumTrack(3, "Donya", "Bahram", "2002", "Sokoot"),
        };
        var rj = new FakeProvider(SearchTier.Display, ProviderId.RadioJavan, sokoot, isAlbumProvider: true);

        var search = new SearchService(new ISearchProvider[] { catalog, rj });
        var works = await search.RunAsync("bahram sokot", null); // fuzzy album spelling

        Assert.Equal(3, works.Count);
        Assert.Equal("Sokoot", works[0].Goal.Album);
    }

    [Fact]
    public async Task SongQueryStaysSongMode()
    {
        // Catalog rows come from DIFFERENT albums — no album grouping, so the
        // query must resolve as a single song and Goal.Album stays null.
        var catalogRows = new List<SearchResult>
        {
            AlbumTrack(1, "Behesht", "Amir Tataloo", "7001", "Album A"),
            AlbumTrack(2, "Qahreman", "Amir Tataloo", "7002", "Album B"),
        };
        var catalog = new FakeProvider(SearchTier.Catalog, ProviderId.ITunes, catalogRows, isAlbumProvider: true);
        var song = new List<SearchResult>
        {
            AlbumTrack(0, "Behesht", "Amir Tataloo", null, "Behesht - Single"),
        };
        var rj = new FakeProvider(SearchTier.Display, ProviderId.RadioJavan, song);

        var search = new SearchService(new ISearchProvider[] { catalog, rj });
        var works = await search.RunAsync("tataloo behesht", null);

        var work = Assert.Single(works);
        Assert.Null(work.Goal.Album);
        Assert.Equal("Behesht", work.Goal.Title);
    }

    private static SearchResult AlbumTrack(int trackNumber, string title, string artist,
        string? albumId, string? album) => new()
    {
        Provider = ProviderId.RadioJavan,
        Id = $"t-{albumId ?? "?"}-{title}",
        Metadata = new TrackMetadata
        {
            Title = title,
            Artist = artist,
            Album = album,
            AlbumId = albumId,
            TrackNumber = trackNumber > 0 ? trackNumber : null,
            Duration = TimeSpan.FromSeconds(180),
        },
        DirectStreamUri = new Uri($"http://cdn.test/{title}.mp3"),
        MaxQuality = StreamQuality.High192K,
        Downloadable = true,
    };

    /// <summary>A canned provider: catalog rows (preview-like when in Catalog
    /// tier) or retrieval rows, with optional album expansion.</summary>
    private sealed class FakeProvider : ISearchProvider, IAlbumProvider
    {
        private readonly SearchTier _tier;
        private readonly IReadOnlyList<SearchResult> _rows;
        private readonly bool _isAlbumProvider;

        public FakeProvider(SearchTier tier, ProviderId id, IReadOnlyList<SearchResult> rows, bool isAlbumProvider = false)
        {
            _tier = tier;
            Id = id;
            _rows = rows;
            _isAlbumProvider = isAlbumProvider;
        }

        public ProviderId Id { get; }
        public string DisplayName => "Fake-" + Id;
        public SearchTier Tier => _tier;
        public bool IsAvailable => true;

        public async IAsyncEnumerable<SearchResult> SearchAsync(
            string query, int maxResults = 25,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var r in _rows)
            {
                ct.ThrowIfCancellationRequested();
                yield return r;
            }
        }

        public Task<IReadOnlyList<SearchResult>> GetAlbumTracksAsync(AlbumRef album, CancellationToken ct = default)
        {
            if (!_isAlbumProvider) return Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());
            return Task.FromResult<IReadOnlyList<SearchResult>>(_rows
                .Where(r => r.Metadata.AlbumId == album.Id)
                .ToList());
        }
    }
}
