namespace MusicEngine.Models;

public enum DownloadPhase
{
    Queued,
    Resolving,
    Downloading,
    Tagging,
    Completed,
        Failed,
        Cancelled,
        AlreadyOwned,
        Paused,
    }

/// <summary>Progress payload for a download job.</summary>
public sealed record DownloadProgress(
    DownloadPhase Phase,
    long BytesDone = 0,
    long? BytesTotal = null,
    string? Message = null,
    string? FilePath = null)
{
    public int? Percent => BytesTotal is long t && t > 0
        ? (int)Math.Clamp(Math.Round((double)BytesDone / t * 100), 0, 100)
        : null;
}

/// <summary>Outcome of a finished download job.</summary>
public sealed record DownloadResult(
    string FilePath,
    StreamQuality Quality,
    ProviderId ViaProvider);

public sealed class DownloadOptions
{
    public required string OutputDirectory { get; init; }
    /// <summary>Target bitrate when transcoding to MP3 via yt-dlp/ffmpeg.</summary>
    public int MaxBitrateKbps { get; init; } = 320;
    /// <summary>Embed ID3 tags + artwork after download (direct-MP3 sources).</summary>
    public bool EmbedTags { get; init; } = true;
    /// <summary>Metadata used for tagging/preview when the downloaded source has poor titles.</summary>
    public TrackMetadata? TagTemplate { get; init; }

    /// <summary>Output filename shape (see FileNaming).</summary>
    public Configuration.FilenameTemplate FilenameTemplate { get; init; } = Configuration.FilenameTemplate.ArtistTitle;
}
