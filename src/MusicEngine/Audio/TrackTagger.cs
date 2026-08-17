namespace MusicEngine.Audio;

using Http;
using Models;
using TagLib;

/// <summary>
/// Post-download tagging via TagLibSharp: writes ID3 title/artist/album/year from
/// the catalog (goal) metadata and embeds the artwork — so files scraped from
/// Iranian sites (which carry "دانلود آهنگ…" junk tags or none) come out clean.
/// yt-dlp downloads are already tagged; this only fills gaps.
/// </summary>
public sealed class TrackTagger
{
    private readonly SharedHttpClient _http;

    public TrackTagger(SharedHttpClient http) => _http = http;

    public void Tag(string filePath, TrackMetadata meta)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            var changed = false;

            if (IsBetter(meta.Title, file.Tag.Title)) { file.Tag.Title = meta.Title; changed = true; }
            if (IsBetter(meta.Artist, string.Join(", ", file.Tag.Performers ?? Array.Empty<string>())))
            {
                file.Tag.Performers = string.IsNullOrWhiteSpace(meta.Artist) ? Array.Empty<string>() : new[] { meta.Artist };
                changed = true;
            }
            if (IsBetter(meta.Album, file.Tag.Album)) { file.Tag.Album = meta.Album!; changed = true; }
            if (meta.ReleaseDate is { } rd && file.Tag.Year == 0)
            {
                file.Tag.Year = (uint)rd.Year;
                changed = true;
            }

            // Embed artwork when missing.
            if ((file.Tag.Pictures is null || file.Tag.Pictures.Length == 0) && meta.ArtworkUri is not null)
            {
                var art = TryDownloadArtwork(meta.ArtworkUri);
                if (art is not null)
                {
                    file.Tag.Pictures = new IPicture[] { new Picture(art) { Type = PictureType.FrontCover } };
                    changed = true;
                }
            }

            if (changed) file.Save();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Tagging failed for {filePath}: {ex.Message}");
        }
    }

    private static bool IsBetter(string? candidate, string? current)
        => !string.IsNullOrWhiteSpace(candidate)
           && (string.IsNullOrWhiteSpace(current)
               || current.Contains("دانلود") // junk-tagged scraped files
               || current.Length < 2);

    private byte[]? TryDownloadArtwork(Uri uri)
    {
        try
        {
            return _http.Create("Artwork").GetByteArrayAsync(uri).GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }
    }
}
