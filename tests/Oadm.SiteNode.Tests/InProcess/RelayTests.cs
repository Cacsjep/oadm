using System.Net;
using System.Net.Sockets;
using System.Text;

using Google.Protobuf;

using Grpc.Core;

using Oadm.Contracts.SiteNode;
using Oadm.Sdk.Testing;
using Oadm.SiteNode.Admin;
using Oadm.SiteNode.Config;
using Oadm.SiteNode.Tests.Support;

namespace Oadm.SiteNode.Tests.InProcess;

/// <summary><c>Relay</c>: UDP and TCP listeners on the reached cards for as long as the call lives, only on Listen on ports.</summary>
public sealed class RelayTests
{
    [Fact]
    public async Task UdpDatagramsReachTheServerAndAnswersGoBackToTheSender()
    {
        var port = Ports.FreeUdp();
        await using var bound = await BoundNodeAsync(c => c.Allowed.Add(new PortEntry(PortUse.Listen, PortProtocol.Udp, port, "NTP server")));
        using var relay = bound.Link.Client.Relay();
        await relay.RequestStream.WriteAsync(new RelayMessage { Open = new RelayOpen { Protocol = Protocol.Udp, Port = port, Reason = "NTP server" } });
        Assert.Equal(RelayStateKind.RelayStateRunning, (await NextAsync(relay)).State.State);

        using var device = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await device.SendAsync(Encoding.ASCII.GetBytes("time?"), new IPEndPoint(IPAddress.Loopback, port));
        var packet = (await NextAsync(relay)).Packet;
        Assert.Equal("time?", packet.Data.ToStringUtf8());
        Assert.Equal(device.Client.LocalEndPoint!.ToString(), packet.RemoteAddress);

        await relay.RequestStream.WriteAsync(new RelayMessage { Packet = new RelayPacket { RemoteAddress = packet.RemoteAddress, Data = ByteString.CopyFromUtf8("12:00") } });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var answer = await device.ReceiveAsync(timeout.Token);
        Assert.Equal("12:00", Encoding.ASCII.GetString(answer.Buffer));
        Assert.Equal(port, answer.RemoteEndPoint.Port);
        Assert.Contains(bound.Node.Get<Feed.NodeStatusFeed>().View().Relays, r => r.Port == port && r.State == "Running");

        await relay.RequestStream.CompleteAsync();
        relay.Dispose();
        await Eventually.TrueAsync(() => bound.Node.Get<Relays.RelayHost>().Relays.Count == 0, "the relay ends with the call");
        await Eventually.TrueAsync(() => CanBindUdp(port), "the port is free again");
    }

    [Fact]
    public async Task TcpConnectionsPassThroughWithHalfCloseAndClose()
    {
        var port = Ports.FreeTcp();
        await using var bound = await BoundNodeAsync(c => c.Allowed.Add(new PortEntry(PortUse.Listen, PortProtocol.Tcp, port, "syslog")));
        using var relay = bound.Link.Client.Relay();
        await relay.RequestStream.WriteAsync(new RelayMessage { Open = new RelayOpen { Protocol = Protocol.Tcp, Port = port, Reason = "syslog" } });
        Assert.Equal(RelayStateKind.RelayStateRunning, (await NextAsync(relay)).State.State);

        using var device = new TcpClient();
        await device.ConnectAsync(IPAddress.Loopback, port);
        var stream = device.GetStream();
        var accepted = (await NextAsync(relay)).Packet;
        Assert.True(accepted.Accepted);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("hello"));
        device.Client.Shutdown(SocketShutdown.Send);

        var received = new StringBuilder();
        while (true)
        {
            var packet = (await NextAsync(relay)).Packet;
            Assert.Equal(accepted.ConnectionId, packet.ConnectionId);
            if (packet.End)
                break;
            received.Append(packet.Data.ToStringUtf8());
        }

        Assert.Equal("hello", received.ToString());
        await relay.RequestStream.WriteAsync(new RelayMessage { Packet = new RelayPacket { ConnectionId = accepted.ConnectionId, Data = ByteString.CopyFromUtf8("ok") } });
        await relay.RequestStream.WriteAsync(new RelayMessage { Packet = new RelayPacket { ConnectionId = accepted.ConnectionId, End = true } });
        var buffer = new byte[16];
        var answer = new StringBuilder();
        int read;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while ((read = await stream.ReadAsync(buffer, timeout.Token)) > 0)
            answer.Append(Encoding.ASCII.GetString(buffer, 0, read));
        Assert.Equal("ok", answer.ToString());
        await relay.RequestStream.WriteAsync(new RelayMessage { Packet = new RelayPacket { ConnectionId = accepted.ConnectionId, Closed = true } });
        await relay.RequestStream.CompleteAsync();
    }

    [Fact]
    public async Task APortNotOnListenOnWaitsForApprovalAndStartsOnceAllowed()
    {
        var port = Ports.FreeTcp();
        // TCP on Connect to never allows a listener on the same port.
        await using var bound = await BoundNodeAsync(c => Allow.Connect(c, port));
        using var relay = bound.Link.Client.Relay();
        await relay.RequestStream.WriteAsync(new RelayMessage { Open = new RelayOpen { Protocol = Protocol.Tcp, Port = port, Reason = "RADIUS" } });

        var waiting = (await NextAsync(relay)).State;
        Assert.Equal(RelayStateKind.RelayStateWaitingForApproval, waiting.State);
        Assert.Equal(AdminTexts.WaitingForApproval(PortProtocol.Tcp, port), waiting.Message);
        var request = Assert.Single(bound.Node.Get<NodeStore>().Read(c => c.Requested.ToList()));
        Assert.Equal((PortUse.Listen, PortProtocol.Tcp, port, "RADIUS"), (request.Use, request.Protocol, request.Port, request.Reason));

        bound.Node.Get<PortPolicy>().Allow(request);

        Assert.Equal(RelayStateKind.RelayStateRunning, (await NextAsync(relay)).State.State);
        await relay.RequestStream.CompleteAsync();
    }

    [Fact]
    public async Task APortAnotherProgramHoldsIsReported()
    {
        using var other = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
        other.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)other.LocalEndPoint!).Port;
        await using var bound = await BoundNodeAsync(c => c.Allowed.Add(new PortEntry(PortUse.Listen, PortProtocol.Udp, port, "NTP server")));
        using var relay = bound.Link.Client.Relay();
        await relay.RequestStream.WriteAsync(new RelayMessage { Open = new RelayOpen { Protocol = Protocol.Udp, Port = port, Reason = "NTP server" } });

        var state = (await NextAsync(relay)).State;

        Assert.Equal(RelayStateKind.RelayStatePortInUse, state.State);
        Assert.Equal(AdminTexts.PortInUse(PortProtocol.Udp, port), state.Message);
        await relay.RequestStream.CompleteAsync();
    }

    [Fact]
    public async Task ARelayFollowsTheCardsWhenTheyChange()
    {
        var port = Ports.FreeUdp();
        await using var bound = await BoundNodeAsync(c => c.Allowed.Add(new PortEntry(PortUse.Listen, PortProtocol.Udp, port, "NTP server")));
        using var relay = bound.Link.Client.Relay();
        await relay.RequestStream.WriteAsync(new RelayMessage { Open = new RelayOpen { Protocol = Protocol.Udp, Port = port, Reason = "NTP server" } });
        Assert.Equal(RelayStateKind.RelayStateRunning, (await NextAsync(relay)).State.State);
        var cards = (FakeCards)bound.Node.Options.NetworkCards!;
        var networks = bound.Node.Get<Networks.NetworkScope>();
        networks.CheckCards();

        // The camera card loses its address (DHCP gone): the relay lets go of it and waits.
        var camera = cards.List()[0];
        cards.Replace(0, camera with { Addresses = [] });
        networks.CheckCards(); // the card watcher may see it first; either raises the change once
        Assert.Equal(RelayStateKind.RelayStateWaitingForApproval, (await NextAsync(relay)).State.State);
        await Eventually.TrueAsync(() => CanBindUdp(port), "the old address is free");

        // It comes back: the relay binds again.
        cards.Replace(0, camera);
        networks.CheckCards();
        Assert.Equal(RelayStateKind.RelayStateRunning, (await NextAsync(relay)).State.State);
        Assert.False(CanBindUdp(port));
        await relay.RequestStream.CompleteAsync();
    }

    [Fact]
    public async Task ATcpRelayHoldsAtMostItsLimitOfConnections()
    {
        var port = Ports.FreeTcp();
        await using var bound = await BoundNodeAsync(c => c.Allowed.Add(new PortEntry(PortUse.Listen, PortProtocol.Tcp, port, "syslog")));
        using var relay = bound.Link.Client.Relay();
        await relay.RequestStream.WriteAsync(new RelayMessage { Open = new RelayOpen { Protocol = Protocol.Tcp, Port = port, Reason = "syslog" } });
        Assert.Equal(RelayStateKind.RelayStateRunning, (await NextAsync(relay)).State.State);

        var devices = new List<TcpClient>();
        try
        {
            for (var i = 0; i < Relays.RelayHost.MaxTcpConnections; i++)
            {
                var device = new TcpClient();
                devices.Add(device);
                await device.ConnectAsync(IPAddress.Loopback, port);
                Assert.True((await NextAsync(relay)).Packet.Accepted);
            }

            using var extra = new TcpClient();
            await extra.ConnectAsync(IPAddress.Loopback, port);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var read = 0;
            try
            {
                read = await extra.GetStream().ReadAsync(new byte[1], timeout.Token);
            }
            catch (IOException)
            {
                // closed with a reset
            }

            Assert.Equal(0, read);
            await Eventually.TrueAsync(() => bound.Node.Get<Logging.ConnectionLog>().Newest(10).Any(e => e.Result == "Too many connections"), "the refusal is logged");
        }
        finally
        {
            foreach (var device in devices)
                device.Dispose();
        }

        await relay.RequestStream.CompleteAsync();
    }

    [Fact]
    public async Task AnUnboundNodeOpensNoRelay()
    {
        await using var server = new TestServer();
        await using var node = await TestNode.StartAsync(c => c.ServerAddress = server.Address);
        await using var link = await server.NextLinkAsync();
        using var relay = link.Client.Relay();
        await relay.RequestStream.WriteAsync(new RelayMessage { Open = new RelayOpen { Protocol = Protocol.Udp, Port = 123 } });

        var error = await Assert.ThrowsAsync<RpcException>(() => relay.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal(StatusCode.FailedPrecondition, error.StatusCode);
    }

    private static async Task<RelayMessage> NextAsync(AsyncDuplexStreamingCall<RelayMessage, RelayMessage> relay)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Assert.True(await relay.ResponseStream.MoveNext(timeout.Token));
        return relay.ResponseStream.Current;
    }

    private static bool CanBindUdp(int port)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static async Task<BoundNode> BoundNodeAsync(Action<NodeConfig> config)
    {
        var server = new TestServer();
        var node = await TestNode.StartAsync(c =>
        {
            c.ServerAddress = server.Address;
            config(c);
        });
        var link = await server.NextLinkAsync();
        await link.BindAsync(node.Code);
        return new BoundNode(server, node, link);
    }

    private sealed record BoundNode(TestServer Server, TestNode Node, ServerLink Link) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Link.DisposeAsync();
            await Server.DisposeAsync();
            await Node.DisposeAsync();
        }
    }
}
