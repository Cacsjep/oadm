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
        Assert.Equal(NodeMode.Connect, hello.Mode);
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
        Assert.Equal(NodeState.Bound, (await worker1.Client.HelloAsync(new HelloRequest())).State);
        Assert.Equal(OutboundConnector.MaxWorkers, (await control.Client.OpenConnectionsAsync(new OpenConnectionsRequest { Count = 20 })).Open);
    }

    [Fact]
    public async Task ABoundNodeRefusesAnotherServerKeyAndAcceptsItOnlyAfterAccept()
    {
        await using var server = new TestServer();
        await using var node = await TestNode.StartAsync(c => c.ServerAddress = server.Address);
        var link = await server.NextLinkAsync();
        await link.BindAsync(node.Code);
        await using var replaced = new TestServer();

        // The server's key is lost (a new identity at the same address): the node does not connect and asks.
        node.Get<NodeStore>().Update(c => c.ServerAddress = replaced.Address);
        await Eventually.TrueAsync(() => node.Get<NodeStore>().Read(c => c.NewServerIdentity) == replaced.FingerprintHex, "the new identity is noted");
        Assert.False(node.Get<ConnectionState>().Connected);
        await link.DisposeAsync();

        node.Get<BindingState>().AcceptNewServerIdentity();

        var accepted = await replaced.NextWorkingLinkAsync();
        Assert.Equal(NodeState.Bound, (await accepted.Client.HelloAsync(new HelloRequest())).State);
        Assert.Equal(replaced.FingerprintHex, node.Get<BindingState>().Bound!.Fingerprint);
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
