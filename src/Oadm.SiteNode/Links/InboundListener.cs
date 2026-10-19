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
    ConnectionState connection,
    NodeOptions options,
    ILogger<InboundListener> logger) : BackgroundService
{
    /// <summary>Handshakes in progress at most (a flood of half-open connections cannot hold the node).</summary>
    public const int MaxHandshakes = 64;

    private readonly SemaphoreSlim _handshakes = new(MaxHandshakes);
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
                    listener = new TcpListener(IPAddress.IPv6Any, options.ListenPort);
                    listener.Server.DualMode = true;
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

            if (!_handshakes.Wait(0, CancellationToken.None))
            {
                client.Dispose();
                continue;
            }

            _ = Task.Run(() => HandleAsync(client, ct), CancellationToken.None);
        }
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
        try
        {
            await links.ServeAsync(tls, link, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cannot wait for the server on port {Port}: {Reason}")]
    private static partial void LogCannotListen(ILogger logger, int port, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "TLS with {Remote} failed: {Reason}")]
    private static partial void LogHandshakeFailed(ILogger logger, string remote, string reason);
}
