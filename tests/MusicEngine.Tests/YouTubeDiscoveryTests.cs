namespace MusicEngine.Tests;

using System.Runtime.CompilerServices;
using Models;
using Providers;
using Search;
using Xunit;

/// <summary>Album discovery (IAlbumDiscovery): Persian albums that live on YouTube
/// playlists (\"fadaei hagh\", \"tataloo jahanam\") are probed by the discovery source
/// when the catalogs can't surface them. Fully offline — fake discovery provider.</summary>
public class YouTubeDiscoveryTests
{
    [Fact]
    public void PlaylistTitleScoring_RequiresAllQueryTokens()
    {
        // Both query tokens must appear in the title — a "jahanam"-themed mix
        // must not claim "tataloo jahanam".
        Assert.True(YouTubeProvider.ScorePlaylistTitle("Amir Tataloo - Jahanam (Full Album)", "tataloo jahanam") >= 3);
        Assert.True(YouTubeProvider.ScorePlaylistTitle("Fadaei - Hagh آلبوم کامل", "fadaei hagh") >= 3);
        Assert.True(YouTubeProvider.ScorePlaylistTitle("Bahram - Sokoot (Album)", "bahram sokot") >= 3);
        // Artist token missing → no match.
        Assert.Equal(0, YouTubeProvider.ScorePlaylistTitle("Sokoot - The Collection", "bahram sokot"));
        Assert.Equal(0, YouTubeProvider.ScorePlaylistTitle("Random Jahanam Mix", "tataloo jahanam"));
    }

    [Fact]
    public async Task AlbumQueryExpandsFromDiscoveryWhenCatalogsLackIt()
    {
        // Empty catalogs (iTunes/Deezer have nothing for the Persian album).
        var catalog = new DiscoveryFake(SearchTier.Catalog, ProviderId.ITunes, new List<SearchResult>());
        // Retrieval tier returns nothing useful either.
        var rj = new DiscoveryFake(SearchTier.Display, ProviderId.RadioJavan, new List<SearchResult>());
        // The discovery source (YouTube) finds the album playlist and expands it.
        var discovery = new DiscoveryFake(SearchTier.Display, ProviderId.YouTube, new List<SearchResult>())
        {
            AlbumTracks = new List<SearchResult>
            {
                AlbumTrack(1, "Jahanam", "Amir Tataloo"),
                AlbumTrack(2, "Man Bahet Ghahram", "Amir Tataloo"),
                AlbumTrack(3, "Nakon Deleto Tang", "Amir Tataloo"),
                AlbumTrack(4, "Pishgooyi", "Amir Tataloo"),
            },
            AlbumName = "Jahanam",
            AlbumArtist = "Amir Tataloo",
        };

        SearchService.DebugPhases = true;
        var search = new SearchService(new ISearchProvider[] { catalog, rj, discovery });
        var works = await search.RunAsync("tataloo jahanam", null);
        SearchService.DebugPhases = false;

        Assert.Equal(4, works.Count);
        Assert.Equal("Jahanam", works[0].Goal.Album);
        Assert.Equal("Jahanam", works[0].Title);
        Assert.Equal("Man Bahet Ghahram", works[1].Title);
        Assert.Equal("Nakon Deleto Tang", works[2].Title);
        Assert.Equal("Pishgooyi", works[3].Title);
        Assert.All(works, w => Assert.Equal("Amir Tataloo", w.Goal.Artist));
    }

    [Fact]
    public async Task AlbumTrackSongQueryIsNotHijackedByDiscovery()
    {
        // A song query the catalogs resolve as a real ALBUM-TRACK song ("coldplay
        // yellow" — "Yellow" lives on the "Parachutes" album) must stay song mode
        // even when discovery finds a matching-titled playlist.
        var catalogRows = new List<SearchResult>
        {
            AlbumTrackRow("Yellow", "Coldplay", "Parachutes", "9001"),
        };
        var catalog = new DiscoveryFake(SearchTier.Catalog, ProviderId.ITunes, catalogRows);
        // A retrieval copy of the SONG (like the existing song-mode test) — a
        // song query has downloadable copies, so the pipeline resolves it as a
        // song and never reaches the zero-result rescue path.
        var rj = new DiscoveryFake(SearchTier.Display, ProviderId.RadioJavan, new List<SearchResult>
        {
            new()
            {
                Provider = ProviderId.RadioJavan,
                Id = "rj-yellow",
                Metadata = new TrackMetadata
                {
                    Title = "Yellow",
                    Artist = "Coldplay",
                    Duration = TimeSpan.FromSeconds(180),
                },
                DirectStreamUri = new Uri("http://cdn.test/yellow.mp3"),
                MaxQuality = StreamQuality.High192K,
                Downloadable = true,
            },
        });
        var discovery = new DiscoveryFake(SearchTier.Display, ProviderId.YouTube, new List<SearchResult>())
        {
            AlbumTracks = new List<SearchResult>
            {
                AlbumTrack(1, "Yellow", "Coldplay"),
                AlbumTrack(2, "Shiver", "Coldplay"),
                AlbumTrack(3, "Spies", "Coldplay"),
            },
            AlbumName = "Parachutes",
            AlbumArtist = "Coldplay",
        };

        var search = new SearchService(new ISearchProvider[] { catalog, rj, discovery });
        var works = await search.RunAsync("coldplay yellow", null);

        // The catalog resolved "Yellow" as an album-track song — discovery must
        // NOT flip this into a 3-track album.
        var work = Assert.Single(works);
        Assert.Null(work.Goal.Album);
        Assert.Equal("Yellow", work.Goal.Title);
    }

    [Fact]
    public async Task SingleMarkedCatalogDefersToDiscoveredAlbum()
    {
        // iTunes indexes "Jahanam" as "Jahanam - Single" (1-track "album") — the
        // user searching "tataloo jahanam" wants the FULL album, so a discovered
        // playlist takes over even though a single catalog row exists.
        var catalogRows = new List<SearchResult>
        {
            SongTrack("Jahanam", "Amir Tataloo", "9001"),
        };
        var catalog = new DiscoveryFake(SearchTier.Catalog, ProviderId.ITunes, catalogRows);
        var discovery = new DiscoveryFake(SearchTier.Display, ProviderId.YouTube, new List<SearchResult>())
        {
            AlbumTracks = new List<SearchResult>
            {
                AlbumTrack(1, "Jahanam", "Amir Tataloo"),
                AlbumTrack(2, "Man Bahet Ghahram", "Amir Tataloo"),
                AlbumTrack(3, "Nakon Deleto Tang", "Amir Tataloo"),
            },
            AlbumName = "Jahanam",
            AlbumArtist = "Amir Tataloo",
        };

        var search = new SearchService(new ISearchProvider[] { catalog, discovery });
        var works = await search.RunAsync("tataloo jahanam", null);

        Assert.Equal(3, works.Count);
        Assert.Equal("Jahanam", works[0].Goal.Album);
    }

    [Fact]
    public async Task AlbumsOnlyForcesDiscoveryForSingleToken()
    {
        // The Albums toggle probes discovery even for a 1-token query ("jahanam")
        // — auto mode skips the probe (a single token is presumed a song).
        var catalog = new DiscoveryFake(SearchTier.Catalog, ProviderId.ITunes, new List<SearchResult>());
        var discovery = new DiscoveryFake(SearchTier.Display, ProviderId.YouTube, new List<SearchResult>())
        {
            AlbumTracks = new List<SearchResult>
            {
                AlbumTrack(1, "Jahanam", "Amir Tataloo"),
                AlbumTrack(2, "Man Bahet Ghahram", "Amir Tataloo"),
                AlbumTrack(3, "Nakon Deleto Tang", "Amir Tataloo"),
            },
            AlbumName = "Jahanam",
            AlbumArtist = "Amir Tataloo",
        };

        var search = new SearchService(new ISearchProvider[] { catalog, discovery });

        // Auto mode: single token → no discovery probe → nothing surfaces.
        var autoWorks = await search.RunAsync("jahanam", null);
        Assert.Empty(autoWorks);

        // Albums toggle: discovery runs and the playlist becomes the album.
        var albumWorks = await search.RunAsync("jahanam", null, CancellationToken.None, albumsOnly: true);
        Assert.Equal(3, albumWorks.Count);
        Assert.Equal("Jahanam", albumWorks[0].Goal.Album);
    }

    [Fact]
    public async Task AlbumsOnlyOverridesCatalogConfirmedSong()
    {
        // "coldplay yellow" resolves as an album-track song in auto mode (discovery
        // must not hijack it) — but with the Albums toggle the user explicitly
        // asked for the album, so the discovered playlist wins unconditionally.
        var catalogRows = new List<SearchResult>
        {
            AlbumTrackRow("Yellow", "Coldplay", "Parachutes", "9001"),
        };
        var catalog = new DiscoveryFake(SearchTier.Catalog, ProviderId.ITunes, catalogRows);
        var discovery = new DiscoveryFake(SearchTier.Display, ProviderId.YouTube, new List<SearchResult>())
        {
            AlbumTracks = new List<SearchResult>
            {
                AlbumTrack(1, "Yellow", "Coldplay", "Parachutes"),
                AlbumTrack(2, "Shiver", "Coldplay", "Parachutes"),
                AlbumTrack(3, "Spies", "Coldplay", "Parachutes"),
            },
            AlbumName = "Parachutes",
            AlbumArtist = "Coldplay",
        };

        var search = new SearchService(new ISearchProvider[] { catalog, discovery });
        var works = await search.RunAsync("coldplay yellow", null, CancellationToken.None, albumsOnly: true);

        Assert.Equal(3, works.Count);
        Assert.Equal("Parachutes", works[0].Goal.Album);
    }

    [Fact]
    public async Task AlbumsOnlyWidensWithVariantQueriesWhenFirstProbeMisses()
    {
        // The first (bounded) probe misses; the album-mode widen pass re-probes
        // with album-oriented query variants and finds the playlist.
        var catalog = new DiscoveryFake(SearchTier.Catalog, ProviderId.ITunes, new List<SearchResult>());
        var discovery = new DiscoveryFake(SearchTier.Display, ProviderId.YouTube, new List<SearchResult>())
        {
            // First 4 FindAlbumAsync calls (raw + 3 variants) return null; the
            // widen pass's next call finds the album.
            NullUntilCall = 4,
            AlbumTracks = new List<SearchResult>
            {
                AlbumTrack(1, "Hagh", "Fadaei"),
                AlbumTrack(2, "Maah", "Fadaei"),
                AlbumTrack(3, "Shab", "Fadaei"),
            },
            AlbumName = "Hagh",
            AlbumArtist = "Fadaei",
        };

        var search = new SearchService(new ISearchProvider[] { catalog, discovery });
        var works = await search.RunAsync("fadaei hagh", null, CancellationToken.None, albumsOnly: true);

        Assert.Equal(3, works.Count);
        Assert.Equal("Hagh", works[0].Goal.Album);
        Assert.True(discovery.CallsToFind > 4, "widen pass should re-probe discovery");
    }

    private static SearchResult SongTrack(string title, string artist, string id) => new()
    {
        Provider = ProviderId.ITunes,
        Id = id,
        Metadata = new TrackMetadata
        {
            Title = title,
            Artist = artist,
            Duration = TimeSpan.FromSeconds(180),
            Album = $"{title} - Single",
        },
        MaxQuality = StreamQuality.Standard128K,
        Downloadable = true,
    };

    private static SearchResult AlbumTrackRow(string title, string artist, string album, string id) => new()
    {
        Provider = ProviderId.ITunes,
        Id = id,
        Metadata = new TrackMetadata
        {
            Title = title,
            Artist = artist,
            Duration = TimeSpan.FromSeconds(180),
            Album = album,
        },
        MaxQuality = StreamQuality.Standard128K,
        Downloadable = true,
    };

    private static SearchResult AlbumTrack(int trackNumber, string title, string artist, string album = "Jahanam") => new()
    {
        Provider = ProviderId.YouTube,
        Id = $"yt-{trackNumber}-{title}",
        Metadata = new TrackMetadata
        {
            Title = title,
            Artist = artist,
            Album = album,
            AlbumId = "yt:" + album,
            TrackNumber = trackNumber,
            Duration = TimeSpan.FromSeconds(210),
        },
        MaxQuality = StreamQuality.High192K,
        SourceUrl = $"https://www.youtube.com/watch?v={trackNumber}-{title}",
        Downloadable = true,
    };

    /// <summary>A canned provider that can also act as the album-discovery source.</summary>
    private sealed class DiscoveryFake : ISearchProvider, IAlbumDiscovery
    {
        private readonly SearchTier _tier;
        private readonly IReadOnlyList<SearchResult> _rows;

        public DiscoveryFake(SearchTier tier, ProviderId id, IReadOnlyList<SearchResult> rows)
        {
            _tier = tier;
            Id = id;
            _rows = rows;
        }

        public ProviderId Id { get; }
        public string DisplayName => "Fake-" + Id;
        public SearchTier Tier => _tier;
        public bool IsAvailable => true;

        public IReadOnlyList<SearchResult> AlbumTracks { get; init; } = Array.Empty<SearchResult>();
        public string AlbumName { get; init; } = "";
        public string AlbumArtist { get; init; } = "";

        /// <summary>Simulate a slow/missed probe: the first N FindAlbumAsync
        /// calls return null, modelling the first pass failing and the widen
        /// pass succeeding.</summary>
        public int NullUntilCall { get; init; }
        public int CallsToFind { get; private set; }

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

        public Task<AlbumCandidate?> FindAlbumAsync(string query, CancellationToken ct = default)
        {
            CallsToFind++;
            if (CallsToFind <= NullUntilCall || AlbumTracks.Count < 3)
                return Task.FromResult<AlbumCandidate?>(null);
            return Task.FromResult<AlbumCandidate?>(new AlbumCandidate(
                new AlbumRef("yt:pl-" + AlbumName, AlbumName, AlbumArtist, ProviderId.YouTube),
                AlbumTracks));
        }
    }
}
