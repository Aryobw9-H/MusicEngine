namespace MusicEngine.Downloads;

using Configuration;
using Models;

/// <summary>
/// Output file naming driven by the configured template — clean, human-readable,
/// no provider ids unless asked for. Uniqueness is handled by the caller.
/// </summary>
public static class FileNaming
{
    /// <summary>
    /// Builds the output filename, preferring catalog metadata (goal) over the
    /// downloaded copy's (often noisy scraped) title.
    /// </summary>
    public static string Build(TrackMetadata? tagTemplate, SearchResult source,
        string ext = ".mp3", FilenameTemplate template = FilenameTemplate.ArtistTitle)
    {
        var artist = Clean(tagTemplate?.Artist) is { Length: > 0 } a ? a : Clean(source.Metadata.Artist);
        var title = Clean(tagTemplate?.Title) is { Length: > 0 } t ? t : Clean(source.Metadata.Title);

        var name = template switch
        {
            FilenameTemplate.Title => title.Length > 0 ? title : "Unknown Track",
            FilenameTemplate.ArtistTitleSource => artist.Length > 0 && title.Length > 0
                ? $"{artist} - {title} ({source.Provider})"
                : title.Length > 0 ? $"{title} ({source.Provider})" : "Unknown Track",
            _ => artist.Length > 0 && title.Length > 0 ? $"{artist} - {title}"
                : title.Length > 0 ? title : "Unknown Track",
        };
        if (name.Length > 150) name = name[..150];
        return name + ext;
    }

    /// <summary>Existing path for this track, or null when not yet downloaded.</summary>
    public static string? ExistingPath(string outputDirectory, TrackMetadata? tagTemplate, SearchResult source,
        FilenameTemplate template = FilenameTemplate.ArtistTitle)
    {
        var p = Path.Combine(outputDirectory, Build(tagTemplate, source, ".mp3", template));
        return File.Exists(p) ? p : null;
    }

    private static string Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var invalid = Path.GetInvalidFileNameChars();
        var clean = string.Concat(s.Where(c => !invalid.Contains(c))).Trim();
        return clean.Trim('.');
    }
}
