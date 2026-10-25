using System.Net;
using System.Net.Sockets;
using System.Text;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

using Oadm.Sdk.Sites;
using Oadm.Sdk.Testing;
using Oadm.SiteNode;
using Oadm.SiteNode.Admin;
using Oadm.SiteNode.Config;
using Oadm.SiteNode.Networks;
using Oadm.SiteNode.Relays;
using Oadm.SiteNode.State;
using Oadm.SiteNode.Tests.Support;

namespace Oadm.Server.Tests.Sites;

/// <summary>
/// Relays into a site with the real OADM Site Node in this process (docs/spec/sites.md, "Relay"): the node listens on a port of
/// its reached card (loopback here), asks its user for a port it has not allowed, passes every datagram and connection to
/// the server, and the relay opens again after the node reconnects.
/// </summary>
public sealed class SiteRelayTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task AUdpRelayWaitsForApprovalThenCarriesDatagramsBothWays()
    {
        await using var server = await SiteServer.StartAsync();
        await using var node = await RelayNode.StartAsync(server.Address);
        var id = await BindAsync(server, node);
        var port = FreeUdpPort();

        await using var relay = server.Manager.OpenUdp(id, port, "NTP");
        await Eventually.TrueAsync(() => relay.State == RelayState.WaitingForApproval, "waiting for approval in the node app");
        Assert.Equal($"NTP at Building B: waiting for approval in the OADM Site Node app", relay.StateText);
        Assert.Contains(node.Get<NodeStore>().Snapshot().Requested, p => p is { Use: PortUse.Listen, Protocol: PortProtocol.Udp, Reason: "NTP" } && p.Port == port);

        node.Get<PortPolicy>().Allow(new PortEntry(PortUse.Listen, PortProtocol.Udp, port, "NTP"));
        await Eventually.TrueAsync(() => relay.State == RelayState.Running, "the relay running");
        Assert.Equal("NTP at Building B: running", relay.StateText);

        using var device = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var cts = new CancellationTokenSource(Wait);
        await device.SendAsync(Encoding.ASCII.GetBytes("what time is it"), new IPEndPoint(IPAddress.Loopback, port), cts.Token);
        var datagram = await relay.ReceiveAsync(cts.Token);
        Assert.Equal("what time is it", Encoding.ASCII.GetString(datagram.Data.Span));
        Assert.Equal((IPEndPoint)device.Client.LocalEndPoint!, datagram.Remote);

        await relay.SendAsync(Encoding.ASCII.GetBytes("noon"), datagram.Remote, cts.Token);
        var answer = await device.ReceiveAsync(cts.Token);
        Assert.Equal("noon", Encoding.ASCII.GetString(answer.Buffer));
        Assert.Equal(port, answer.RemoteEndPoint.Port);

        await relay.DisposeAsync();
        await Eventually.TrueAsync(() => node.Get<RelayHost>().Relays.Count == 0, "the node stops listening");
    }

    [Fact]
    public async Task AReleasedRelayGivesThePortBackAndADisposedOneKeepsIt()
    {
        await using var server = await SiteServer.StartAsync();
        await using var node = await RelayNode.StartAsync(server.Address);
        var id = await BindAsync(server, node);
        var port = FreeUdpPort();
        bool Allowed() => node.Get<NodeStore>().Snapshot().Allowed.Exists(p => p.Protocol == PortProtocol.Udp && p.Port == port);

        // Allowed from the server's request, then the plugin only stops: the port stays for its next start.
        var relay = server.Manager.OpenUdp(id, port, "NTP");
        await Eventually.TrueAsync(() => relay.State == RelayState.WaitingForApproval, "waiting for approval in the node app");
        node.Get<PortPolicy>().Allow(new PortEntry(PortUse.Listen, PortProtocol.Udp, port, "NTP"));
        await Eventually.TrueAsync(() => relay.State == RelayState.Running, "the relay running");
        await relay.DisposeAsync();
        await Eventually.TrueAsync(() => node.Get<RelayHost>().Relays.Count == 0, "the node stops listening");
        Assert.True(Allowed());

        // The plugin no longer serves there: the node gives the port back.
        var again = server.Manager.OpenUdp(id, port, "NTP");
        await Eventually.TrueAsync(() => again.State == RelayState.Running, "the relay running again without asking");
        await again.ReleaseAsync();
        await Eventually.TrueAsync(() => !Allowed(), "the port is given back");
        await Eventually.TrueAsync(() => node.Get<RelayHost>().Relays.Count == 0, "the node stops listening");
    }

    [Fact]
    public async Task ATcpRelayHandsEveryConnectionToTheServerAsAStream()
    {
        await using var server = await SiteServer.StartAsync();
        await using var node = await RelayNode.StartAsync(server.Address);
        var id = await BindAsync(server, node);
        var port = FreeTcpPort();
        node.Get<PortPolicy>().Allow(new PortEntry(PortUse.Listen, PortProtocol.Tcp, port, "Reports"));

        await using var relay = server.Manager.OpenTcp(id, port, "Reports");
        await Eventually.TrueAsync(() => relay.State == RelayState.Running, "the relay running");

        using var cts = new CancellationTokenSource(Wait);
        using var device = new TcpClient(AddressFamily.InterNetwork);
        await device.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        var deviceStream = device.GetStream();
        await using var connection = await relay.AcceptAsync(cts.Token);
        Assert.Equal(id, connection.SiteId);
        Assert.Equal(((IPEndPoint)device.Client.LocalEndPoint!).Port, connection.Remote.Port);

        await deviceStream.WriteAsync(Encoding.ASCII.GetBytes("GET /report"), cts.Token);
        Assert.Equal("GET /report", await ReadAsync(connection.Stream, 11, cts.Token));

        await connection.Stream.WriteAsync(Encoding.ASCII.GetBytes("200 OK"), cts.Token);
        Assert.Equal("200 OK", await ReadAsync(deviceStream, 6, cts.Token));

        // The plugin closes the connection: the device sees the end.
        await connection.DisposeAsync();
        var buffer = new byte[16];
        Assert.Equal(0, await deviceStream.ReadAsync(buffer, cts.Token));
    }

    [Fact]
    public async Task ADeviceThatSendsMoreThanThePluginReadsLosesItsConnection()
    {
        await using var server = await SiteServer.StartAsync();
        await using var node = await RelayNode.StartAsync(server.Address);
        var id = await BindAsync(server, node);
        var port = FreeTcpPort();
        node.Get<PortPolicy>().Allow(new PortEntry(PortUse.Listen, PortProtocol.Tcp, port, "EST"));
        await using var relay = server.Manager.OpenTcp(id, port, "EST");
        await Eventually.TrueAsync(() => relay.State == RelayState.Running, "the relay running");

        using var cts = new CancellationTokenSource(Wait);
        using var device = new TcpClient(AddressFamily.InterNetwork);
        await device.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        await using var connection = await relay.AcceptAsync(cts.Token);

        // The plugin reads nothing; the device keeps sending until the server lets go of its connection.
        var deviceStream = device.GetStream();
        var chunk = new byte[64 * 1024];
        var sending = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < 1024; i++)
                {
                    await deviceStream.WriteAsync(chunk, cts.Token);
                }
            }
            catch (IOException)
            {
                // closed by the node
            }
        });
        var read = 0;
        try
        {
            read = await deviceStream.ReadAsync(new byte[16], cts.Token);
        }
        catch (IOException)
        {
            // closed with a reset
        }

        Assert.Equal(0, read);
        await sending.WaitAsync(cts.Token);

        // The plugin gets what was buffered, then the end of the connection.
        var received = 0L;
        var buffer = new byte[64 * 1024];
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            int n;
            while ((n = await connection.Stream.ReadAsync(buffer, cts.Token)) > 0)
            {
                received += n;
            }
        });
        Assert.InRange(received, 0, global::Oadm.Server.Sites.RelayConnection.MaxBuffered);
    }

    [Fact]
    public async Task TheRelayIsSiteOfflineWhileTheNodeIsGoneAndOpensAgainWhenItReconnects()
    {
        await using var server = await SiteServer.StartAsync();
        var node = await RelayNode.StartAsync(server.Address);
        var folder = node.DataDirectory;
        var id = await BindAsync(server, node);
        var port = FreeUdpPort();
        node.Get<PortPolicy>().Allow(new PortEntry(PortUse.Listen, PortProtocol.Udp, port, "NTP"));
        await using var relay = server.Manager.OpenUdp(id, port, "NTP");
        await Eventually.TrueAsync(() => relay.State == RelayState.Running, "the relay running");

        await node.DisposeAsync(keepFolder: true);
        await Eventually.TrueAsync(() => relay.State == RelayState.SiteOffline, "site offline while the node is gone");
        Assert.Equal("NTP at Building B: site offline", relay.StateText);

        await using var again = await RelayNode.StartAsync(server.Address, folder);
        await Eventually.TrueAsync(() => relay.State == RelayState.Running, "running again after the node reconnected");
        using var device = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var cts = new CancellationTokenSource(Wait);
        await device.SendAsync(Encoding.ASCII.GetBytes("again"), new IPEndPoint(IPAddress.Loopback, port), cts.Token);
        Assert.Equal("again", Encoding.ASCII.GetString((await relay.ReceiveAsync(cts.Token)).Data.Span));
        RelayNode.TryDelete(folder);
    }

    [Fact]
    public async Task TheSitesPluginsSeeAreTheBoundOnesAndAnUnknownSiteIsRefused()
    {
        await using var server = await SiteServer.StartAsync();
        await using var node = await RelayNode.StartAsync(server.Address);
        var changed = 0;
        server.Manager.SitesChanged += (_, _) => Interlocked.Increment(ref changed);
        var id = await BindAsync(server, node);

        var site = Assert.Single(server.Manager.BoundSites);
        Assert.Equal((id, "Building B", true), (site.Id, site.Name, site.IsOnline));
        Assert.True(Volatile.Read(ref changed) > 0);
        Assert.Throws<ArgumentException>(() => server.Manager.OpenUdp(Guid.NewGuid(), 123, "NTP"));
    }

    private static async Task<Guid> BindAsync(SiteServer server, RelayNode node)
    {
        var waiting = await server.WaitingAsync(node.NodeId);
        var site = await server.Client.BindSiteAsync(new Contracts.V1.BindSiteRequest { NodeId = waiting.NodeId, Name = "Building B", PairingCode = node.Code });
        var id = Guid.Parse(site.Id);
        await Eventually.TrueAsync(() => server.Manager.IsUsable(id), "the bound site online");
        return id;
    }

    private static async Task<string> ReadAsync(Stream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        return Encoding.ASCII.GetString(buffer, 0, read);
    }

    private static int FreeUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>A site node whose only reached card is loopback (its relays bind 127.0.0.1, never a real network card).</summary>
    private sealed class RelayNode : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private int _disposed;

        private RelayNode(WebApplication app, string folder)
        {
            _app = app;
            DataDirectory = folder;
        }

        public string DataDirectory { get; }

        public string Code => Get<BindingState>().PairingCode;

        public string NodeId => Get<NodeStore>().Snapshot().Id;

        public T Get<T>()
            where T : notnull => _app.Services.GetRequiredService<T>();

        public static async Task<RelayNode> StartAsync(string serverAddress, string? folder = null)
        {
            var fresh = folder is null;
            folder ??= Path.Combine(Path.GetTempPath(), "oadm-relay-node-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(folder);
            if (fresh)
            {
                new NodeStore(folder).Update(c => c.ServerAddress = serverAddress);
            }

            var app = NodeHost.Build(new NodeOptions
            {
                DataDirectory = folder,
                UseSerilog = false,
                HostName = "relay-pc",
                Version = "3.7.0-test",
                AllowLoopbackDevices = true,
                NetworkCards = new FakeCards(new NetworkCard("lo", "Loopback", [new CardAddress(IPAddress.Loopback, 8)], HasDefaultRouter: false)),
                Dns = new FakeDns(),
                Pinger = new NoPinger(),
                Connector = new MapConnector(),
                MaxBackoff = TimeSpan.FromSeconds(1),
            });
            await app.StartAsync();
            return new RelayNode(app, folder);
        }

        public ValueTask DisposeAsync() => DisposeAsync(keepFolder: false);

        public async ValueTask DisposeAsync(bool keepFolder)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            Get<SiteNode.Links.LinkRegistry>().CloseAll();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await _app.StopAsync(stop.Token);
            }
            catch (OperationCanceledException)
            {
            }

            await _app.DisposeAsync();
            if (!keepFolder)
            {
                TryDelete(DataDirectory);
            }
        }

        public static void TryDelete(string folder)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
