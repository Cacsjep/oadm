using System.Net;
using System.Net.Sockets;

using Oadm.Contracts.SiteNode;
using Oadm.SiteNode.Admin;
using Oadm.SiteNode.Config;
using Oadm.SiteNode.Logging;
using Oadm.SiteNode.Networks;

namespace Oadm.SiteNode.Devices;

/// <summary>The limits of one node (docs/sites.md, "Limits per node").</summary>
public sealed record DialLimits(int ShortOpen = 1024, int ShortConnecting = 256, int PermanentConnecting = 64)
{
    public static DialLimits Default { get; } = new();
}

/// <summary>A dial that connected: the socket and its place in the limits (released on dispose).</summary>
public sealed class DialConnection : IDisposable
{
    private readonly Action _release;
    private int _disposed;

    internal DialConnection(Socket socket, IPEndPoint remote, Action release)
    {
        Socket = socket;
        Remote = remote;
        _release = release;
    }

    public Socket Socket { get; }

    public IPEndPoint Remote { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            Socket.Dispose();
        }
        finally
        {
            _release();
        }
    }
}

/// <summary>
/// Opens the TCP connections of <c>Dial</c> (docs/sites.md, "Transport"): the port must be on Connect to, the address on
/// a reached network, the node under its limits; then the device's own socket error comes back as a DEVICE_* error and
/// the node's problems as SITE_* errors. Every dial is a line in the connection log.
/// </summary>
public sealed class Dialer(
    PortPolicy ports,
    NetworkScope networks,
    IDeviceConnector connector,
    INodeDns dns,
    ConnectionLog log,
    TimeProvider time,
    DialLimits? limits = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(2);

    private readonly DialLimits _limits = limits ?? DialLimits.Default;
    private int _shortOpen;
    private int _shortConnecting;
    private int _permanentOpen;
    private int _permanentConnecting;

    /// <summary>Raised when a count changes (the status feed throttles it).</summary>
    public event Action? CountsChanged;

    public int ShortConnections => Volatile.Read(ref _shortOpen);

    public int PermanentConnections => Volatile.Read(ref _permanentOpen);

    /// <summary>Opens the connection or says why not. <paramref name="server"/> names the caller in the log.</summary>
    public async Task<(DialResult Result, DialConnection? Connection)> OpenAsync(DialOpen open, string server, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(open);
        var permanent = open.Kind == DialKind.Permanent;
        var kind = permanent ? "Permanent" : "Short";
        var target = Target(open.Host, open.Port);
        var (result, connection) = await TryOpenAsync(open, permanent, ct).ConfigureAwait(false);
        log.Add(server, target, kind, result.Error == DialError.None ? "Connected" : LogText(result));
        return (result, connection);
    }

    private async Task<(DialResult, DialConnection?)> TryOpenAsync(DialOpen open, bool permanent, CancellationToken ct)
    {
        if (open.Port is <= 0 or > 65535 || string.IsNullOrWhiteSpace(open.Host))
            return (Site(DialError.SiteOutsideNetworks, AdminTexts.OutsideNetworks), null);
        if (!ports.Allows(PortUse.Connect, PortProtocol.Tcp, open.Port))
            return (Site(DialError.SitePortNotAllowed, AdminTexts.PortNotAllowed(PortProtocol.Tcp, open.Port)), null);

        var timeout = open.TimeoutMs > 0 ? TimeSpan.FromMilliseconds(Math.Min(open.TimeoutMs, MaxTimeout.TotalMilliseconds)) : DefaultTimeout;
        using var deadline = new CancellationTokenSource(timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

        IPAddress? address;
        if (IPAddress.TryParse(open.Host.Trim('[', ']'), out var literal))
        {
            address = literal;
            if (!networks.Reaches(address))
                return (Site(DialError.SiteOutsideNetworks, AdminTexts.OutsideNetworks), null);
        }
        else
        {
            IReadOnlyList<IPAddress> found;
            try
            {
                found = await dns.ResolveAsync(open.Host, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return (Device(DialError.DeviceHostNotFound, "The name was not found in time."), null);
            }

            if (found.Count == 0)
                return (Device(DialError.DeviceHostNotFound, "The name was not found."), null);
            address = found.FirstOrDefault(networks.Reaches);
            if (address is null)
                return (Site(DialError.SiteOutsideNetworks, AdminTexts.OutsideNetworks), null);
        }

        if (!TryEnter(permanent))
            return (Site(DialError.SiteBusy, AdminTexts.SiteBusy), null);
        var connected = false;
        try
        {
            var endpoint = new IPEndPoint(address, open.Port);
            Socket socket;
            try
            {
                socket = await connector.ConnectAsync(endpoint, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return (Device(DialError.DeviceTimedOut, "The device did not answer in time."), null);
            }
            catch (SocketException e)
            {
                return (Device(Map(e.SocketErrorCode), e.Message), null);
            }

            connected = true;
            Connected(permanent);
            return (new DialResult { Error = DialError.None, RemoteAddress = endpoint.ToString() }, new DialConnection(socket, endpoint, () => Closed(permanent)));
        }
        finally
        {
            if (!connected)
                LeaveConnecting(permanent, opened: false);
        }
    }

    /// <summary>The device's socket error as a dial error (anything unknown counts as unreachable).</summary>
    public static DialError Map(SocketError error) => error switch
    {
        SocketError.ConnectionRefused => DialError.DeviceRefused,
        SocketError.TimedOut => DialError.DeviceTimedOut,
        SocketError.ConnectionReset or SocketError.ConnectionAborted => DialError.DeviceReset,
        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => DialError.DeviceHostNotFound,
        _ => DialError.DeviceUnreachable,
    };

    // Short: counted from the start of the connect to the close (1,024), connecting apart (256). Permanent: only the
    // connecting ones are capped (64).
    private bool TryEnter(bool permanent)
    {
        if (permanent)
        {
            if (Interlocked.Increment(ref _permanentConnecting) > _limits.PermanentConnecting)
            {
                Interlocked.Decrement(ref _permanentConnecting);
                return false;
            }

            return true;
        }

        if (Interlocked.Increment(ref _shortOpen) > _limits.ShortOpen)
        {
            Interlocked.Decrement(ref _shortOpen);
            return false;
        }

        if (Interlocked.Increment(ref _shortConnecting) > _limits.ShortConnecting)
        {
            Interlocked.Decrement(ref _shortConnecting);
            Interlocked.Decrement(ref _shortOpen);
            return false;
        }

        CountsChanged?.Invoke();
        return true;
    }

    private void Connected(bool permanent)
    {
        if (permanent)
        {
            Interlocked.Decrement(ref _permanentConnecting);
            Interlocked.Increment(ref _permanentOpen);
        }
        else
        {
            Interlocked.Decrement(ref _shortConnecting);
        }

        CountsChanged?.Invoke();
    }

    private void LeaveConnecting(bool permanent, bool opened)
    {
        _ = opened;
        if (permanent)
        {
            Interlocked.Decrement(ref _permanentConnecting);
        }
        else
        {
            Interlocked.Decrement(ref _shortConnecting);
            Interlocked.Decrement(ref _shortOpen);
            CountsChanged?.Invoke();
        }
    }

    private void Closed(bool permanent)
    {
        if (permanent)
            Interlocked.Decrement(ref _permanentOpen);
        else
            Interlocked.Decrement(ref _shortOpen);
        CountsChanged?.Invoke();
    }

    private static DialResult Site(DialError error, string message) => new() { Error = error, Message = message };

    private static DialResult Device(DialError error, string message) => new() { Error = error, Message = message };

    private static string Target(string host, int port) =>
        host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[') ? $"[{host}]:{port}" : $"{host}:{port}";

    private static string LogText(DialResult result) => result.Error switch
    {
        DialError.DeviceRefused => "Refused",
        DialError.DeviceTimedOut => "Timed out",
        DialError.DeviceUnreachable => "Unreachable",
        DialError.DeviceReset => "Reset",
        DialError.DeviceHostNotFound => "Name not found",
        DialError.SiteOutsideNetworks => "Outside the networks",
        DialError.SitePortNotAllowed => "Port not allowed",
        DialError.SiteBusy => "Busy",
        _ => result.Message,
    };
}
