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
}
