namespace MusicEngine.Http;

using System;
using System.Threading;
using System.Threading.Tasks;

public class ArtworkLoader : IArtworkLoader
{
    private readonly SharedHttpClient _http;

    public ArtworkLoader(SharedHttpClient http)
    {
        _http = http;
    }

    public async Task<byte[]?> LoadAsync(Uri uri, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.Create("Artwork").GetAsync(uri, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }
}
