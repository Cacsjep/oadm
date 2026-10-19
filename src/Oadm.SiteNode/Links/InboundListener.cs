using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Oadm.SiteNode.Admin;
using Oadm.SiteNode.Config;
using Oadm.SiteNode.Identity;
using Oadm.SiteNode.Feed;
using Oadm.SiteNode.State;
using Oadm.Sites.Transport;

namespace Oadm.SiteNode.Links;

/// <summary>
/// Mode Wait ("Wait for the server", docs/sites.md): the node listens on its port (5081) and the server opens TCP + TLS
/// to it. TLS is done here with the same options as every link (any client key: Hello answers anyone, the calls check
/// the bound identity), then the stream goes to Kestrel like an outbound link, so everything above the socket is the
/// same code. Every handshake ends within <see cref="SiteTls.HandshakeTimeout"/>.
/// </summary>
public sealed partial class InboundListener(
    NodeStore store,
    NodeIdentity identity,
    LinkRegistry links,
    BindingState binding,
    ConnectionState connection,
    NodeOptions options,
    ILogger<InboundListener> logger) : BackgroundService
{
    /// <summary>Handshakes in progress at most (a flood of half-open connections cannot hold the node).</summary>
    public const int MaxHandshakes = 64;

    /// <summary>New connections a minute from one address (IPv6 per /64), counted before TLS.</summary>
    public const int ConnectionsPerMinute = 30;

    /// <summary>Links of servers the node is not bound to, in all and from one address (IPv6 per /64).</summary>
    public const int MaxUnboundLinks = 16;
    public const int MaxUnboundPerAddress = 4;

    /// <summary>A link of a server the node is not bound to closes after this long without a call.</summary>
    public static readonly TimeSpan UnboundIdle = TimeSpan.FromSeconds(120);

    private readonly SemaphoreSlim _handshakes = new(MaxHandshakes);
    private readonly AddressRateLimiter _connections = new(ConnectionsPerMinute, 0, TimeSpan.FromMinutes(1), options.Time);
    private readonly Lock _unboundLock = new();
    private readonly Dictionary<string, int> _unbound = new(StringComparer.Ordinal);
    private int _unboundTotal;
    private readonly TaskCompletionSource<int> _port = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activePort;

    /// <summary>The port the node listens on once it does (tests ask for any free port).</summary>
    public Task<int> Port => _port.Task;

    public int ActivePort => Volatile.Read(ref _activePort);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var changed = new SemaphoreSlim(0);
        void OnChanged() => changed.Release();
        store.Changed += OnChanged;
        links.Changed += OnChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (store.Read(c => c.Mode) != NodeLinkMode.Wait)
                {
                    await changed.WaitAsync(stoppingToken).ConfigureAwait(false);
                    continue;
                }

                TcpListener listener;
                try
                {
                    listener = CreateListener(options.ListenPort);
                    listener.Start();
                }
                catch (SocketException e)
                {
                    LogCannotListen(logger, options.ListenPort, e.Message);
                    connection.Set(AdminTexts.PortInUse(PortProtocol.Tcp, options.ListenPort), false);
                    using var retry = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    retry.CancelAfter(TimeSpan.FromSeconds(30));
                    try
                    {
                        await changed.WaitAsync(retry.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                    }

                    continue;
                }

                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Volatile.Write(ref _activePort, port);
                _port.TrySetResult(port);
                using var listening = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var accepting = AcceptLoopAsync(listener, listening.Token);
                try
                {
                    while (store.Read(c => c.Mode) == NodeLinkMode.Wait && !stoppingToken.IsCancellationRequested)
                    {
                        var serving = links.BoundLinks > 0;
                        connection.Set(serving ? AdminTexts.Connected(links.Links.FirstOrDefault(l => !l.Outbound)?.RemoteHost ?? "") : AdminTexts.WaitingOnPort(port), serving);
                        await changed.WaitAsync(stoppingToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    listener.Stop();
                    await listening.CancelAsync().ConfigureAwait(false);
                    await accepting.ConfigureAwait(false);
                    Volatile.Write(ref _activePort, 0);
                    links.CloseWhere(l => !l.Outbound);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            store.Changed -= OnChanged;
            links.Changed -= OnChanged;
            changed.Dispose();
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            var from = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            if (!_connections.TryAcquire(from) || !_handshakes.Wait(0, CancellationToken.None))
            {
                client.Dispose();
                continue;
            }

            _ = Task.Run(() => HandleAsync(client, ct), CancellationToken.None);
        }
    }

    /// <summary>Dual mode on [::] where the OS has IPv6, else 0.0.0.0 (a host without IPv6 must still wait for the server).</summary>
    internal static TcpListener CreateListener(int port)
    {
        if (Socket.OSSupportsIPv6)
        {
            TcpListener? listener = null;
            try
            {
                listener = new TcpListener(IPAddress.IPv6Any, port);
                listener.Server.DualMode = true;
                return listener;
            }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported or SocketError.OperationNotSupported or SocketError.InvalidArgument)
            {
                listener?.Server.Dispose();
            }
        }

        return new TcpListener(IPAddress.Any, port);
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "";
        SslStream? tls = null;
        try
        {
            client.NoDelay = true;
            tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                handshake.CancelAfter(SiteTls.HandshakeTimeout);
                await tls.AuthenticateAsServerAsync(SiteTls.ServerOptions(identity.Certificate, fingerprint => fingerprint is not null), handshake.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException or SocketException)
        {
            LogHandshakeFailed(logger, remote, e.Message);
            if (tls is not null)
                await tls.DisposeAsync().ConfigureAwait(false);
            client.Dispose();
            return;
        }
        finally
        {
            _handshakes.Release();
        }

        var certificate = tls.RemoteCertificate is X509Certificate2 c2 ? c2 : X509CertificateLoader.LoadCertificate(tls.RemoteCertificate!.GetRawCertData());
        var link = new SiteLink(outbound: false, worker: false, remote, certificate, identity.Fingerprint);
        link.Touch(options.Time.GetTimestamp());
        var unbound = !binding.IsBoundTo(link.ServerFingerprintHex);
        if (unbound && !TryHoldUnbound(link.RemoteKey))
        {
            LogTooManyUnbound(logger, remote);
            await tls.DisposeAsync().ConfigureAwait(false);
            client.Dispose();
            return;
        }

        using var watching = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watchdog = unbound ? WatchUnboundAsync(link, watching.Token) : Task.CompletedTask;
        try
        {
            await links.ServeAsync(tls, link, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            await watching.CancelAsync().ConfigureAwait(false);
            await watchdog.ConfigureAwait(false);
        }
    }

    // Closes a link of a server the node is not bound to once it was idle for UnboundIdle; frees its place in the limits
    // when it ends or once that server is bound.
    private async Task WatchUnboundAsync(SiteLink link, CancellationToken ct)
    {
        try
        {
            var check = TimeSpan.FromSeconds(5);
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(check, options.Time, ct).ConfigureAwait(false);
                if (binding.IsBoundTo(link.ServerFingerprintHex))
                    return;
                if (options.Time.GetElapsedTime(link.LastActivity) >= UnboundIdle)
                {
                    LogUnboundIdle(logger, link.Remote);
                    link.Close();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            ReleaseUnbound(link.RemoteKey);
        }
    }

    private bool TryHoldUnbound(string key)
    {
        lock (_unboundLock)
        {
            var count = _unbound.GetValueOrDefault(key);
            if (_unboundTotal >= MaxUnboundLinks || count >= MaxUnboundPerAddress)
                return false;
            _unbound[key] = count + 1;
            _unboundTotal++;
            return true;
        }
    }

    private void ReleaseUnbound(string key)
    {
        lock (_unboundLock)
        {
            _unboundTotal--;
            if (_unbound.TryGetValue(key, out var count) && count > 1)
                _unbound[key] = count - 1;
            else
                _unbound.Remove(key);
        }
    }

    /// <summary>Links of servers the node is not bound to, open now (tests).</summary>
    public int UnboundLinks
    {
        get
        {
            lock (_unboundLock)
                return _unboundTotal;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Refused a connection from {Remote}: too many connections of servers this node is not bound to")]
    private static partial void LogTooManyUnbound(ILogger logger, string remote);

    [LoggerMessage(Level = LogLevel.Information, Message = "Closed the idle connection of {Remote}: this node is not bound to that server")]
    private static partial void LogUnboundIdle(ILogger logger, string remote);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cannot wait for the server on port {Port}: {Reason}")]
    private static partial void LogCannotListen(ILogger logger, int port, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "TLS with {Remote} failed: {Reason}")]
    private static partial void LogHandshakeFailed(ILogger logger, string remote, string reason);
}
