using System.Net;
using System.Net.Sockets;

using Grpc.Core;

using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Contracts.SiteNode;
using Oadm.Core.Sites;
using Oadm.Sdk.Testing;
using Oadm.Server.Sites;
using Oadm.Sites.Binding;
using Oadm.Sites.Transport;

namespace Oadm.Server.Tests.Sites;

/// <summary>
/// A bound site's node that keeps no rules (docs/spec/sites.md, Server side: Limits on a bound site's node): its answers
/// are read up to 1 MB, every Hello and status is checked, and one that breaks the rules ends the session with nothing
/// kept. Uploads and downloads through a site are streams of small chunks and pass as before.
/// </summary>
public sealed class SiteNodeLimitTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ATrustedLinkReadsAtMostOneMegabyteInOneAnswer()
    {
        using var identity = SiteTls.CreateIdentity("server");
        var loopback = SiteListenerLimitTests.PrivateLoopback();
        var accepted = new TaskCompletionSource<NodeLink>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var listener = new SiteListener(new IPEndPoint(loopback, 0).ToString(), () => identity, maxHandshakes: 8, link => accepted.TrySetResult(link), NullLogger.Instance,
            handshakeTimeout: Generous);
        listener.Start();
        await using var node = await RogueNode.StartAsync(SiteTls.CreateIdentity("node"));
        var size = 64 * 1024;
        node.Answers.HelloOf = _ => new HelloReply { HostName = new string('a', Volatile.Read(ref size)) };
        await node.ConnectAsync(loopback, listener.Port);
        await using var link = await accepted.Task.WaitAsync(Generous);

        // A stranger: a few KB.
        Assert.Equal(StatusCode.ResourceExhausted, (await Assert.ThrowsAsync<RpcException>(async () => await HelloAsync(link))).StatusCode);

        // A bound site's node: far more than the largest chunk it sends (64 KB), and no more than 1 MB.
        link.Trust();
        Volatile.Write(ref size, 512 * 1024);
        Assert.Equal(512 * 1024, (await HelloAsync(link)).HostName.Length);
        Volatile.Write(ref size, 2 * 1024 * 1024);
        Assert.Equal(StatusCode.ResourceExhausted, (await Assert.ThrowsAsync<RpcException>(async () => await HelloAsync(link))).StatusCode);
        Assert.Equal(1024 * 1024, NodeLink.TrustedReceiveBytes);
    }

    [Fact]
    public async Task LargeTransfersThroughASitePassInChunksBothWays()
    {
        // What an AXIS OS upload, a download or live video through a site is: megabytes over one Dial, with the real node.
        using var device = new TcpListener(IPAddress.Loopback, 0);
        device.Start();
        var echoing = EchoAsync(device);
        await using var server = await SiteServer.StartAsync();
        await using var node = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        node.Map("192.168.0.20", 80, (IPEndPoint)device.LocalEndpoint);
        var id = await server.BindAsync(node);
        var payload = new byte[8 * 1024 * 1024];
        Random.Shared.NextBytes(payload);
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        var back = new byte[payload.Length];
        await using (var stream = await server.Manager.OpenStreamAsync(id, "192.168.0.20", 80, SiteDialKind.Permanent, Generous, limit.Token))
        {
            var writing = Task.Run(async () => await stream.WriteAsync(payload, limit.Token), limit.Token);
            await stream.ReadExactlyAsync(back, limit.Token);
            await writing;
        }

        Assert.True(payload.AsSpan().SequenceEqual(back), "every byte came back");
        await echoing.WaitAsync(Generous); // the node closed the device's connection with the dial
    }

    [Fact]
    public async Task AHeartbeatHelloOutsideTheRulesEndsTheSessionAndIsNotKept()
    {
        await using var server = await SiteServer.StartAsync(options: new SiteOptions
        {
            ListenUrl = "127.0.0.1:0",
            WorkerWait = Generous,
            CallTimeout = Generous,
            HandshakeTimeout = Generous,
            HeartbeatInterval = TimeSpan.FromMilliseconds(50), // the next Hello comes at once
        });
        var (id, rogue) = await TakeOverAsync(server);
        await using (rogue)
        {
            // The Hello of the new link keeps the rules; the heartbeat's version is longer than a version may be.
            var tooLong = new string('9', HelloRules.MaxVersion + 1);
            rogue.Answers.HelloOf = n => RogueAnswers.Bound(n == 1 ? "3.7.0-test" : tooLong);
            var started = server.Manager.SessionsStarted;

            await rogue.ConnectAsync(IPAddress.Loopback, server.Manager.ListenPort);

            await Eventually.TrueAsync(() => server.Manager.AnswersRefused >= 1, "the heartbeat's Hello refused");
            await Eventually.TrueAsync(() => server.Manager.LiveSessions == 0, "its session ended");
            Assert.True(server.Manager.SessionsStarted > started, "the link was the site's session first");
            var site = Assert.Single((await server.ListAsync()).Sites, s => s.Id == id.ToString());
            Assert.Equal("3.7.0-test", site.Version);
        }
    }

    [Fact]
    public async Task AStatusBeyondTheLimitsEndsTheSessionAndIsNeverKept()
    {
        await using var server = await SiteServer.StartAsync();
        var (id, rogue) = await TakeOverAsync(server);
        await using (rogue)
        {
            var status = new NodeStatus { State = NodeState.Bound };
            status.Allowed.Add(Enumerable.Range(1, NodeStatusRules.MaxAllowed + 1).Select(port => new PortRule { Direction = PortDirection.Connect, Protocol = Protocol.Tcp, Port = port }));
            rogue.Answers.Statuses = [status];
            await Eventually.TrueAsync(() => server.Manager.LiveSessions == 0, "the real node's session ended");

            await rogue.ConnectAsync(IPAddress.Loopback, server.Manager.ListenPort);

            await Eventually.TrueAsync(() => server.Manager.AnswersRefused >= 1, "the status refused");
            await Eventually.TrueAsync(() => server.Manager.LiveSessions == 0, "its session ended");
            Assert.False(server.Manager.HasSession(id));
        }
    }

    /// <summary>A bound site whose node is gone, and a rogue node with that site's key (not connected yet).</summary>
    private static async Task<(Guid Id, RogueNode Rogue)> TakeOverAsync(SiteServer server)
    {
        var node = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        Guid id;
        try
        {
            id = await server.BindAsync(node);
        }
        catch
        {
            await node.DisposeAsync();
            throw;
        }

        return (id, await RogueNode.StartAsync(await RogueNode.KeyOfAsync(node)));
    }

    private static async Task<HelloReply> HelloAsync(NodeLink link) =>
        await link.Client.HelloAsync(new HelloRequest(), deadline: DateTime.UtcNow + Generous);

    /// <summary>Sends everything the one connection of <paramref name="device"/> receives back, until it ends.</summary>
    private static async Task EchoAsync(TcpListener device)
    {
        try
        {
            using var client = await device.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read));
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            // The node closed the connection, or the test stopped the device.
        }
    }
}
