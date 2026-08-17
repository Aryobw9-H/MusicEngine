namespace MusicEngine.Network;

using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.Http;

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
    private readonly HttpClient _direct;
    private readonly HttpClient? _proxied;
    private readonly ConcurrentDictionary<string, Task<HostRoute>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _fpLock = new();
    private string _fingerprint;
    private DateTime _fpChecked = DateTime.UtcNow;

    /// <summary>Fires (on a thread-pool thread) when routes were invalidated by a network change.</summary>
    public event Action? RoutesChanged;

    public Reachability(string? proxyUrl)
    {
        _direct = MakeClient(proxied: false);
        if (!string.IsNullOrWhiteSpace(proxyUrl))
            _proxied = MakeClient(proxied: true, proxyUrl!);
        _fingerprint = ComputeFingerprint();
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
    }

    public string? ProxyUrl { get; }

    /// <summary>Cached answer without probing (Unknown when never probed).</summary>
    public HostRoute Peek(string host)
    {
        InvalidateIfNetworkChanged();
        return _cache.TryGetValue(host, out var t) && t.IsCompletedSuccessfully
            ? t.Result
            : HostRoute.Unknown;
    }

    /// <summary>Probe (or return cached) route for a host. Never throws.</summary>
    public Task<HostRoute> ProbeAsync(string host, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host)) return Task.FromResult(HostRoute.Dead);
        InvalidateIfNetworkChanged();
        return _cache.GetOrAdd(host.Trim(), h => ProbeUncachedAsync(h, ct));
    }

    public bool HasRoute(string host) => Peek(host) is HostRoute.Direct or HostRoute.ViaProxy;

    private async Task<HostRoute> ProbeUncachedAsync(string host, CancellationToken ct)
    {
        // 1) direct — any HTTP response (even 404/403) proves TCP+TLS+egress.
        if (await HttpAliveAsync(_direct, host, ct).ConfigureAwait(false))
            return HostRoute.Direct;
        // 2) proxy. Slow proxy exits (4s+ handshakes are normal here) must not
        //    produce false "dead" verdicts — retry once before giving up.
        if (_proxied is not null
            && (await HttpAliveAsync(_proxied, host, ct).ConfigureAwait(false)
                || await HttpAliveAsync(_proxied, host, ct).ConfigureAwait(false)))
            return HostRoute.ViaProxy;
        return HostRoute.Dead;
    }

    private static async Task<bool> HttpAliveAsync(HttpClient client, string host, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(7));
            using var resp = await client.GetAsync($"https://{host}/",
                HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    private static HttpClient MakeClient(bool proxied, string? proxyUrl = null)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(8),
            AutomaticDecompression = DecompressionMethods.All,
        };
        if (proxied && proxyUrl is not null)
        {
            handler.Proxy = new WebProxy(proxyUrl, BypassOnLocal: true);
            handler.UseProxy = true;
        }
        return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(9) };
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
            var fp = ComputeFingerprint();
            if (fp == _fingerprint) return false;
            _fingerprint = fp;
        }
        _cache.Clear();
        return true;
    }

    private static string ComputeFingerprint()
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
        catch { return "unknown"; }
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
