namespace MusicEngine.Network;

using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.Http;
using Microsoft.Extensions.Logging;

/// <summary>How a host is currently reachable.</summary>
public enum HostRoute
{
    Unknown,
    Direct,     // plain connection works
    ViaProxy,   // only reachable through the configured proxy
    Dead,       // neither works → providers on this host are auto-disabled
}

/// <summary>
/// Per-host reachability with routing. Probes https://{host}/ directly first and
/// through the proxy second; the answer is cached until the machine's local IP
/// set changes (NetworkAddressChanged / fingerprint mismatch) — exactly the
/// "switch network, re-probe" behavior. <see cref="RoutingHandler"/> uses these
/// answers to send each request the working way, so a dead host fails in
/// milliseconds instead of burning the whole provider timeout.
/// </summary>
public sealed class Reachability : IDisposable
{
    private HttpClient _direct;
    private HttpClient? _proxied;
    private readonly ConcurrentDictionary<string, Task<HostRoute>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _fpLock = new();
    private readonly ILogger<Reachability> _logger;
    private string _fingerprint;
    private DateTime _fpChecked = DateTime.UtcNow;

    /// <summary>Fires (on a thread-pool thread) when routes were invalidated by a network change.</summary>
    public event Action? RoutesChanged;

    public Reachability(string? proxyUrl, ILogger<Reachability>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<Reachability>.Instance;
        _direct = MakeClient(proxied: false, _logger);
        if (!string.IsNullOrWhiteSpace(proxyUrl))
            _proxied = MakeClient(proxied: true, _logger, proxyUrl!);
        _fingerprint = ComputeFingerprint(_logger);
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
    }

    /// <summary>Cached answer without probing (Unknown when never probed).</summary>
    public HostRoute Peek(string host)
    {
        InvalidateIfNetworkChanged();
        return _cache.TryGetValue(host, out var t) && t.IsCompletedSuccessfully
            ? t.Result
            : HostRoute.Unknown;
    }

    /// <summary>Probe (or return cached) route for a host. Never throws.</summary>
    /// <remarks>
    /// The cached probe is deliberately started with <see cref="CancellationToken.None"/>
    /// so one cancelling caller cannot poison the shared cached task (BUG-04); it has
    /// internal 7s timeouts plus a 9s HttpClient timeout, so it cannot hang. Callers
    /// apply their own cancellation at the await site. A probe that does not complete
    /// successfully is removed from the cache so a later caller re-probes.
    /// </remarks>
    public Task<HostRoute> ProbeAsync(string host, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host)) return Task.FromResult(HostRoute.Dead);
        InvalidateIfNetworkChanged();
        return _cache.GetOrAdd(host.Trim(), h =>
        {
            var probe = ProbeUncachedAsync(h, CancellationToken.None);
            _ = probe.ContinueWith(_ => _cache.TryRemove(h, out var removed), CancellationToken.None,
                TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return probe;
        });
    }

    public bool HasRoute(string host) => Peek(host) is HostRoute.Direct or HostRoute.ViaProxy;

    /// <summary>
    /// Hot-reload foundation (FEAT-05): rebuild the direct/proxied clients for a
    /// new proxy URL and drop every cached route so the next probe re-verifies
    /// against the new network path.
    ///
    /// NOT wired to the UI yet — the Settings dialog prompts for a restart
    /// instead, because the proxy is also baked into SharedHttpClient's clients
    /// and the providers' construction. When hot-reload is implemented properly,
    /// SharedHttpClient needs a matching <c>Reconfigure</c> that rebuilds its
    /// client pool, and the proxy-aware providers must rebuild their handlers
    /// from it. Until then this method must not be called at runtime.
    /// </summary>
    public void Reconfigure(string? proxyUrl)
    {
        _direct.Dispose();
        _proxied?.Dispose();
        _direct = MakeClient(proxied: false, _logger);
        _proxied = string.IsNullOrWhiteSpace(proxyUrl)
            ? null
            : MakeClient(proxied: true, _logger, proxyUrl);
        _cache.Clear();
        RoutesChanged?.Invoke();
    }

    private async Task<HostRoute> ProbeUncachedAsync(string host, CancellationToken ct)
    {
        // 1) direct — any HTTP response (even 404/403) proves TCP+TLS+egress.
        if (await HttpAliveAsync(_direct, host, ct, _logger).ConfigureAwait(false))
            return HostRoute.Direct;
        // 2) proxy. Slow proxy exits (4s+ handshakes are normal here) must not
        //    produce false "dead" verdicts — retry once before giving up.
        if (_proxied is not null
            && (await HttpAliveAsync(_proxied, host, ct, _logger).ConfigureAwait(false)
                || await HttpAliveAsync(_proxied, host, ct, _logger).ConfigureAwait(false)))
            return HostRoute.ViaProxy;
        return HostRoute.Dead;
    }

    private static async Task<bool> HttpAliveAsync(HttpClient client, string host, CancellationToken ct, ILogger logger)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(7));
            using var resp = await client.GetAsync($"https://{host}/",
                HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            return true;
        }
        // Caller cancellation is NOT a "host is dead" verdict — rethrow it so a
        // cancelled search cannot poison routing for the rest of the session (BUG-03).
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        // Everything else (connection refused, DNS, the internal 7s probe timeout)
        // is a genuine unreachable answer.
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or OperationCanceledException)
        {
            logger.LogDebug(ex, "Probe {Host} unreachable", host);
            return false;
        }
    }

    private static HttpClient MakeClient(bool proxied, ILogger logger, string? proxyUrl = null)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(8),
            AutomaticDecompression = DecompressionMethods.All,
        };
        if (proxied && proxyUrl is not null)
        {
            if (TryParseProxyUrl(proxyUrl, out var proxyUri, logger))
            {
                handler.Proxy = new WebProxy(proxyUri, BypassOnLocal: true);
                handler.UseProxy = true;
            }
        }
        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(9) };
    }

    private static bool TryParseProxyUrl(string proxyUrl, out Uri proxyUri, ILogger logger)
    {
        proxyUri = null!;
        try
        {
            var uri = new Uri(proxyUrl);
            if (uri.Port > 0 && uri.Port <= 65535)
            {
                proxyUri = uri;
                return true;
            }
            logger.LogWarning("Proxy URL has an invalid port: {ProxyUrl}", proxyUrl);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Proxy URL is not parseable: {ProxyUrl}", proxyUrl);
        }
        return false;
    }

    // ---------- network-change detection ----------

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        // Fires in bursts when an interface flaps; debounce, then compare fingerprints.
        Task.Run(async () =>
        {
            await Task.Delay(1500).ConfigureAwait(false);
            if (InvalidateIfNetworkChanged(forceCheck: true))
                RoutesChanged?.Invoke();
        });
    }

    /// <summary>
    /// Drop cached routes when the local IP set changed. Fingerprinting costs a
    /// NIC enumeration, so spontaneous checks are rate-limited to once per 5s
    /// (event-driven checks bypass the limit).
    /// </summary>
    private bool InvalidateIfNetworkChanged(bool forceCheck = false)
    {
        lock (_fpLock)
        {
            if (!forceCheck && DateTime.UtcNow - _fpChecked < TimeSpan.FromSeconds(5)) return false;
            _fpChecked = DateTime.UtcNow;
            var fp = ComputeFingerprint(_logger);
            if (fp == _fingerprint) return false;
            _fingerprint = fp;
        }
        _cache.Clear();
        return true;
    }

    private static string ComputeFingerprint(ILogger logger)
    {
        try
        {
            var ips = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                .Select(ip => ip.ToString())
                .OrderBy(s => s, StringComparer.Ordinal);
            return string.Join("|", ips);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Network fingerprint computation failed; treating as unknown");
            return "unknown";
        }
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
        _direct.Dispose();
        _proxied?.Dispose();
        _cache.Clear();
    }
}

/// <summary>
/// Delegating handler that routes each request the way <see cref="Reachability"/>
/// says its host can be reached: direct when possible, proxy when needed, and an
/// immediate <see cref="HttpRequestException"/> when the host is dead — so
/// providers fail in milliseconds instead of timing out.
/// </summary>
public sealed class RoutingHandler : HttpMessageHandler
{
    private readonly ExposingHandler _direct;
    private readonly ExposingHandler _proxied;
    private readonly Reachability _reach;

    public RoutingHandler(Reachability reach, HttpMessageHandler direct, HttpMessageHandler proxied)
    {
        _reach = reach;
        _direct = new ExposingHandler(direct);
        _proxied = new ExposingHandler(proxied);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var host = request.RequestUri?.Host ?? "";
        // Never block a request on a slow probe: if the cached verdict isn't
        // ready within 1.5s (e.g. first search racing the startup probes), go
        // through the proxy optimistically — these hosts are the proxy tier,
        // and a proxy detour still works for direct-capable hosts.
        var probe = _reach.ProbeAsync(host, ct);
        HostRoute route;
        var winner = await Task.WhenAny(probe, Task.Delay(1500, ct)).ConfigureAwait(false);
        if (winner == probe)
            route = await probe.ConfigureAwait(false);
        else
            route = HostRoute.ViaProxy;
        return route switch
        {
            HostRoute.Direct => await _direct.SendCoreAsync(request, ct).ConfigureAwait(false),
            HostRoute.ViaProxy => await _proxied.SendCoreAsync(request, ct).ConfigureAwait(false),
            _ => throw new HttpRequestException($"Host unreachable both directly and via proxy: {host}"),
        };
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _direct.Dispose();
            _proxied.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Exposes the protected DelegatingHandler send. Inner handlers must be raw
    /// handlers, NOT HttpClients — an inner HttpClient would reject the request
    /// ("already sent") because the outer HttpClient marked it.
    /// </summary>
    private sealed class ExposingHandler : DelegatingHandler
    {
        public ExposingHandler(HttpMessageHandler inner) : base(inner) { }

        public Task<HttpResponseMessage> SendCoreAsync(HttpRequestMessage request, CancellationToken ct) =>
            base.SendAsync(request, ct);
    }
}
