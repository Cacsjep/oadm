using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace Oadm.Core.Sites;

/// <summary>
/// The route handler: opens every TCP connection to a device on its <see cref="DeviceRoute"/> (docs/sites.md, Server:
/// Routing). The server's own network is a normal socket connect, exactly as before sites existed; a site route asks
/// <see cref="ISiteTransport"/> for a new stream each time a connection opens (never a stream kept from creation), so a
/// reconnected node is used at once. TLS, certificate pinning and the login stay in the server, end to end to the device.
/// The VAPIX client (HTTP, HTTPS, WebSockets), the discovery probes and the RTSP client all connect through here.
/// </summary>
[SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs", Justification = "The route handler: the one place a device connection on the server's own network opens a socket.")]
public sealed class DeviceRouter
{
    /// <summary>A new connection through a site costs about this many round trips (open the stream, connect, TLS, request).</summary>
    public const int NewConnectionRoundTrips = 4;

    /// <summary>Connections per device at most through a site (polls, tasks and page reads share them).</summary>
    public const int SiteConnectionsPerDevice = 2;

    /// <summary>
    /// Connections per device through a site for the status reads (the poll and the full refresh's basicdeviceinfo) on a
    /// pool of their own: an upload, a Web View page or a long poll on the device never makes a status read wait for a
    /// connection (that wait would end as a device timeout).
    /// </summary>
    public const int SiteStatusConnectionsPerDevice = 2;

    /// <summary>
    /// Connections per device through a site for the Web View relay: a pool of its own, so a page's MJPEG stream or long
    /// poll never takes the connections tasks, compliance checks and API reads share (those would time out as device
    /// errors). As many as a browser opens to one host.
    /// </summary>
    public const int SiteRelayConnectionsPerDevice = 6;

    /// <summary>How long an idle connection through a site stays open.</summary>
    public static readonly TimeSpan SiteIdleTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The route of one request on a handler that serves every route (<see cref="CreateRequestRoutedCallback"/>, the
    /// discovery probe). A request without it goes to the server's own network.
    /// </summary>
    public static readonly HttpRequestOptionsKey<DeviceRoute> RouteOption = new("Oadm.Route");

    /// <summary>The device timeout of one request (before the route's extra), read by the connect callback.</summary>
    public static readonly HttpRequestOptionsKey<TimeSpan> ConnectTimeoutOption = new("Oadm.ConnectTimeout");

    private readonly ISiteTransport? _transport;
    private readonly ISiteStates? _states;

    /// <param name="transport">The sites' transport; null = no sites on this server (a site route is refused as offline).</param>
    /// <param name="states">Names and round trips of the sites; null = no round trip on top of timeouts.</param>
    public DeviceRouter(ISiteTransport? transport = null, ISiteStates? states = null)
    {
        _transport = transport;
        _states = states;
    }

    /// <summary>The server's own network only: no sites (tests, tools, a server without sites).</summary>
    public static DeviceRouter Local { get; } = new();

    /// <summary>
    /// What a new connection through the route costs on top of a device timeout: zero on the server's own network,
    /// <see cref="NewConnectionRoundTrips"/> times the measured round trip through a site (a 3 s poll over a 600 ms link
    /// waits 5.4 s).
    /// </summary>
    public TimeSpan ExtraTimeout(DeviceRoute route) =>
        route.SiteId is { } site && _states is not null ? _states.RoundTrip(site) * NewConnectionRoundTrips : TimeSpan.Zero;

    /// <summary>
    /// A large transfer (a streamed upload or answer) on <paramref name="route"/>: through a site it waits for one of the
    /// site's slots (<see cref="ISiteLargeTransfers"/>); on the server's own network, or without such a transport, null at once.
    /// </summary>
    public ValueTask<IDisposable?> EnterLargeTransferAsync(DeviceRoute route, CancellationToken ct) =>
        EnterLargeTransferAsync(route, ct, CancellationToken.None);

    /// <summary>
    /// <see cref="EnterLargeTransferAsync(DeviceRoute, CancellationToken)"/> with a bound: when <paramref name="limit"/>
    /// ends the wait (and <paramref name="ct"/> did not), the site is busy (<see cref="SiteException"/>, never a device
    /// timeout). A task in the current flow sees "Waiting for the site link" while it waits (<see cref="SiteLinkWait"/>).
    /// </summary>
    public ValueTask<IDisposable?> EnterLargeTransferAsync(DeviceRoute route, CancellationToken ct, CancellationToken limit) =>
        route.SiteId is { } site && _transport is ISiteLargeTransfers transfers
            ? EnterAsync(transfers, site, ct, limit)
            : ValueTask.FromResult<IDisposable?>(null);

    private async ValueTask<IDisposable?> EnterAsync(ISiteLargeTransfers transfers, Guid site, CancellationToken ct, CancellationToken limit)
    {
        using var both = limit.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(ct, limit) : null;
        var wait = transfers.EnterLargeTransferAsync(site, both?.Token ?? ct);
        if (wait.IsCompleted)
        {
            return await wait.ConfigureAwait(false);
        }

        var reporter = SiteLinkWait.Current;
        SiteLinkWait.Report(reporter, true);
        try
        {
            return await wait.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && limit.IsCancellationRequested)
        {
            throw SiteException.For(site, _states?.NameOf(site) ?? site.ToString(), SiteProblem.Busy);
        }
        finally
        {
            SiteLinkWait.Report(reporter, false);
        }
    }

    /// <summary><paramref name="timeout"/> with <see cref="ExtraTimeout"/> on top (an infinite timeout stays infinite).</summary>
    public TimeSpan Timeout(DeviceRoute route, TimeSpan timeout) =>
        timeout == System.Threading.Timeout.InfiniteTimeSpan || timeout <= TimeSpan.Zero ? timeout : timeout + ExtraTimeout(route);

    /// <summary>
    /// One TCP connection to <paramref name="host"/>:<paramref name="port"/> on <paramref name="route"/>. The device's own
    /// socket errors are <see cref="SocketException"/> on both routes; a site that cannot carry the connection is a
    /// <see cref="SiteException"/> (never a device error). <paramref name="timeout"/> bounds the connect through a site
    /// (the node stops connecting then); on the server's own network the caller's token bounds it, as before.
    /// </summary>
    public async ValueTask<Stream> ConnectAsync(DeviceRoute route, string host, int port, SiteDialKind kind, TimeSpan timeout, CancellationToken ct,
        int receiveBufferSize = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (route.SiteId is { } site)
        {
            if (_transport is null)
            {
                throw SiteException.For(site, _states?.NameOf(site) ?? site.ToString(), SiteProblem.Offline);
            }

            var limit = timeout == System.Threading.Timeout.InfiniteTimeSpan || timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(30) : timeout;
            return await _transport.OpenStreamAsync(site, host, port, kind, limit, ct).ConfigureAwait(false);
        }

        // The server's own network: what SocketsHttpHandler does without a ConnectCallback.
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            if (receiveBufferSize > 0)
            {
                socket.ReceiveBufferSize = receiveBufferSize;
            }

            await socket.ConnectAsync(new DnsEndPoint(host, port), ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The connect callback of a handler that serves one route (the VAPIX client of one device): every new connection
    /// of the handler opens on <paramref name="route"/>; a WebSocket upgrade counts as a permanent connection.
    /// </summary>
    public Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> CreateConnectCallback(DeviceRoute route, TimeSpan defaultTimeout) =>
        (context, ct) => ConnectAsync(route, context.DnsEndPoint.Host, context.DnsEndPoint.Port, KindOf(context), ConnectTimeout(context, defaultTimeout, route), ct);

    /// <summary>
    /// The connect callback of a handler that serves every route (the discovery probe): the route of each new connection
    /// comes from its request (<see cref="RouteOption"/>; none = the server's own network). Such a handler keeps one
    /// connection pool per host and port, so one handler must only serve routes whose addresses cannot clash (stream 5
    /// keys the probe's pools by route).
    /// </summary>
    public Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> CreateRequestRoutedCallback(TimeSpan defaultTimeout) =>
        (context, ct) =>
        {
            var route = context.InitialRequestMessage.Options.TryGetValue(RouteOption, out var requested) ? requested : DeviceRoute.Local;
            return ConnectAsync(route, context.DnsEndPoint.Host, context.DnsEndPoint.Port, SiteDialKind.Short, ConnectTimeout(context, defaultTimeout, route), ct);
        };

    /// <summary>
    /// Sets the routing parts of a handler for one route: the connect callback, and through a site at most
    /// <see cref="SiteConnectionsPerDevice"/> connections that close after <see cref="SiteIdleTimeout"/>. The server's own
    /// network keeps the handler's defaults (as before sites existed).
    /// </summary>
    public void Configure(SocketsHttpHandler handler, DeviceRoute route, TimeSpan defaultTimeout)
    {
        ArgumentNullException.ThrowIfNull(handler);
        handler.ConnectCallback = CreateConnectCallback(route, defaultTimeout);
        if (!route.IsLocal)
        {
            handler.MaxConnectionsPerServer = SiteConnectionsPerDevice;
            handler.PooledConnectionIdleTimeout = SiteIdleTimeout;
        }
    }

    /// <summary>WebSocket upgrades stay open: they count as permanent connections on the node.</summary>
    private static SiteDialKind KindOf(SocketsHttpConnectionContext context) =>
        context.InitialRequestMessage.Headers.Contains("Sec-WebSocket-Key") || context.InitialRequestMessage.Headers.Upgrade.Count > 0
            ? SiteDialKind.Permanent
            : SiteDialKind.Short;

    private TimeSpan ConnectTimeout(SocketsHttpConnectionContext context, TimeSpan defaultTimeout, DeviceRoute route) =>
        Timeout(route, context.InitialRequestMessage.Options.TryGetValue(ConnectTimeoutOption, out var requested) ? requested : defaultTimeout);
}
