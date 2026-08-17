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
