using System.Net;
using System.Net.Security;
using System.Net.Sockets;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Sdk.Network;
using Oadm.Sdk.Testing;
using Oadm.Server.Sites;
using Oadm.SiteNode.Links;
using Oadm.Sites.Transport;
using Oadm.Tests.Shared;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Sites;

/// <summary>
/// The site port under a flood (docs/spec/sites.md, Server listener): one address never holds every handshake, strangers never
/// keep the sites' own nodes out, and closing the port waits for every handshake.
/// </summary>
public sealed class SiteListenerLimitTests
{
    [Fact]
    public async Task OneAddressGetsAFewHandshakesAtOnceAndClosingWaitsForThem()
    {
        using var identity = SiteTls.CreateIdentity("server");
        var handedOver = 0;
        var loopback = PrivateLoopback();
        var listener = new SiteListener(new IPEndPoint(loopback, 0).ToString(), () => identity, maxHandshakes: 64, _ => Interlocked.Increment(ref handedOver), NullLogger.Instance, maxHandshakesPerAddress: 2);
        listener.Start();
        var clients = new List<TcpClient>();
        try
        {
            // Connections that never start TLS: each holds a handshake until its timeout.
            for (var i = 0; i < 3; i++)
            {
                var client = new TcpClient();
                clients.Add(client);
                await client.ConnectAsync(loopback, listener.Port);
            }

            await Eventually.TrueAsync(() => listener.RefusedPerAddress == 1 && listener.HandshakesRunning == 2, "two handshakes, the third refused");

            await listener.DisposeAsync();

            Assert.Equal(0, listener.HandshakesRunning); // closing waited for them
            Assert.Equal(0, handedOver);
            foreach (var client in clients)
            {
                Assert.True(await ClosedByServerAsync(client), "every connection closed");
            }
        }
        finally
        {
            foreach (var client in clients)
            {
                client.Dispose();
            }

            await listener.DisposeAsync();
        }
    }

    [Fact]
    public async Task StrangersWaitingForHelloNeverKeepABoundNodeOut()
    {
        await using var server = await SiteServer.StartAsync(options: new SiteOptions
        {
            ListenUrl = "127.0.0.1:0",
            WorkerWait = TimeSpan.FromSeconds(30),
            CallTimeout = TimeSpan.FromSeconds(30),
            MaxPendingLinks = 1,
            HelloTimeout = TimeSpan.FromMinutes(5), // the stranger holds its place for the whole test
        });
        await using var node = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        var id = await server.BindAsync(node);

        using var stranger = await StrangerAsync(server.Manager.ListenPort);
        await Eventually.TrueAsync(() => server.Manager.PendingLinks == 1, "the stranger holds the only place");

        var started = server.Manager.SessionsStarted;
        node.Get<LinkRegistry>().CloseAll(); // the bound node connects again
        await Eventually.TrueAsync(() => server.Manager.SessionsStarted > started && server.Manager.HasSession(id), "the bound node connected");
        Assert.Equal(1, server.Manager.PendingLinks);
    }

    [Fact]
    public async Task OneAddressKeepsAtMostThreeNodesWaiting()
    {
        await using var server = await SiteServer.StartAsync();
        var nodes = new List<SiteTestNode>();
        try
        {
            for (var i = 0; i < 4; i++)
            {
                nodes.Add(await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address, hostName: "site-pc-" + i));
            }

            await Eventually.TrueAsync(async () => server.Manager.WaitingRefused >= 1 && Waiting(await server.ListAsync()) == 3, "three wait, the fourth refused");
        }
        finally
        {
            foreach (var node in nodes)
            {
                await node.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task AFullWaitingListDropsTheNodeWaitingLongest()
    {
        await using var server = await SiteServer.StartAsync(options: new SiteOptions
        {
            ListenUrl = "127.0.0.1:0",
            WorkerWait = TimeSpan.FromSeconds(30),
            CallTimeout = TimeSpan.FromSeconds(30),
            MaxWaiting = 2,
        });
        await using var first = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address, hostName: "site-pc-1");
        await server.WaitingAsync(first.NodeId);
        await using var second = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address, hostName: "site-pc-2");
        await server.WaitingAsync(second.NodeId);

        await using var third = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address, hostName: "site-pc-3");

        // The newest node always gets a place; the list never grows past its size.
        await server.WaitingAsync(third.NodeId);
        Assert.Equal(2, Waiting(await server.ListAsync()));
    }

    [Fact]
    public async Task TheSitePortsFirewallRuleIsOpenOnlyWhileSiteNodesAreAccepted()
    {
        var firewall = new RecordingFirewall();
        await using (var server = await SiteServer.StartAsync(accept: false, configure: s => s.AddSingleton<IFirewallRules>(firewall)))
        {
            Assert.Empty(firewall.CallList); // the port the OS chooses is not known while nothing listens

            await server.Client.SetAcceptAsync(new Proto.SetAcceptRequest { Accept = true });
            var rule = $"OADM Server (Site nodes, TCP {server.Manager.ListenPort})";
            Assert.Equal(["open " + rule], firewall.CallList);

            await server.Client.SetAcceptAsync(new Proto.SetAcceptRequest { Accept = false });
            Assert.Equal(["open " + rule, "close " + rule], firewall.CallList);

            await server.Client.SetAcceptAsync(new Proto.SetAcceptRequest { Accept = true });
            Assert.Equal($"open OADM Server (Site nodes, TCP {server.Manager.ListenPort})", firewall.CallList[^1]); // the OS chose another port
        }

        // The server stopped: the port is closed, so is its rule.
        Assert.StartsWith("close OADM Server (Site nodes, TCP ", firewall.CallList[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStartWithSiteNodesOffRemovesARuleLeftBehind()
    {
        var firewall = new RecordingFirewall();
        // A fixed port: nothing listens there while site nodes are off, the rule is still known by its name.
        await using var server = await SiteServer.StartAsync(accept: false, configure: s => s.AddSingleton<IFirewallRules>(firewall),
            options: new SiteOptions { ListenUrl = "127.0.0.1:5081", WorkerWait = TimeSpan.FromSeconds(30), CallTimeout = TimeSpan.FromSeconds(30) });

        Assert.Equal(["close OADM Server (Site nodes, TCP 5081)"], firewall.CallList);
        Assert.False(server.Manager.Accept);
    }

    // The site nodes of other tests keep dialing the 127.0.0.1 port their server had; the OS may hand that port to this
    // listener, and such a node would take a place of this address or even be handed over. IPv6 loopback is a port space
    // no site test uses; 127.0.0.1 only where the system has no IPv6 loopback.
    private static IPAddress PrivateLoopback()
    {
        try
        {
            using var probe = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            probe.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
            return IPAddress.IPv6Loopback;
        }
        catch (SocketException)
        {
            return IPAddress.Loopback;
        }
    }

    private static int Waiting(Proto.SiteList list) => list.Sites.Count(s => s.Id.Length == 0);

    // A TLS client with a key the server does not know that never answers Hello.
    private static async Task<SslStream> StrangerAsync(int port)
    {
        using var identity = SiteTls.CreateIdentity("stranger");
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
        await tls.AuthenticateAsClientAsync(SiteTls.ClientOptions(identity, _ => true));
        return tls;
    }

    private static async Task<bool> ClosedByServerAsync(TcpClient client)
    {
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            return await client.GetStream().ReadAsync(new byte[1], limit.Token) == 0;
        }
        catch (IOException)
        {
            return true; // reset by the server
        }
    }
}
