using System.Net;

using Microsoft.Extensions.Time.Testing;

using Oadm.Sdk.Testing;
using Oadm.Server.Sites;
using Oadm.SiteNode.Links;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Sites;

/// <summary>
/// The rules of the node sessions with the real OADM Site Node in this process: the waiting times on the monotonic clock,
/// no database write under the manager's lock, no session that outlives its site, the listener or the server.
/// </summary>
public sealed class SiteSessionRulesTests
{
    [Fact]
    public async Task ADeadNodeGoesOfflineAfterThirtySecondsAlsoWhenTheWallClockJumpsBack()
    {
        var clock = new JumpingClock(new FakeTimeProvider(DateTimeOffset.UtcNow));
        await using var server = await SiteServer.StartAsync(clock: clock);
        var node = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        var id = await server.BindAsync(node);
        await node.DisposeAsync();
        await Eventually.TrueAsync(() => !server.Manager.HasSession(id), "the node's connection gone");

        clock.Jump(TimeSpan.FromHours(-1)); // a time sync sets the server's clock back
        clock.Fake.Advance(TimeSpan.FromSeconds(25));
        Assert.True(server.Manager.IsOnline(id));
        clock.Fake.Advance(TimeSpan.FromSeconds(6));

        await Eventually.TrueAsync(() => !server.Manager.IsOnline(id), "the site offline 30 s after the last answer");
    }

    [Fact]
    public async Task ANodeThatIsConnectedButSilentGoesOfflineAfterThirtySeconds()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        // The control call's own timeout is far away: only the 30 s rule can end the session in time.
        await using var server = await SiteServer.StartAsync(options: new SiteOptions
        {
            ListenUrl = "127.0.0.1:0",
            Time = time,
            WorkerWait = TimeSpan.FromSeconds(30),
            CallTimeout = TimeSpan.FromMinutes(10),
        });
        await using var proxy = new PausableProxy(new IPEndPoint(IPAddress.Loopback, server.Manager.ListenPort));
        await using var node = await SiteTestNode.StartAsync(c => c.ServerAddress = proxy.Address);
        var id = await server.BindAsync(node);

        proxy.Pause(); // the link stays open, nothing gets through
        time.Advance(TimeSpan.FromSeconds(25));
        Assert.True(server.Manager.IsOnline(id));
        time.Advance(TimeSpan.FromSeconds(6));

        Assert.False(server.Manager.IsOnline(id)); // the tick of second 30 ended the session and set the site offline
        Assert.False(server.Manager.HasSession(id));
        await Eventually.TrueAsync(async () => (await server.AuditActionsAsync()).Contains(SiteAudit.Offline), "the offline edge audited");
    }

    [Fact]
    public async Task ANodeThatConnectsAgainWhileItsSiteIsRemovedGetsNoSession()
    {
        await using var server = await SiteServer.StartAsync();
        await using var node = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        var id = await server.BindAsync(node);
        var gate = PointGate.Hold(server.Manager, "forgetting");

        var remove = server.Client.RemoveSiteAsync(new Proto.RemoveSiteRequest { Id = id.ToString(), KeepDevices = true }).ResponseAsync;
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(30));
        // The node's connection drops while the site is being removed; it connects again at once, still bound.
        node.Get<LinkRegistry>().CloseAll();
        await Eventually.TrueAsync(async () => (await server.ListAsync()).Sites.Any(s => s.NodeId == node.NodeId && s.Id.Length == 0), "the node connected again");
        gate.Open();
        await remove;

        Assert.Empty(server.Manager.Sites);
        Assert.Equal(1, server.Manager.SessionsStarted);
        await Eventually.TrueAsync(() => server.Manager.LiveSessions == 0, "no session left");
    }

    [Fact]
    public async Task ALinkThatSaysHelloWhileSiteNodesAreTurnedOffBecomesNothing()
    {
        await using var server = await SiteServer.StartAsync();
        await using var node = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        var id = await server.BindAsync(node);
        var gate = PointGate.Hold(server.Manager, "after-hello");

        node.Get<LinkRegistry>().CloseAll(); // the node connects again
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(30));
        await server.Manager.SetAcceptAsync(false, CancellationToken.None);
        gate.Open();

        await Eventually.TrueAsync(() => node.Get<LinkRegistry>().Links.Count == 0, "the link closed");
        Assert.Equal(1, server.Manager.SessionsStarted);
        Assert.False(server.Manager.HasSession(id));
        await Eventually.TrueAsync(() => server.Manager.LiveSessions == 0, "no session left");
        Assert.DoesNotContain((await server.ListAsync()).Sites, s => s.Id.Length == 0);
    }

    [Fact]
    public async Task NoSessionStartsWhileTheServerStops()
    {
        await using var server = await SiteServer.StartAsync();
        await using var node = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        await server.BindAsync(node);
        var gate = PointGate.Hold(server.Manager, "after-hello");

        node.Get<LinkRegistry>().CloseAll();
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(30));
        await server.Manager.DisposeAsync(); // a restore rebuilds the host in this process
        gate.Open();

        await Eventually.TrueAsync(() => node.Get<LinkRegistry>().Links.Count == 0, "the link closed");
        Assert.Equal(1, server.Manager.SessionsStarted);
        Assert.Equal(0, server.Manager.LiveSessions);
    }

    [Fact]
    public async Task ABindWhoseSiteNodesAreTurnedOffMeanwhileClosesItsLink()
    {
        await using var server = await SiteServer.StartAsync();
        await using var node = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        var waiting = await server.WaitingAsync(node.NodeId);
        var gate = PointGate.Hold(server.Manager, "bound");

        var bind = server.Client.BindSiteAsync(new Proto.BindSiteRequest { NodeId = waiting.NodeId, NodeFingerprint = waiting.NodeFingerprint, Name = "Building B", PairingCode = node.Code }).ResponseAsync;
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(30));
        await server.Manager.SetAcceptAsync(false, CancellationToken.None);
        gate.Open();
        await bind;

        Assert.Single(server.Manager.Sites); // bound and stored: the node connects again once site nodes are turned on
        await Eventually.TrueAsync(() => node.Get<LinkRegistry>().Links.Count == 0, "the bind's link closed");
        Assert.Equal(0, server.Manager.LiveSessions);
    }

    [Fact]
    public async Task TheAuditOfAnOfflineSiteIsWrittenOutsideTheLock()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var gate = new DatabaseGate();
        await using var server = await SiteServer.StartAsync(time: time, configure: gate.Install);
        var node = await SiteTestNode.StartAsync(c => c.ServerAddress = server.Address);
        var id = await server.BindAsync(node);
        await node.DisposeAsync();
        await Eventually.TrueAsync(() => !server.Manager.HasSession(id), "the node's connection gone");

        gate.Hold(SiteAudit.Offline);
        var tick = Task.Run(() => time.Advance(TimeSpan.FromSeconds(31))); // the tick writes the offline edge
        try
        {
            await gate.Entered.WaitAsync(TimeSpan.FromSeconds(30));

            // The audit write is held (a slow disk): everyone else who asks the manager still gets an answer at once.
            var online = await Task.Run(() => server.Manager.IsOnline(id)).WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(online);
            Assert.Equal("Building B", await Task.Run(() => server.Manager.NameOf(id)).WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            gate.Open();
        }

        await tick;
        await Eventually.TrueAsync(async () => (await server.AuditActionsAsync()).Contains(SiteAudit.Offline), "the offline edge audited");
    }
}
