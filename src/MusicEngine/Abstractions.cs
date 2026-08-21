namespace MusicEngine;

using Models;

/// <summary>
/// A music source. A provider implements only the roles it can satisfy:
/// <see cref="ISearchProvider"/> and/or <see cref="IDownloadProvider"/> and/or <see cref="IPreviewProvider"/>.
/// </summary>
public interface IMusicProvider
{
    ProviderId Id { get; }
    string DisplayName { get; }
}

/// <summary>Search tier: 1 = fast catalog (metadata/preview), 2 = fast retrieval (display rows), 3 = slow scrapers (download resolution only).</summary>
public enum SearchTier
{
    Catalog = 1,
    Display = 2,
    DownloadOnly = 3,
}

public interface ISearchProvider : IMusicProvider
{
    SearchTier Tier { get; }

    /// <summary>True when the provider is currently usable (binary present, sidecar installed, …).</summary>
    bool IsAvailable { get; }

    IAsyncEnumerable<SearchResult> SearchAsync(
        string query,
        int maxResults,
        CancellationToken ct = default);
}

public interface IDownloadProvider : IMusicProvider
{
    bool IsAvailable { get; }

    /// <summary>True when this provider knows how to fetch the given result (native URL/host).</summary>
    bool CanDownload(SearchResult result);

    Task<DownloadResult> DownloadAsync(
        SearchResult track,
        DownloadOptions options,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default);
}

/// <summary>Preview-only sources (iTunes/Deezer 30s streams) so the UI can offer playback.</summary>
public interface IPreviewProvider : IMusicProvider
{
    Uri? GetPreviewStreamUri(SearchResult track);
}

/// <summary>One identified album — the expansion key for album search.</summary>
public sealed record AlbumRef(
    string Id,
    string Name,
    string Artist,
    ProviderId Provider);

/// <summary>
/// A source that can expand an album into its full track list (album search).
/// Implemented by iTunes/Deezer (lookup by album id) and Radio Javan (search-based,
/// best effort). Results are ordinary downloadable SearchResults carrying the
/// album's metadata and track numbers.
/// </summary>
public interface IAlbumProvider : IMusicProvider
{
    Task<IReadOnlyList<SearchResult>> GetAlbumTracksAsync(AlbumRef album, CancellationToken ct = default);
}

/// <summary>An album found by probing a query — the discovery trigger for album mode.</summary>
public sealed record AlbumCandidate(
    AlbumRef Album,
    IReadOnlyList<SearchResult> Tracks);

/// <summary>
/// A source that can DISCOVER an album from a raw query (album search) — unlike
/// <see cref="IAlbumProvider"/>, which expands an already-identified album. This
/// is the path for Persian albums that live on YouTube playlists: the catalogs
/// (iTunes/Deezer) index them as singles or not at all, and Radio Javan's search
/// only surfaces one track per album. Implemented by the YouTube provider.
/// </summary>
public interface IAlbumDiscovery : IMusicProvider
{
    /// <summary>Probe the query for a matching album; null when none found.</summary>
    Task<AlbumCandidate?> FindAlbumAsync(string query, CancellationToken ct = default);
}

public interface IDispatcher
{
    void Run(Action action);
}

public interface IArtworkLoader
{
    Task<byte[]?> LoadAsync(Uri uri, CancellationToken ct = default);
}
