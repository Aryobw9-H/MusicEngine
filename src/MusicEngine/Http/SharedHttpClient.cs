namespace MusicEngine.Http;

using System.Collections.Concurrent;
using System.Net;
using Network;

/// <summary>
/// One shared HttpClient factory: one client per logical name so sockets pool and
/// handlers live for the app lifetime. This is also the single proxy injection
/// point. SocketsHttpHandler natively supports http:// and socks5:// proxies.
///
/// "Proxied" clients built with a <see cref="Reachability"/> instance become
/// smart: each request goes direct when its host allows it and through the proxy
/// only when needed, and dead hosts fail in milliseconds instead of timing out.
/// </summary>
public sealed class SharedHttpClient : IDisposable
{
    private readonly ConcurrentDictionary<string, HttpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _proxyUrl;
    private readonly Reachability? _reach;

    public SharedHttpClient(string? proxyUrl = null, Reachability? reachability = null)
    {
        _proxyUrl = proxyUrl;
        _reach = reachability;
    }

    /// <summary>A real desktop-Chrome UA — several Iranian CDNs reject bare
    /// default-dotnet requests as obvious bots.</summary>
    public const string BrowserUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    /// <summary>Shared client. <paramref name="proxied"/> picks the proxy-wired
    /// variant (for YouTube/Deezer/SoundCloud on filtered networks); Iranian
    /// sites and iTunes work best direct. <paramref name="insecureTls"/> accepts
    /// self-signed CDN certs (several Iranian CDNs use them).</summary>
    public HttpClient Create(string name, bool proxied = false, bool insecureTls = false)
    {
        var key = name + (proxied ? "+proxy" : "") + (insecureTls ? "+tls" : "");
        return _clients.GetOrAdd(key, _ =>
        {
            if (proxied && !string.IsNullOrEmpty(_proxyUrl) && _reach is not null)
            {
                // Smart routing: per-request direct-when-possible, proxy-when-needed,
                // instant failure on dead hosts.
                var direct = new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                    PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
                    AutomaticDecompression = DecompressionMethods.All,
                };
                var viaProxy = new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                    AutomaticDecompression = DecompressionMethods.All,
                    Proxy = new WebProxy(_proxyUrl, BypassOnLocal: true),
                    UseProxy = true,
                };
                if (insecureTls)
                {
                    direct.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
                    viaProxy.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
                }
                return new HttpClient(new RoutingHandler(_reach, direct, viaProxy), disposeHandler: true)
                {
                    Timeout = TimeSpan.FromSeconds(30),
                };
            }

            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
                AutomaticDecompression = DecompressionMethods.All,
            };
            if (proxied && !string.IsNullOrEmpty(_proxyUrl))
            {
                handler.Proxy = new WebProxy(_proxyUrl, BypassOnLocal: true);
                handler.UseProxy = true;
            }
            if (insecureTls)
                handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            return new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
        });
    }

    /// <summary>Apply realistic browser default headers to a client (idempotent).</summary>
    public static void ApplyBrowserHeaders(HttpClient client, string? referer = null)
    {
        if (!client.DefaultRequestHeaders.Contains("User-Agent"))
        {
            client.DefaultRequestHeaders.Add("User-Agent", BrowserUa);
            client.DefaultRequestHeaders.Add("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
            client.DefaultRequestHeaders.Add("Accept-Language", "fa-IR,fa;q=0.9,en-US;q=0.8,en;q=0.7");
            client.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua",
                "\"Chromium\";v=\"124\", \"Google Chrome\";v=\"124\"");
            client.DefaultRequestHeaders.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
            client.DefaultRequestHeaders.Add("Upgrade-Insecure-Requests", "1");
        }
        if (referer is { Length: > 0 } && !client.DefaultRequestHeaders.Contains("Referer"))
            client.DefaultRequestHeaders.Add("Referer", referer);
    }

    public void Dispose()
    {
        foreach (var c in _clients.Values) c.Dispose();
        _clients.Clear();
    }
}
