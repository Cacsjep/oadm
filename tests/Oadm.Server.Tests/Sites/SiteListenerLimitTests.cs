using System.Net;
using System.Net.Security;
using System.Net.Sockets;

using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Sdk.Testing;
using Oadm.Server.Sites;
using Oadm.SiteNode.Links;
using Oadm.Sites.Transport;

namespace Oadm.Server.Tests.Sites;

/// <summary>
/// The site port under a flood (docs/sites.md, Server listener): one address never holds every handshake, strangers never
/// keep the sites' own nodes out, and closing the port waits for every handshake.
/// </summary>
public sealed class SiteListenerLimitTests
{
    [Fact]
    public async Task OneAddressGetsAFewHandshakesAtOnceAndClosingWaitsForThem()
    {
        using var identity = SiteTls.CreateIdentity("server");
        var handedOver = 0;
        var listener = new SiteListener("127.0.0.1:0", () => identity, maxHandshakes: 64, _ => Interlocked.Increment(ref handedOver), NullLogger.Instance, maxHandshakesPerAddress: 2);
        listener.Start();
        var clients = new List<TcpClient>();
        try
        {
            // Connections that never start TLS: each holds a handshake until its timeout.
            for (var i = 0; i < 3; i++)
            {
                var client = new TcpClient();
                clients.Add(client);
                await client.ConnectAsync(IPAddress.Loopback, listener.Port);
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
