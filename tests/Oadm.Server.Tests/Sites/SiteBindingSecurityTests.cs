using Grpc.Core;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Oadm.Sdk.Testing;
using Oadm.Server.Sites;
using Oadm.SiteNode.Admin;
using Oadm.SiteNode.Links;
using Oadm.SiteNode.State;

using Proto = Oadm.Contracts.V1;

using SiteTexts = Oadm.Contracts.Sites.SiteTexts;

namespace Oadm.Server.Tests.Sites;

/// <summary>
/// What strangers at the site port can and cannot do (docs/spec/sites.md, Server side: Limits before a site is bound, Binding), with the
/// real OADM Site Node in this process.
/// </summary>
public sealed class SiteBindingSecurityTests
{
    [Fact]
    public async Task AStrangerReportingAnotherNodesIdGetsItsOwnRowAndNeverTheBind()
    {
        await using var server = await SiteServer.StartAsync();
        await using var node = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        var real = await server.WaitingAsync(node.NodeId);
        await using var impostor = await SiteTestNode.StartAsync(c =>
        {
            c.ServerAddress = server.Address;
            c.Id = node.NodeId;
        }, hostName: "site-pc");

        // Both wait, each under its own key: the impostor did not take the real node's place.
        await Eventually.TrueAsync(async () => (await server.ListAsync()).Sites.Count(s => s.NodeId == node.NodeId) == 2, "both nodes wait");
        var rows = (await server.ListAsync()).Sites.Where(s => s.NodeId == node.NodeId).ToList();
        Assert.Contains(rows, r => r.NodeFingerprint == real.NodeFingerprint);
        Assert.Equal(2, rows.Select(r => r.NodeFingerprint).Distinct().Count());

        // By id alone the server cannot tell them apart and refuses; by key it binds the one the administrator chose.
        var ambiguous = await SiteAsserts.RefusedAsync(StatusCode.FailedPrecondition, async () =>
            await server.Client.BindSiteAsync(new Proto.BindSiteRequest { NodeId = node.NodeId, Name = "Building B", PairingCode = node.Code }));
        Assert.Equal(SiteTexts.NodeIdAmbiguous, ambiguous.Status.Detail);
        var site = await server.Client.BindSiteAsync(new Proto.BindSiteRequest
        {
            NodeId = node.NodeId,
            NodeFingerprint = real.NodeFingerprint,
            Name = "Building B",
            PairingCode = node.Code,
        });

        Assert.Equal(real.NodeFingerprint, site.NodeFingerprint);
        Assert.Equal(NodeBinding.Bound, node.Get<BindingState>().Binding);
        Assert.NotEqual(NodeBinding.Bound, impostor.Get<BindingState>().Binding);
    }

    [Fact]
    public async Task TwoBindsAtOnceNeverGetTheSameName()
    {
        await using var server = await SiteServer.StartAsync();
        await using var first = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        await using var second = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address, hostName: "site-pc-2");
        var a = await server.WaitingAsync(first.NodeId);
        var b = await server.WaitingAsync(second.NodeId);

        async Task<StatusCode> BindAsync(Proto.Site row, string code)
        {
            try
            {
                await server.Client.BindSiteAsync(new Proto.BindSiteRequest { NodeId = row.NodeId, NodeFingerprint = row.NodeFingerprint, Name = "Building B", PairingCode = code });
                return StatusCode.OK;
            }
            catch (RpcException e)
            {
                Assert.True(e.StatusCode == StatusCode.AlreadyExists, $"{e.StatusCode}: {e.Status.Detail}");
                return e.StatusCode;
            }
        }

        var results = await Task.WhenAll(BindAsync(a, first.Code), BindAsync(b, second.Code));

        Assert.Single(results, StatusCode.OK);
        Assert.Single(results, StatusCode.AlreadyExists);
        Assert.Single(server.Manager.Sites);
    }

    [Fact]
    public async Task ASiteThatCannotBeStoredAfterTheNodeBoundIsUndoneOnTheNode()
    {
        await using var server = await SiteServer.StartAsync();
        await using var node = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        var row = await server.WaitingAsync(node.NodeId);

        // A row the server does not hold in memory takes the name in the database: the insert fails after the node bound.
        var factory = server.Host.Get<Microsoft.EntityFrameworkCore.IDbContextFactory<Oadm.Core.Persistence.OadmDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Sites.Add(new Oadm.Core.Sites.Site { Name = "Building B", NormalizedName = "BUILDING B", NodeId = "ffffffffffffffff", NodeKeyFingerprint = "00", BoundUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var error = await SiteAsserts.RefusedAsync(StatusCode.Internal, async () =>
            await server.Client.BindSiteAsync(new Proto.BindSiteRequest { NodeId = row.NodeId, NodeFingerprint = row.NodeFingerprint, Name = "Building B", PairingCode = node.Code }));

        Assert.Equal(SiteTexts.NotStored, error.Status.Detail);
        Assert.Empty(server.Manager.Sites);
        await Eventually.TrueAsync(() => node.Get<BindingState>().Binding == NodeBinding.Open, "the node was sent Unbind");
    }

    [Fact]
    public async Task NewConnectionsFromOneAddressAreCountedBeforeTls()
    {
        await using var server = await SiteServer.StartAsync(configure: s =>
        {
            s.RemoveAll<SiteOptions>();
            s.AddSingleton(new SiteOptions { ListenUrl = "127.0.0.1:0", ConnectionsPerMinute = 2, CallTimeout = TimeSpan.FromSeconds(30) });
        });
        var port = server.Manager.ListenPort;
        using var first = new System.Net.Sockets.TcpClient();
        using var second = new System.Net.Sockets.TcpClient();
        using var third = new System.Net.Sockets.TcpClient();
        await first.ConnectAsync(System.Net.IPAddress.Loopback, port);
        await second.ConnectAsync(System.Net.IPAddress.Loopback, port);
        await third.ConnectAsync(System.Net.IPAddress.Loopback, port);

        // The third is closed at once, without a TLS handshake: it reads the end of the stream.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var buffer = new byte[1];
        int read;
        try
        {
            read = await third.GetStream().ReadAsync(buffer, timeout.Token);
        }
        catch (IOException)
        {
            read = 0; // a reset is a close too
        }

        Assert.Equal(0, read);
        Assert.False(timeout.IsCancellationRequested);
    }

    [Fact]
    public async Task AHelloOutsideTheRulesIsRefusedAndNotListed()
    {
        await using var server = await SiteServer.StartAsync();
        await using var node = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address, hostName: "site‮pc");

        // The server closes the link after the Hello (the node then says it cannot reach the server). Not checked through the
        // server's log file: with several hosts in one test process the log lines may land in another host's file.
        await Eventually.TrueAsync(() => node.Get<SiteNode.Feed.ConnectionState>().Text == SiteNode.Admin.AdminTexts.CannotReach(server.Address), "the server refuses the Hello");
        Assert.DoesNotContain((await server.ListAsync()).Sites, s => s.NodeId == node.NodeId);
    }

    [Fact]
    public async Task ANodeThatAcceptedTheServersNewIdentityBindsItsSiteAgainWithTheCode()
    {
        await using var server = await SiteServer.StartAsync();
        await using var node = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        var id = await server.BindAsync(node, "Building B");

        // The server's site identity is lost (as with a replaced master key): the node refuses it and asks.
        await server.Host.Get<SiteIdentityStore>().RotateAsync("OADM-SRV", CancellationToken.None);
        var replaced = server.Host.Get<SiteIdentityStore>().FingerprintHex;
        node.Get<LinkRegistry>().CloseAll();
        await Eventually.TrueAsync(() => node.Get<SiteNode.Config.NodeStore>().Read(c => c.NewServerIdentity) == replaced, "the node notes the new identity");
        Assert.DoesNotContain((await server.ListAsync()).Sites, s => s.State == Proto.SiteState.Waiting);

        var code = node.Get<BindingState>().AcceptNewServerIdentity();

        // The node offers to pair with the new identity; the server lists it to bind the site again, nothing more.
        var row = await server.WaitingAsync(node.NodeId);
        Assert.Equal(SiteTexts.BindAgain("Building B"), row.Message);
        Assert.NotEqual(replaced, node.Get<BindingState>().Bound!.Fingerprint);
        var wrong = code[0] == 'A' ? "B" + code[1..] : "A" + code[1..];
        await SiteAsserts.RefusedAsync(StatusCode.InvalidArgument, async () =>
            await server.Client.BindSiteAsync(new Proto.BindSiteRequest { NodeId = row.NodeId, NodeFingerprint = row.NodeFingerprint, Name = "Building B", PairingCode = wrong }));

        var site = await server.Client.BindSiteAsync(new Proto.BindSiteRequest { NodeId = row.NodeId, NodeFingerprint = row.NodeFingerprint, Name = "Building B", PairingCode = node.Code });

        Assert.Equal(id.ToString(), site.Id);
        Assert.Single(server.Manager.Sites);
        Assert.Equal(replaced, node.Get<BindingState>().Bound!.Fingerprint);
        await Eventually.TrueAsync(() => server.Manager.IsUsable(id), "the site is online again");
    }
}
