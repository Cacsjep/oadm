using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

using Oadm.Sites.Transport;

namespace Oadm.Server.Sites;

/// <summary>
/// The site port (docs/sites.md, Server listener): TCP on <c>Sites.ListenUrl</c> (default 0.0.0.0:5081), apart from the
/// clients' endpoint, open only while Accept site nodes is on. Every connection goes through TLS with the server's site
/// identity (any node key: the manager decides by fingerprint and Hello), each handshake within
/// <see cref="SiteTls.HandshakeTimeout"/>, at most a fixed number at once and a few per address (IPv6 per /64), so one
/// address cannot hold all of them. The roles then turn: the server is the gRPC client on the connection
/// (<see cref="NodeLink"/>). <see cref="DisposeAsync"/> waits for every handshake: none hands over a link afterwards.
/// </summary>
[SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs", Justification = "The site listener: connections of site nodes to the server, never to devices.")]
internal sealed partial class SiteListener : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<X509Certificate2> _identity;
    private readonly SemaphoreSlim _handshakes;
    private readonly Action<NodeLink> _accepted;
    private readonly ILogger _logger;
    private readonly AddressRateLimiter? _connections;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _stopping;
    private readonly int _maxPerAddress;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, int> _perAddress = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Task, byte> _running = new();
    private int _refusedPerAddress;
    private int _disposed;
    private Task _loop = Task.CompletedTask;

    /// <param name="connections">
    /// New connections per source address (IPv6 per /64), counted at accept before any TLS work; null = no limit.
    /// </param>
    /// <param name="maxHandshakesPerAddress">Handshakes in progress from one address (IPv6 per /64) at most.</param>
    public SiteListener(string url, Func<X509Certificate2> identity, int maxHandshakes, Action<NodeLink> accepted, ILogger logger, AddressRateLimiter? connections = null, int maxHandshakesPerAddress = int.MaxValue)
    {
        _connections = connections;
        _stopping = _stop.Token;
        _maxPerAddress = maxHandshakesPerAddress;
        var (address, port) = Parse(url);
        if (address.Equals(IPAddress.IPv6Any) && !Socket.OSSupportsIPv6)
        {
            address = IPAddress.Any;
        }

        _listener = new TcpListener(address, port);
        if (address.Equals(IPAddress.IPv6Any))
        {
            _listener.Server.DualMode = true;
        }

        _identity = identity;
        _handshakes = new SemaphoreSlim(maxHandshakes, maxHandshakes);
        _accepted = accepted;
        _logger = logger;
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Handshakes in progress (tests).</summary>
    internal int HandshakesRunning => _running.Count;

    /// <summary>Connections refused because their address had too many handshakes in progress (tests).</summary>
    internal int RefusedPerAddress => Volatile.Read(ref _refusedPerAddress);

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>"https://0.0.0.0:5081", "0.0.0.0:5081", "[::]:5081"; the port defaults to 5081.</summary>
    public static (IPAddress Address, int Port) Parse(string url)
    {
        var text = (url ?? string.Empty).Trim();
        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            text = text[(scheme + 3)..];
        }

        text = text.TrimEnd('/');
        var host = text;
        var port = 5081;
        if (text.StartsWith('[') && text.IndexOf(']', StringComparison.Ordinal) is var end and > 0)
        {
            host = text[1..end];
            if (text.Length > end + 2 && text[end + 1] == ':' && int.TryParse(text[(end + 2)..], out var bracketPort))
            {
                port = bracketPort;
            }
        }
        else if (text.Count(c => c == ':') == 1)
        {
            var colon = text.IndexOf(':', StringComparison.Ordinal);
            host = text[..colon];
            if (int.TryParse(text[(colon + 1)..], out var hostPort))
            {
                port = hostPort;
            }
        }

        return (IPAddress.TryParse(host, out var address) ? address : IPAddress.Any, Math.Clamp(port, 0, 65535));
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _listener.AcceptSocketAsync(_stopping).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            var from = (socket.RemoteEndPoint as IPEndPoint)?.Address;
            if (_connections is not null && !_connections.TryAcquire(from))
            {
                LogTooManyConnections(from);
                socket.Dispose();
                continue;
            }

            var key = from is null ? "?" : AddressRateLimiter.KeyOf(from);
            if (!EnterAddress(key))
            {
                Interlocked.Increment(ref _refusedPerAddress);
                LogTooManyHandshakes(from);
                socket.Dispose();
                continue;
            }

            if (!_handshakes.Wait(0, CancellationToken.None))
            {
                LeaveAddress(key);
                socket.Dispose();
                continue;
            }

            var handshake = Task.Run(() => HandshakeAsync(socket, key), CancellationToken.None);
            _running.TryAdd(handshake, 0);
            _ = handshake.ContinueWith(t => _running.TryRemove(t, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private bool EnterAddress(string key)
    {
        lock (_lock)
        {
            var running = _perAddress.GetValueOrDefault(key);
            if (running >= _maxPerAddress)
            {
                return false;
            }

            _perAddress[key] = running + 1;
            return true;
        }
    }

    private void LeaveAddress(string key)
    {
        lock (_lock)
        {
            if (_perAddress.TryGetValue(key, out var running) && running > 1)
            {
                _perAddress[key] = running - 1;
            }
            else
            {
                _perAddress.Remove(key);
            }
        }
    }

    // Never throws (it runs unobserved): the socket or TLS stream is disposed on every way out except the hand-over.
    private async Task HandshakeAsync(Socket socket, string key)
    {
        IPEndPoint? remote = null;
        SslStream? tls = null;
        var handedOver = false;
        try
        {
            remote = socket.RemoteEndPoint as IPEndPoint;
            tls = new SslStream(new NetworkStream(socket, ownsSocket: true), leaveInnerStreamOpen: false);
            socket.NoDelay = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
            timeout.CancelAfter(SiteTls.HandshakeTimeout);
            await tls.AuthenticateAsServerAsync(SiteTls.ServerOptions(_identity(), fingerprint => fingerprint is not null), timeout.Token).ConfigureAwait(false);
            if (!_stopping.IsCancellationRequested)
            {
                var link = new NodeLink(tls, remote);
                handedOver = true;
                _accepted(link);
            }
        }
#pragma warning disable CA1031 // A failed handshake only closes its own connection.
        catch (Exception e)
#pragma warning restore CA1031
        {
            LogHandshakeFailed(remote, e.Message);
        }
        finally
        {
            _handshakes.Release();
            LeaveAddress(key);
            if (!handedOver)
            {
                await Close(socket, tls).ConfigureAwait(false);
            }
        }
    }

    private static async Task Close(Socket socket, SslStream? tls)
    {
        try
        {
            if (tls is not null)
            {
                await tls.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                socket.Dispose();
            }
        }
#pragma warning disable CA1031 // Closing a connection that already failed.
        catch (Exception)
#pragma warning restore CA1031
        {
            socket.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        await _loop.ConfigureAwait(false);
        await Task.WhenAll(_running.Keys).ConfigureAwait(false); // they never throw
        _stop.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "TLS with a site node from {Remote} failed: {Reason}")]
    private partial void LogHandshakeFailed(IPEndPoint? remote, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Refused a connection to the site port from {Remote}: too many new connections from that address")]
    private partial void LogTooManyConnections(IPAddress? remote);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Refused a connection to the site port from {Remote}: too many handshakes from that address at once")]
    private partial void LogTooManyHandshakes(IPAddress? remote);
}

/// <summary>This server's own addresses and DNS name (what a node tries when the typed address no longer answers).</summary>
internal static class LocalAddresses
{
    /// <summary>Unicast addresses of the network cards that are up, IPv4 first; no loopback, no link-local.</summary>
    public static IReadOnlyList<IPAddress> Local()
    {
        try
        {
            return [.. NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => !IPAddress.IsLoopback(a) && !a.IsIPv6LinkLocal && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                .Distinct()];
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    /// <summary>The full DNS name of this computer, empty when it has no domain.</summary>
    public static string DnsName()
    {
        try
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            return string.IsNullOrEmpty(properties.DomainName) ? string.Empty : properties.HostName + "." + properties.DomainName;
        }
        catch (NetworkInformationException)
        {
            return string.Empty;
        }
    }
}
