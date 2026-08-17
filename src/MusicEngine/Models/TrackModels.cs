namespace MusicEngine.Models;

/// <summary>
/// Provider-agnostic track metadata. Every provider maps its native data onto
/// this shape so ranking, gating, the UI and the tagger treat all sources uniformly.
/// </summary>
public sealed class TrackMetadata
{
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string? Album { get; init; }
    public TimeSpan? Duration { get; init; }
    public Uri? ArtworkUri { get; init; }
    public DateTimeOffset? ReleaseDate { get; init; }
    public string? Genre { get; init; }

    public string DisplayTitle => string.IsNullOrWhiteSpace(Artist)
        ? Title
        : $"{Artist} — {Title}";
}

/// <summary>One track as found by a search.</summary>
public sealed class SearchResult
{
    public required ProviderId Provider { get; init; }
    /// <summary>Provider-native id (video id, track id, or absolute URL for scraped sources).</summary>
    public required string Id { get; init; }
    public required TrackMetadata Metadata { get; init; }

    /// <summary>Direct audio URL when the provider exposed one up front (Radio Javan, scraped MP3s, catalog previews). Null when it must be resolved at download time.</summary>
    public Uri? DirectStreamUri { get; init; }

    /// <summary>Human-facing page URL (or yt-dlp target).</summary>
    public string SourceUrl { get; init; } = "";

    public StreamQuality MaxQuality { get; init; }

    /// <summary>False for catalog rows that only carry a 30s preview (iTunes/Deezer).</summary>
    public bool Downloadable { get; init; } = true;

    /// <summary>True when the only audio this result can offer is a 30s preview.</summary>
    public bool PreviewOnly { get; init; }

    public string DedupKey => Id.Length > 0
        ? $"{Provider}::{Id}"
        : $"url::{DirectStreamUri?.OriginalString ?? SourceUrl}";
}

/// <summary>Rough audio quality ladder.</summary>
public enum StreamQuality
{
    Unknown = 0,
    Preview64K,
    Preview128K,
    Standard128K,
    High192K,
    Maximum256K,
}
