namespace MusicEngine.Downloads;

using System.Text;

/// <summary>
/// Cheap post-download sanity check that the bytes on disk are actually audio.
/// Blocked CDNs and dead hosts hand back HTML/JSON error pages (Cloudflare
/// challenge, 404, "download not allowed") that would otherwise be renamed to
/// .mp3 and reported as a successful download — the classic source of "the file
/// downloaded but it's corrupt/unplayable".
/// </summary>
public static class AudioFile
{
    private static readonly (byte[] Magic, string Name)[] Signatures =
    {
        (new byte[] { 0x49, 0x44, 0x33 }, "MP3 (ID3)"),   // "ID3"
        (new byte[] { 0xFF, 0xFB }, "MP3 frame"),         // MPEG-1 Layer 3
        (new byte[] { 0xFF, 0xF3 }, "MP3 frame"),
        (new byte[] { 0xFF, 0xF2 }, "MP3 frame"),
        (new byte[] { 0xFF, 0xFA }, "MP3 frame"),
        (new byte[] { 0x52, 0x49, 0x46, 0x46 }, "WAV"),   // "RIFF"
        (new byte[] { 0x66, 0x4C, 0x61, 0x43 }, "FLAC"),  // "fLaC"
        (new byte[] { 0x4F, 0x67, 0x67, 0x53 }, "OGG"),   // "OggS"
        (new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }, "WebM/MKV"),
    };

    private const long MinPlausibleSize = 8_192;

    /// <summary>True when the file looks like a real audio file. Rejects error
    /// pages (HTML/XML/JSON) and anything too small to be a song; unknown binary
    /// is accepted rather than over-rejected.</summary>
    public static bool IsProbablyAudio(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MinPlausibleSize) return false;

            using var fs = File.OpenRead(path);
            var head = new byte[512];
            var n = fs.Read(head, 0, head.Length);
            if (n < 8) return false;

            // MP4/M4A containers carry "ftyp" at offset 4.
            if (n >= 8 && head.AsSpan(4, 4).SequenceEqual("ftyp"u8)) return true;

            foreach (var (magic, _) in Signatures)
            {
                if (magic.Length <= n && head.AsSpan(0, magic.Length).SequenceEqual(magic))
                    return true;
            }

            // Nothing recognized: reject obvious text/error pages, accept the rest.
            var text = Encoding.UTF8.GetString(head, 0, n);
            return !text.Contains("<html", StringComparison.OrdinalIgnoreCase)
                && !text.Contains("<!doctype", StringComparison.OrdinalIgnoreCase)
                && !text.Contains("<?xml", StringComparison.OrdinalIgnoreCase)
                && text[0] != '<' && text[0] != '{';
        }
        catch
        {
            return false;
        }
    }
}
