namespace MusicEngine.Audio;

using Http;
using Microsoft.Extensions.Logging;
using Models;
using TagLib;

/// <summary>
/// Post-download tagging via TagLibSharp: writes ID3 title/artist/album/year from
/// the catalog (goal) metadata and embeds the artwork — so files scraped from
/// Iranian sites (which carry "دانلود آهنگ…" junk tags or none) come out clean.
/// yt-dlp downloads are already tagged; this only fills gaps.
///
/// Crash-safe: the tags are written to a temp COPY and then atomically moved over
/// the original. If TagLib dies mid-save the real file is never left truncated —
/// a corrupted MP3 was the exact bug this protects against. The artwork fetch is
/// async and time-bounded so a slow artwork host cannot stall a download worker
/// (which used to freeze the whole download queue).
/// </summary>
public sealed class TrackTagger
{
    private static readonly TimeSpan ArtworkTimeout = TimeSpan.FromSeconds(10);

    private readonly SharedHttpClient _http;
    private readonly ILogger<TrackTagger> _logger;

    public TrackTagger(SharedHttpClient http, ILogger<TrackTagger>? logger = null)
    {
        _http = http;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TrackTagger>.Instance;
    }

    public async Task TagAsync(string filePath, TrackMetadata meta)
    {            var tmp = filePath + ".tagtmp";
            try
            {
                System.IO.File.Copy(filePath, tmp, overwrite: true);

            // Fetch artwork first — it can hit the network, so keep it off the
            // file-open window and bounded in time.
            byte[]? art = null;
            if (meta.ArtworkUri is not null)
                art = await TryDownloadArtworkAsync(meta.ArtworkUri).ConfigureAwait(false);

            using (var file = TagLib.File.Create(tmp, "audio/mpeg", ReadStyle.None))
            {
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
                if ((file.Tag.Pictures is null || file.Tag.Pictures.Length == 0) && art is not null)
                {
                    file.Tag.Pictures = new IPicture[] { new Picture(art) { Type = PictureType.FrontCover } };
                    changed = true;
                }

                if (changed) file.Save();
            }

            System.IO.File.Move(tmp, filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tagging failed for {File}", filePath);
        }
        finally
        {
            try { if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    private static bool IsBetter(string? candidate, string? current)
        => !string.IsNullOrWhiteSpace(candidate)
           && (string.IsNullOrWhiteSpace(current)
               || current.Contains("دانلود") // junk-tagged scraped files
               || current.Length < 2);

    private async Task<byte[]?> TryDownloadArtworkAsync(Uri uri)
    {
        try
        {
            using var cts = new CancellationTokenSource(ArtworkTimeout);
            var bytes = await _http.Create("Artwork").GetByteArrayAsync(uri, cts.Token).ConfigureAwait(false);
            return bytes.Length > 0 ? bytes : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Artwork embed skipped for {Uri}", uri);
            return null;
        }
    }
}
