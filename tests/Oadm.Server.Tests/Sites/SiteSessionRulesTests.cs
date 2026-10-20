using System.Net;

using Microsoft.Extensions.Time.Testing;

using Oadm.Sdk.Testing;
using Oadm.Server.Sites;

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
