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
/// <see cref="SiteTls.HandshakeTimeout"/>, at most a fixed number at once. The roles then turn: the server is the gRPC
/// client on the connection (<see cref="NodeLink"/>).
/// </summary>
[SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs", Justification = "The site listener: connections of site nodes to the server, never to devices.")]
internal sealed partial class SiteListener : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<X509Certificate2> _identity;
    private readonly SemaphoreSlim _handshakes;
    private readonly Action<NodeLink> _accepted;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private Task _loop = Task.CompletedTask;

    public SiteListener(string url, Func<X509Certificate2> identity, int maxHandshakes, Action<NodeLink> accepted, ILogger logger)
    {
        var (address, port) = Parse(url);
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
        while (!_stop.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _listener.AcceptSocketAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            if (!_handshakes.Wait(0, CancellationToken.None))
            {
                socket.Dispose();
                continue;
            }

            _ = Task.Run(() => HandshakeAsync(socket), CancellationToken.None);
        }
    }

    private async Task HandshakeAsync(Socket socket)
    {
        var remote = socket.RemoteEndPoint as IPEndPoint;
        var tls = new SslStream(new NetworkStream(socket, ownsSocket: true), leaveInnerStreamOpen: false);
        try
        {
            socket.NoDelay = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(SiteTls.HandshakeTimeout);
            await tls.AuthenticateAsServerAsync(SiteTls.ServerOptions(_identity(), fingerprint => fingerprint is not null), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException or SocketException)
        {
            var from = remote?.ToString() ?? "?";
            LogHandshakeFailed(from, e.Message);
            await tls.DisposeAsync().ConfigureAwait(false);
            return;
        }
        finally
        {
            _handshakes.Release();
        }

        _accepted(new NodeLink(tls, remote, openedByNode: true));
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        await _loop.ConfigureAwait(false);
        _stop.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "TLS with a site node from {Remote} failed: {Reason}")]
    private partial void LogHandshakeFailed(string remote, string reason);
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
