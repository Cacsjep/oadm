using System.Net;
using System.Net.Sockets;

using Oadm.Contracts.SiteNode;
using Oadm.SiteNode.Admin;
using Oadm.SiteNode.Config;
using Oadm.SiteNode.Logging;
using Oadm.SiteNode.Networks;

namespace Oadm.SiteNode.Devices;

/// <summary>
/// The limits of one node (CLAUDE.md "# Sites", "Limits per node"). Probes (ProbeTcp, the ping fallback and every address of a
/// range scan) wait for one of <paramref name="Probes"/> places; at most <paramref name="Scans"/> range scans run at once.
/// </summary>
public sealed record DialLimits(int ShortOpen = 1024, int ShortConnecting = 256, int PermanentConnecting = 64, int Probes = 64, int Scans = 4)
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

/// <summary>What a probe found at an address and port.</summary>
public enum ProbeResult
{
    /// <summary>Nothing answered in time (or the network is unreachable).</summary>
    NoAnswer,

    /// <summary>A host answered with a reset: something is at this address, the port is closed.</summary>
    Refused,

    /// <summary>The port accepted the connection.</summary>
    Open,
}

/// <summary>
/// Opens the TCP connections of <c>Dial</c> (CLAUDE.md "# Sites", "Transport"): the port must be on Connect to, the address on
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
    DialLimits? limits = null) : IDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(2);

    private readonly DialLimits _limits = limits ?? DialLimits.Default;
    private readonly SemaphoreSlim _probes = new((limits ?? DialLimits.Default).Probes);
    private int _scans;
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

    /// <summary>
    /// A short connect that is closed at once (ProbeTcp, the ping fallback, the range scan). The caller checked the address
    /// and port; the probe waits for a free place of <see cref="DialLimits.Probes"/>, so probes never pile up sockets.
    /// </summary>
    public async Task<ProbeResult> ProbeAsync(IPEndPoint target, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        await _probes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var limit = new CancellationTokenSource(timeout, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
            using var socket = await connector.ConnectAsync(target, linked.Token).ConfigureAwait(false);
            return ProbeResult.Open;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ProbeResult.NoAnswer;
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionRefused)
        {
            return ProbeResult.Refused;
        }
        catch (SocketException)
        {
            return ProbeResult.NoAnswer;
        }
        finally
        {
            _probes.Release();
        }
    }

    /// <summary>A place for one range scan (released on dispose), or null while <see cref="DialLimits.Scans"/> run.</summary>
    public IDisposable? TryStartScan()
    {
        if (Interlocked.Increment(ref _scans) > _limits.Scans)
        {
            Interlocked.Decrement(ref _scans);
            return null;
        }

        return new ScanPlace(this);
    }

    public void Dispose() => _probes.Dispose();

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

        NodeEvents.Raise(CountsChanged);
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

        NodeEvents.Raise(CountsChanged);
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
            NodeEvents.Raise(CountsChanged);
        }
    }

    private void Closed(bool permanent)
    {
        if (permanent)
            Interlocked.Decrement(ref _permanentOpen);
        else
            Interlocked.Decrement(ref _shortOpen);
        NodeEvents.Raise(CountsChanged);
    }

    private sealed class ScanPlace(Dialer owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Decrement(ref owner._scans);
        }
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
