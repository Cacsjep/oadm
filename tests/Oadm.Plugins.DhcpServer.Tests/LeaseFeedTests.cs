using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.DhcpServer.Leases;
using Oadm.Plugins.DhcpServer.Serving;
using Oadm.Sdk.Network;

using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.DhcpServer.Tests;

/// <summary>The read-only view of the DHCP server for other plugins (<see cref="IDhcpServerLeases"/>, Provisioning).</summary>
public sealed class LeaseFeedTests
{
    private static readonly DhcpServerSummary Stopped = new(false, "Stopped", null, null, null, null, null);

    [Fact]
    public void Acknowledgements_come_in_order_with_their_numbers_and_a_reader_that_fell_behind_is_told()
    {
        var feed = new DhcpLeaseFeed(() => Stopped, new LeaseStore(), new FakeTimeProvider());
        Assert.Equal(0, feed.AcksSince(0, 10).Next);

        feed.OnAck(0xACCC8E010203, 0x0A00000A);
        feed.OnAck(0xB8A44F000001, 0x0A00000B);
        var first = feed.AcksSince(0, 10);
        Assert.Equal(["AC:CC:8E:01:02:03", "B8:A4:4F:00:00:01"], first.Acks.Select(a => a.Mac));
        Assert.Equal(["10.0.0.10", "10.0.0.11"], first.Acks.Select(a => a.Address));
        Assert.Equal(2, first.Next);
        Assert.False(first.Missed);
        Assert.Empty(feed.AcksSince(first.Next, 10).Acks);
        Assert.Single(feed.AcksSince(0, 1).Acks);

        for (var i = 0; i < DhcpLeaseFeed.Capacity + 5; i++)
        {
            feed.OnAck(0xACCC8E000000 + (ulong)i, 0x0A000100);
        }

        var behind = feed.AcksSince(first.Next, 10_000);
        Assert.True(behind.Missed);
        Assert.Equal(DhcpLeaseFeed.Capacity, behind.Acks.Count);
        Assert.Equal(feed.Last, behind.Next);
    }

    [Fact]
    public async Task The_plugin_offers_the_view_while_it_runs()
    {
        var ctx = new Testing.FakeCoreContext();
        var plugin = new DhcpServerPlugin(Options.Test(new FakeDhcpNetwork(), time: new FakeTimeProvider()));
        await plugin.StartAsync(ctx, CancellationToken.None);
        var view = ctx.FakeServices.Find<IDhcpServerLeases>();
        Assert.NotNull(view);
        Assert.False(view.Summary().IsRunning);
        Assert.Equal("Stopped", view.Summary().StatusText);

        await plugin.StopAsync(CancellationToken.None);
        Assert.Null(ctx.FakeServices.Find<IDhcpServerLeases>());
    }
}
