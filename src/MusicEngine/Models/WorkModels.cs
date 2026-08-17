namespace MusicEngine.Models;

/// <summary>
/// The GOAL — the real song the user asked for, as identified by the trusted
/// catalogs (iTunes/Deezer) or derived from the parsed query. Every displayed
/// result must match this identity; every download must resolve a copy of it.
/// </summary>
public sealed record GoalSong(
    string Artist,
    string Title,
    TimeSpan? Duration,
    ProviderId Source);

/// <summary>A labelled copy of a work (Original / Remix / Live / …) with its rank score.</summary>
public sealed record TrackVersion(
    SearchResult Result,
    string Label,
    double Score);

/// <summary>
/// One canonical song with every downloadable copy found for it.
/// When <see cref="Representative"/> is a catalog row (PreviewOnly), the
/// <see cref="Versions"/> are the real downloadable copies from other sources.
/// </summary>
public sealed record TrackWork(
    string Title,
    string Artist,
    SearchResult Representative,
    IReadOnlyList<TrackVersion> Versions,
    GoalSong Goal)
{
    public IEnumerable<SearchResult> DownloadableVersions =>
        Versions.Where(v => v.Result.Downloadable && !v.Result.PreviewOnly)
                .Select(v => v.Result);
}
