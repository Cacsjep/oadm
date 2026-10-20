using System.Net;

using Google.Protobuf;

using Grpc.Core;

using Oadm.Contracts.SiteNode;
using Oadm.Sdk.Testing;
using Oadm.SiteNode.Admin;
using Oadm.SiteNode.Config;
using Oadm.SiteNode.Feed;
using Oadm.SiteNode.Links;
using Oadm.SiteNode.State;
using Oadm.SiteNode.Tests.Support;

namespace Oadm.SiteNode.Tests.InProcess;

/// <summary>Mode Connect (default): the real node connects to a test server over loopback and serves gRPC on that connection.</summary>
public sealed class OutboundTests
{
    [Fact]
    public async Task AnUnboundNodeConnectsAnswersHelloAndRefusesEverythingElse()
    {
        await using var server = new TestServer();
        await using var node = await TestNode.StartAsync(c => c.ServerAddress = server.Address);
        await using var link = await server.NextLinkAsync();

        var hello = await link.Client.HelloAsync(new HelloRequest());

        Assert.Equal(NodeState.UnboundOpen, hello.State);
        Assert.Equal(LinkRole.Control, hello.LinkRole);
        Assert.Equal("site-pc", hello.HostName);
        Assert.Equal(1, hello.ProtocolVersion);
        Assert.Contains("dial", hello.Features);
        Assert.Contains("relay.tcp", hello.Features);
        Assert.Empty(hello.BoundServerName);
        var refused = await Assert.ThrowsAsync<RpcException>(async () => await link.Client.ResolveAsync(new ResolveRequest { Name = "camera" }));
        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        Assert.True(node.Get<ConnectionState>().Connected);
        Assert.Equal(AdminTexts.Connected(server.Address), node.Get<ConnectionState>().Text);
    }

    [Fact]
    public async Task AnIdleLinkOfANodeWaitingToBeBoundStaysOpen()
    {
        // Kestrel's keep-alive must not close a link without streams: the server keeps a waiting node's link idle until
        // an administrator binds it. (Timeout.InfiniteTimeSpan closed it after about a second.)
        await using var server = new TestServer();
        await using var node = await TestNode.StartAsync(c => c.ServerAddress = server.Address);
        await using var link = await server.NextLinkAsync();
        await link.Client.HelloAsync(new HelloRequest());

        await Eventually.StaysTrueAsync(() => node.Get<LinkRegistry>().Links.Count == 1, "the idle link stays open", TimeSpan.FromSeconds(4));

        Assert.Equal(NodeState.UnboundOpen, (await link.Client.HelloAsync(new HelloRequest())).State);
    }

    [Fact]
    public async Task TheRightCodeBindsAndTheBoundServerGetsItsNameAndEveryCall()
    {
        await using var echo = new EchoServer();
        await using var server = new TestServer();
        await using var node = await TestNode.StartAsync(c =>
        {
            c.ServerAddress = server.Address;
            Allow.Connect(c, echo.Port);
        });
        await using var link = await server.NextLinkAsync();

        await link.BindAsync(node.Code, "Building B", "OADM-SRV");

        var hello = await link.Client.HelloAsync(new HelloRequest());
        Assert.Equal(NodeState.Bound, hello.State);
        Assert.Equal("OADM-SRV", hello.BoundServerName);
        Assert.Equal("Building B", hello.SiteName);
        var bound = node.Get<BindingState>().Bound!;
        Assert.Equal(server.FingerprintHex, bound.Fingerprint);
        Assert.Equal(NodeBinding.Bound, node.Get<BindingState>().Binding);

        var payload = new byte[300 * 1024];
        Random.Shared.NextBytes(payload);
        Assert.Equal(payload, await DialEchoAsync(link.Client, "127.0.0.1", echo.Port, payload));
        Assert.Contains(node.Get<Logging.ConnectionLog>().Newest(10), e => e.Target == $"127.0.0.1:{echo.Port}" && e.Result == "Connected" && e.Server == "OADM-SRV");
    }

    [Fact]
    public async Task AWrongCodeIsRefusedAndTheNodeStaysUnbound()
    {
        await using var server = new TestServer();
        await using var node = await TestNode.StartAsync(c => c.ServerAddress = server.Address);
        await using var link = await server.NextLinkAsync();
        var wrong = node.Code[0] == 'A' ? "B" + node.Code[1..] : "A" + node.Code[1..];

        var error = await Assert.ThrowsAsync<RpcException>(() => link.BindAsync(wrong));

        Assert.Equal(StatusCode.PermissionDenied, error.StatusCode);
        Assert.Equal(AdminTexts.WrongCode, error.Status.Detail);
        Assert.Equal(NodeBinding.Open, node.Get<BindingState>().Binding);
        Assert.Single(node.Get<BindingState>().WrongCodes);
    }

    [Fact]
    public async Task DeviceErrorsAndSiteErrorsComeBackApart()
    {
        await using var server = new TestServer();
        var closed = Ports.FreeTcp();
        await using var node = await TestNode.StartAsync(c =>
        {
            c.ServerAddress = server.Address;
            Allow.Connect(c, closed);
        });
        await using var link = await server.NextLinkAsync();
        await link.BindAsync(node.Code);

        Assert.Equal(DialError.DeviceRefused, (await DialResultAsync(link.Client, "127.0.0.1", closed)).Error);
        Assert.Equal(DialError.SitePortNotAllowed, (await DialResultAsync(link.Client, "127.0.0.1", 8443)).Error);
        Assert.Equal(DialError.SiteOutsideNetworks, (await DialResultAsync(link.Client, "10.0.0.20", 80)).Error);
        Assert.Equal(DialError.DeviceHostNotFound, (await DialResultAsync(link.Client, "camera.example", 80)).Error);
    }

    [Fact]
    public async Task TheNodeConnectsAgainAfterTheLinkDrops()
    {
        await using var server = new TestServer();
        await using var node = await TestNode.StartAsync(c => c.ServerAddress = server.Address);
        var first = await server.NextLinkAsync();
        await first.Client.HelloAsync(new HelloRequest());

        await first.DisposeAsync();

        await using var second = await server.NextLinkAsync();
        Assert.Equal(NodeState.UnboundOpen, (await second.Client.HelloAsync(new HelloRequest())).State);
    }

    [Fact]
    public async Task OpenConnectionsOpensWorkerLinksToTheSameServer()
    {
        await using var server = new TestServer();
        await using var node = await TestNode.StartAsync(c => c.ServerAddress = server.Address);
        await using var control = await server.NextLinkAsync();
        await control.BindAsync(node.Code);

        var reply = await control.Client.OpenConnectionsAsync(new OpenConnectionsRequest { Count = 2 });

        Assert.Equal(2, reply.Open);
        await using var worker1 = await server.NextLinkAsync();
        await using var worker2 = await server.NextLinkAsync();
        var workerHello = await worker1.Client.HelloAsync(new HelloRequest());
        Assert.Equal(NodeState.Bound, workerHello.State);
        Assert.Equal(LinkRole.Worker, workerHello.LinkRole);
        Assert.Equal(LinkRole.Control, (await control.Client.HelloAsync(new HelloRequest())).LinkRole);
        Assert.Equal(OutboundConnector.MaxWorkers, (await control.Client.OpenConnectionsAsync(new OpenConnectionsRequest { Count = 20 })).Open);
    }

    [Fact]
    public async Task ANewServerIdentityBindsOnlyWithThePairingCodeAfterAccept()
    {
        await using var server = new TestServer();
        await using var node = await TestNode.StartAsync(c => c.ServerAddress = server.Address);
        var link = await server.NextLinkAsync();
        await link.BindAsync(node.Code);
        await using var replaced = new TestServer();

        // The server's key is lost (a new identity at the same address): the node does not connect and asks, naming where
        // the request came from.
        node.Get<NodeStore>().Update(c => c.ServerAddress = replaced.Address);
        await Eventually.TrueAsync(() => node.Get<NodeStore>().Read(c => c.NewServerIdentity) == replaced.FingerprintHex, "the new identity is noted");
        Assert.StartsWith(replaced.Address, node.Get<NodeStore>().Read(c => c.NewServerFrom), StringComparison.Ordinal);
        Assert.False(node.Get<ConnectionState>().Connected);
        await link.DisposeAsync();

        var response = node.Get<AdminChannel.AdminHandler>().Handle(AdminRequest.Of(AdminCommands.AcceptServer), isAdmin: true);
        Assert.True(response.Ok);
        Assert.Equal(Sites.Binding.PairingCode.Format(node.Code), response.PairingCode);

        // Accepting alone binds nothing: the new identity is treated like a server at an open node until it proves the code.
        var accepted = await replaced.NextWorkingLinkAsync();
        Assert.Equal(NodeState.UnboundOpen, (await accepted.Client.HelloAsync(new HelloRequest())).State);
        Assert.Equal(server.FingerprintHex, node.Get<BindingState>().Bound!.Fingerprint);
        var refused = await Assert.ThrowsAsync<RpcException>(async () => await accepted.Client.ResolveAsync(new ResolveRequest { Name = "x" }));
        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        var wrong = node.Code[0] == 'A' ? "B" + node.Code[1..] : "A" + node.Code[1..];
        Assert.Equal(StatusCode.PermissionDenied, (await Assert.ThrowsAsync<RpcException>(() => accepted.BindAsync(wrong))).StatusCode);

        await accepted.BindAsync(node.Code, "Building B", "OADM-SRV");

        Assert.Equal(replaced.FingerprintHex, node.Get<BindingState>().Bound!.Fingerprint);
        Assert.Equal(NodeState.Bound, (await accepted.Client.HelloAsync(new HelloRequest())).State);
        Assert.Empty(node.Get<NodeStore>().Read(c => c.NewServerIdentity));
    }

    [Theory]
    [InlineData(0, false, 1, 2)]     // closed at once: the first wait is the backoff
    [InlineData(5, false, 8, 16)]    // closed at once again and again: 1, 2, 4, 8 ...
    [InlineData(45, false, 8, 2)]    // lasted 30 s or more: starts over
    [InlineData(1, true, 8, 2)]      // served the bound server: starts over
    [InlineData(1, false, 60, 60)]   // never more than the maximum
    public void TheBackoffStartsOverOnlyAfterASessionThatWorked(int lastedSeconds, bool served, int backoff, int next)
    {
        var (wait, after) = OutboundConnector.AfterSession(TimeSpan.FromSeconds(backoff), TimeSpan.FromSeconds(lastedSeconds), served, TimeSpan.FromSeconds(60));

        Assert.Equal(served || lastedSeconds >= 30 ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(backoff), wait);
        Assert.Equal(TimeSpan.FromSeconds(next), after);
    }

    [Fact]
    public async Task AServerThatClosesEveryLinkAtOnceIsNotRetriedEverySecond()
    {
        // With the maximum at 4 s: 1, 2, 4, 4 ... the first links come quickly, then the waits grow.
        await using var server = new TestServer();
        await using var node = await TestNode.StartAsync(c => c.ServerAddress = server.Address, o => o.MaxBackoff = TimeSpan.FromSeconds(4));
        var started = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 4; i++)
            await (await server.NextLinkAsync()).DisposeAsync();

        // Four links take at least 1 + 2 + 4 s of waiting between them.
        Assert.True(started.Elapsed >= TimeSpan.FromSeconds(6.5), $"4 links in {started.Elapsed}");
    }

    [Fact]
    public async Task WatchSendsTheStatusAndEveryChange()
    {
        await using var server = new TestServer();
        await using var node = await TestNode.StartAsync(c => c.ServerAddress = server.Address);
        await using var link = await server.NextLinkAsync();
        await link.BindAsync(node.Code, "Building B");
        using var watch = link.Client.Watch(new WatchRequest());

        Assert.True(await watch.ResponseStream.MoveNext());
        var first = watch.ResponseStream.Current;
        Assert.Equal(NodeState.Bound, first.State);
        Assert.Equal("Building B", first.SiteName);
        Assert.Contains(first.Cards, c => c.Id == "cam" && c.Reached);
        Assert.Contains(first.Cards, c => c.Id == "office" && !c.Reached && c.HasDefaultRouter);
        Assert.Equal(3, first.Allowed.Count);

        await link.Client.RequestPortsAsync(new RequestPortsRequest { Ports = { new PortRule { Direction = PortDirection.Connect, Protocol = Protocol.Tcp, Port = 8443, Reason = "camera 192.168.0.30" } } });

        NodeStatus next;
        do
        {
            Assert.True(await watch.ResponseStream.MoveNext());
            next = watch.ResponseStream.Current;
        }
        while (next.Requested.Count == 0);
        Assert.Equal(8443, next.Requested[0].Port);
        Assert.Equal("camera 192.168.0.30", next.Requested[0].Reason);
    }

    [Fact]
    public async Task UnbindOpensBindingAgainAndTheServerMayBindOnTheSameLink()
    {
        await using var server = new TestServer();
        await using var node = await TestNode.StartAsync(c => c.ServerAddress = server.Address);
        await using var link = await server.NextLinkAsync();
        await link.BindAsync(node.Code);

        await link.Client.UnbindAsync(new UnbindRequest());

        Assert.Equal(NodeBinding.Open, node.Get<BindingState>().Binding);
        Assert.Equal(NodeState.UnboundOpen, (await link.Client.HelloAsync(new HelloRequest())).State);
        await link.BindAsync(node.Code, "Building C");
        Assert.Equal("Building C", node.Get<BindingState>().Bound!.SiteName);
    }

    [Fact]
    public async Task ResetGivesANewIdentityAndTheNodeConnectsAgainUnbound()
    {
        await using var server = new TestServer();
        await using var node = await TestNode.StartAsync(c => c.ServerAddress = server.Address);
        var link = await server.NextLinkAsync();
        await link.BindAsync(node.Code);
        var before = link.NodeFingerprintHex;
        var id = node.Get<NodeStore>().Read(c => c.Id);

        var response = node.Get<AdminChannel.AdminHandler>().Handle(AdminRequest.Of(AdminCommands.Reset), isAdmin: true);

        Assert.True(response.Ok);
        await using var again = await server.NextLinkAsync();
        var hello = await again.Client.HelloAsync(new HelloRequest());
        Assert.Equal(NodeState.UnboundOpen, hello.State);
        Assert.NotEqual(id, hello.NodeId);
        Assert.NotEqual(before, again.NodeFingerprintHex);
        await link.DisposeAsync();
    }

    [Fact]
    public async Task WithBindingClosedAnUnboundNodeDoesNotConnectUntilAllowBinding()
    {
        await using var server = new TestServer();
        await using var node = await TestNode.StartAsync(c =>
        {
            c.ServerAddress = server.Address;
            c.BindingSecondsLeft = 0;
        });

        await Eventually.TrueAsync(() => node.Get<ConnectionState>().Text == AdminTexts.BindingClosed, "the app says binding is closed");
        Assert.False(node.Get<ConnectionState>().Connected);

        node.Get<BindingState>().AllowBinding();

        await using var link = await server.NextLinkAsync();
        Assert.Equal(NodeState.UnboundOpen, (await link.Client.HelloAsync(new HelloRequest())).State);
    }

    [Fact]
    public async Task CancellingADialClosesTheDevicesSocket()
    {
        using var device = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        device.Start();
        var port = ((IPEndPoint)device.LocalEndpoint).Port;
        await using var server = new TestServer();
        await using var node = await TestNode.StartAsync(c =>
        {
            c.ServerAddress = server.Address;
            Allow.Connect(c, port);
        });
        await using var link = await server.NextLinkAsync();
        await link.BindAsync(node.Code);
        using var cancel = new CancellationTokenSource();
        var call = link.Client.Dial(cancellationToken: cancel.Token);
        await call.RequestStream.WriteAsync(new DialMessage { Open = new DialOpen { Host = "127.0.0.1", Port = port, Kind = DialKind.Permanent } });
        Assert.True(await call.ResponseStream.MoveNext());
        using var accepted = await device.AcceptSocketAsync();
        Assert.Equal(1, node.Get<Devices.Dialer>().PermanentConnections);

        await cancel.CancelAsync();
        call.Dispose();

        var buffer = new byte[1];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var read = await accepted.ReceiveAsync(buffer, System.Net.Sockets.SocketFlags.None, timeout.Token);
        Assert.Equal(0, read); // the node closed its side
        await Eventually.TrueAsync(() => node.Get<Devices.Dialer>().PermanentConnections == 0, "the connection is counted out");
    }

    internal static async Task<DialResult> DialResultAsync(SiteNodeService.SiteNodeServiceClient client, string host, int port, DialKind kind = DialKind.Short)
    {
        using var call = client.Dial();
        await call.RequestStream.WriteAsync(new DialMessage { Open = new DialOpen { Host = host, Port = port, Kind = kind, TimeoutMs = 10_000 } });
        Assert.True(await call.ResponseStream.MoveNext());
        var result = call.ResponseStream.Current.Result;
        await call.RequestStream.CompleteAsync();
        return result;
    }

    internal static async Task<byte[]> DialEchoAsync(SiteNodeService.SiteNodeServiceClient client, string host, int port, byte[] payload)
    {
        using var call = client.Dial();
        await call.RequestStream.WriteAsync(new DialMessage { Open = new DialOpen { Host = host, Port = port, Kind = DialKind.Short } });
        Assert.True(await call.ResponseStream.MoveNext());
        Assert.Equal(DialError.None, call.ResponseStream.Current.Result.Error);
        Assert.Equal(new IPEndPoint(IPAddress.Loopback, port).ToString(), call.ResponseStream.Current.Result.RemoteAddress);
        var reading = Task.Run(async () =>
        {
            using var received = new MemoryStream();
            while (await call.ResponseStream.MoveNext())
            {
                if (call.ResponseStream.Current.KindCase == DialMessage.KindOneofCase.End)
                    break;
                call.ResponseStream.Current.Data.WriteTo(received);
            }

            return received.ToArray();
        });
        for (var offset = 0; offset < payload.Length; offset += 32 * 1024)
            await call.RequestStream.WriteAsync(new DialMessage { Data = ByteString.CopyFrom(payload, offset, Math.Min(32 * 1024, payload.Length - offset)) });
        await call.RequestStream.WriteAsync(new DialMessage { End = true });
        var echoed = await reading;
        await call.RequestStream.CompleteAsync();
        return echoed;
    }
}
