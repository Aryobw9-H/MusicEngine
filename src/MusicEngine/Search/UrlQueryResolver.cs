namespace MusicEngine.Search;

using System.Text.Json;
using Http;

/// <summary>
/// Turns a pasted track URL (Spotify / YouTube / SoundCloud / Deezer) into a text
/// query via the platforms' public oEmbed endpoints — no API keys. The resolved
/// "Artist - Title" then flows through the normal search pipeline.
/// </summary>
public static class UrlQueryResolver
{
    public static async Task<string?> ResolveAsync(Uri uri, CancellationToken ct = default)
    {
        var host = uri.Host.StartsWith("www.") ? uri.Host[4..] : uri.Host;
        var oembed = host switch
        {
            "open.spotify.com" or "spotify.com" =>
                $"https://open.spotify.com/oembed?url={Uri.EscapeDataString(uri.AbsoluteUri)}",
            "music.youtube.com" or "youtube.com" or "youtu.be" =>
                $"https://www.youtube.com/oembed?url={Uri.EscapeDataString(uri.AbsoluteUri)}&format=json",
            "soundcloud.com" =>
                $"https://soundcloud.com/oembed?url={Uri.EscapeDataString(uri.AbsoluteUri)}&format=json",
            _ => null,
        };
        if (oembed is null) return null;

        try
        {
            using var http = new SharedHttpClient().Create("oEmbed");
            using var resp = await http.GetAsync(oembed, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var title = doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null;
            var author = doc.RootElement.TryGetProperty("author_name", out var a) ? a.GetString() : null;

            // Spotify oEmbed titles are the track name only; the artist usually
            // lives in the page's og:title ("Artist - Track") — cheap fetch, no auth.
            if (host.Contains("spotify") && author is null or "Spotify")
            {
                var og = await TrySpotifyOgTitleAsync(http, uri, ct).ConfigureAwait(false);
                if (og is { Length: > 2 }) return og;
            }

            var parts = new[] { author, title }.Where(s => !string.IsNullOrWhiteSpace(s) && s != "Spotify");
            var q = string.Join(" - ", parts);
            return q.Length > 1 ? q : title;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> TrySpotifyOgTitleAsync(HttpClient http, Uri uri, CancellationToken ct)
    {
        try
        {
            var html = await http.GetStringAsync(uri.AbsoluteUri, ct).ConfigureAwait(false);
            var m = System.Text.RegularExpressions.Regex.Match(html,
                "<meta property=\"og:title\" content=\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }
}
