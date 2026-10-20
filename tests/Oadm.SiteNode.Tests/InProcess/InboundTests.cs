using System.Net;

using Grpc.Core;

using Oadm.Contracts.SiteNode;
using Oadm.Sdk.Testing;
using Oadm.SiteNode.Admin;
using Oadm.SiteNode.Feed;
using Oadm.SiteNode.Links;
using Oadm.SiteNode.State;
using Oadm.SiteNode.Tests.Support;

namespace Oadm.SiteNode.Tests.InProcess;

/// <summary>Mode Wait ("Wait for the server"): the server opens TCP + TLS to the node's port; the same calls as outbound.</summary>
public sealed class InboundTests
{
    [Fact]
    public async Task TheServerConnectsBindsAndDials()
    {
        await using var echo = new EchoServer();
        await using var server = new TestServer();
        await using var node = await TestNode.StartAsync(c =>
        {
            c.Mode = NodeLinkMode.Wait;
            Allow.Connect(c, echo.Port);
        });
        var port = await node.Get<InboundListener>().Port;
        await using var link = await server.ConnectAsync(port);

        var hello = await link.Client.HelloAsync(new HelloRequest());
        Assert.Equal(NodeMode.Wait, hello.Mode);
        Assert.Equal(NodeState.UnboundOpen, hello.State);

        await link.BindAsync(node.Code, "Building B");
        Assert.Equal(NodeBinding.Bound, node.Get<BindingState>().Binding);
        var payload = new byte[64 * 1024];
        Random.Shared.NextBytes(payload);
        Assert.Equal(payload, await OutboundTests.DialEchoAsync(link.Client, "127.0.0.1", echo.Port, payload));
        await Eventually.TrueAsync(() => node.Get<ConnectionState>().Connected, "the app shows the server connected");
    }

    [Fact]
    public async Task ABoundNodeAnswersHelloToStrangersButNothingElse()
    {
        await using var server = new TestServer();
        await using var stranger = new TestServer();
        await using var node = await TestNode.StartAsync(c => c.Mode = NodeLinkMode.Wait);
        var port = await node.Get<InboundListener>().Port;
        await using var link = await server.ConnectAsync(port);
        await link.BindAsync(node.Code, "Building B", "OADM-SRV");

        await using var other = await stranger.ConnectAsync(port);
        var hello = await other.Client.HelloAsync(new HelloRequest());

        Assert.Equal(NodeState.Bound, hello.State);
        Assert.Empty(hello.BoundServerName); // strangers learn nothing about the owner or the node
        Assert.Empty(hello.SiteName);
        Assert.Empty(hello.NodeId);
        Assert.Empty(hello.HostName);
        Assert.Empty(hello.Features);
        Assert.Equal(NodeMode.Unspecified, hello.Mode);
        Assert.Equal(LinkRole.Unspecified, hello.LinkRole);
        Assert.Equal("3.7.0-test", hello.Version);
        Assert.Equal(1, hello.ProtocolVersion);
        var refused = await Assert.ThrowsAsync<RpcException>(async () => await other.Client.ResolveAsync(new ResolveRequest { Name = "x" }));
        Assert.Equal(AdminTexts.BoundToAnotherServer, refused.Status.Detail);
        var pairing = await Assert.ThrowsAsync<RpcException>(async () => await other.Client.PairStartAsync(new PairStartRequest()));
        Assert.Equal(AdminTexts.BoundToAnotherServer, pairing.Status.Detail);
        Assert.Equal("OADM-SRV", (await link.Client.HelloAsync(new HelloRequest())).BoundServerName);
    }

    [Fact]
    public async Task OneServerSessionAtATime()
    {
        // A restored copy of the server (same identity, another address) cannot drive the node next to the real one.
        await using var server = new TestServer();
        await using var copy = new TestServer(server.Identity);
        await using var node = await TestNode.StartAsync(c => c.Mode = NodeLinkMode.Wait);
        var port = await node.Get<InboundListener>().Port;
        await using var link = await server.ConnectAsync(port);
        await link.BindAsync(node.Code);

        await using var second = await copy.ConnectAsync(port, IPAddress.Parse("127.0.0.2"));
        var refused = await Assert.ThrowsAsync<RpcException>(async () => await second.Client.ResolveAsync(new ResolveRequest { Name = "x" }));

        Assert.Equal(StatusCode.FailedPrecondition, refused.StatusCode);
        Assert.Equal(AdminTexts.AnotherSession("127.0.0.1"), refused.Status.Detail);
        // A second connection of the same session (a worker from the same address) is fine.
        await using var worker = await server.ConnectAsync(port);
        Assert.Empty((await worker.Client.ResolveAsync(new ResolveRequest { Name = "x" })).Addresses);
    }

    [Fact]
    public async Task TheSessionIsCheckedOnEveryCallAgainstTheBindingOfThatMoment()
    {
        // The copy connected first, while the node was not bound; the real server binds on its own link afterwards: the
        // session that bound is the one, and the copy's earlier link is refused from then on, until that session ends.
        await using var server = new TestServer();
        await using var copy = new TestServer(server.Identity);
        await using var node = await TestNode.StartAsync(c => c.Mode = NodeLinkMode.Wait);
        var port = await node.Get<InboundListener>().Port;
        await using var early = await copy.ConnectAsync(port, IPAddress.Parse("127.0.0.2"));
        Assert.Equal(NodeState.UnboundOpen, (await early.Client.HelloAsync(new HelloRequest())).State);
        var link = await server.ConnectAsync(port);
        await link.BindAsync(node.Code);

        var refused = await Assert.ThrowsAsync<RpcException>(async () => await early.Client.ResolveAsync(new ResolveRequest { Name = "x" }));
        Assert.Equal(AdminTexts.AnotherSession("127.0.0.1"), refused.Status.Detail);
        Assert.Empty((await early.Client.HelloAsync(new HelloRequest())).BoundServerName);
        Assert.Empty((await link.Client.ResolveAsync(new ResolveRequest { Name = "x" })).Addresses);

        await link.DisposeAsync();
        await Eventually.TrueAsync(() => node.Get<LinkRegistry>().Links.Count == 1, "the real session ended");
        Assert.Empty((await early.Client.ResolveAsync(new ResolveRequest { Name = "x" })).Addresses);
    }

    [Fact]
    public async Task UnboundLinksAreCappedPerAddress()
    {
        await using var stranger = new TestServer();
        await using var node = await TestNode.StartAsync(c => c.Mode = NodeLinkMode.Wait);
        var port = await node.Get<InboundListener>().Port;
        var links = new List<ServerLink>();
        try
        {
            for (var i = 0; i < InboundListener.MaxUnboundPerAddress; i++)
            {
                var link = await stranger.ConnectAsync(port);
                links.Add(link);
                await link.Client.HelloAsync(new HelloRequest());
            }

            await Eventually.TrueAsync(() => node.Get<InboundListener>().UnboundLinks == InboundListener.MaxUnboundPerAddress, "the links are counted");
            var extra = await stranger.ConnectAsync(port);
            links.Add(extra);
            await Assert.ThrowsAsync<RpcException>(async () => await extra.Client.HelloAsync(new HelloRequest(), deadline: DateTime.UtcNow.AddSeconds(30)));

            // Another address still gets in.
            var other = await stranger.ConnectAsync(port, IPAddress.Parse("127.0.0.3"));
            links.Add(other);
            Assert.Equal(NodeState.UnboundOpen, (await other.Client.HelloAsync(new HelloRequest())).State);
        }
        finally
        {
            foreach (var link in links)
                await link.DisposeAsync();
        }

        await Eventually.TrueAsync(() => node.Get<InboundListener>().UnboundLinks == 0, "closed links free their places");
    }

    [Fact]
    public async Task AnUnboundLinkWithoutCallsIsClosed()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var stranger = new TestServer();
        await using var node = await TestNode.StartAsync(c => c.Mode = NodeLinkMode.Wait, o => o.Time = time);
        var port = await node.Get<InboundListener>().Port;
        await using var link = await stranger.ConnectAsync(port);
        await link.Client.HelloAsync(new HelloRequest());
        await Eventually.TrueAsync(() => node.Get<InboundListener>().UnboundLinks == 1, "the link is watched");

        await Eventually.TrueAsync(() =>
        {
            time.Advance(TimeSpan.FromSeconds(5));
            return node.Get<LinkRegistry>().Links.Count == 0;
        }, "the idle link is closed");
        Assert.Equal(0, node.Get<InboundListener>().UnboundLinks);
    }

    [Fact]
    public async Task ClosedBindingRefusesBindWithThePlainText()
    {
        await using var server = new TestServer();
        await using var node = await TestNode.StartAsync(c =>
        {
            c.Mode = NodeLinkMode.Wait;
            c.BindingSecondsLeft = 0;
        });
        var port = await node.Get<InboundListener>().Port;
        await using var link = await server.ConnectAsync(port);

        Assert.Equal(NodeState.UnboundClosed, (await link.Client.HelloAsync(new HelloRequest())).State);
        var error = await Assert.ThrowsAsync<RpcException>(() => link.BindAsync(node.Code));
        Assert.Equal(StatusCode.FailedPrecondition, error.StatusCode);
        Assert.Equal(AdminTexts.BindingClosed, error.Status.Detail);
    }

    [Fact]
    public async Task TurningWaitOffClosesTheListenerAndTheLinks()
    {
        await using var server = new TestServer();
        await using var node = await TestNode.StartAsync(c => c.Mode = NodeLinkMode.Wait);
        var port = await node.Get<InboundListener>().Port;
        await using var link = await server.ConnectAsync(port);
        await link.Client.HelloAsync(new HelloRequest());

        node.Get<Config.NodeStore>().Update(c => c.Mode = NodeLinkMode.Connect);

        await Eventually.TrueAsync(() => node.Get<InboundListener>().ActivePort == 0, "the node stops listening");
        await Eventually.TrueAsync(() => node.Get<LinkRegistry>().Links.Count == 0, "the server's link is closed");
    }
}
