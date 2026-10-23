using Microsoft.Extensions.Time.Testing;

using PluginCanary = Oadm.Sdk.Testing.PluginCanary;
using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.DhcpServer.Tests;

/// <summary>Device groups (docs/spec/device-scope.md): a server-wide service, changed by users with all devices, read by groups with their leases.</summary>
public sealed class CanaryTests
{
    [Fact]
    public async Task Every_change_is_for_users_with_all_devices_and_reading_shows_no_device_outside_the_group()
    {
        var ctx = new Testing.FakeCoreContext();
        var inside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.48" });
        var outside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.49", Serial = "ACCC8E000002" });
        await using var plugin = new DhcpServerPlugin(Options.Test(new FakeDhcpNetwork(), time: new FakeTimeProvider()));
        await plugin.StartAsync(ctx, CancellationToken.None);
        await new PluginCanary(ctx, plugin, inside, outside)
            .WithoutDevices(DhcpServerMethods.GetState, "server-wide state; the leases of the user's devices only")
            .AllDevicesOnly(DhcpServerMethods.Save, """{"enabled":false}""")
            .AllDevicesOnly(DhcpServerMethods.SaveStatic, """{"mac":"AC:CC:8E:00:00:02","address":"10.0.0.49"}""")
            .AllDevicesOnly(DhcpServerMethods.MakeStatic, """{"mac":"AC:CC:8E:00:00:02"}""")
            .AllDevicesOnly(DhcpServerMethods.DeleteStatic, """{"mac":"AC:CC:8E:00:00:02"}""")
            .AllDevicesOnly(DhcpServerMethods.Release, """{"mac":"AC:CC:8E:00:00:02"}""")
            .RunAsync();
    }

    [Fact]
    public async Task A_group_page_gets_the_leases_of_its_devices_only()
    {
        var time = new FakeTimeProvider();
        var ctx = new Testing.FakeCoreContext();
        var inside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.48" }); // serial B8A44F631339 = its MAC
        var byAddress = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.50", Serial = "ACCC8E000050" });
        ctx.Add(new Testing.FakeDevice { Address = "10.0.0.49", Serial = "ACCC8E000002" });
        await using var plugin = new DhcpServerPlugin(Options.Test(new FakeDhcpNetwork(), time: time));
        await plugin.StartAsync(ctx, CancellationToken.None);
        var key = ctx.FakeScopes.Add("Lobby", [inside.Id, byAddress.Id]);
        var group = Testing.FakeCallContext.WithGroup(ctx, [inside.Id, byAddress.Id], "guard", key);
        var admin = ctx.CallAsAllDevices();
        async Task SaveStatic(string mac, string address) =>
            await ctx.InvokeAsync(plugin, admin, DhcpServerMethods.SaveStatic, DhcpJson.Serialize(new StaticLeaseRequest(mac, address, null)));

        await SaveStatic("B8:A4:4F:63:13:39", "10.0.0.148");
        await SaveStatic("AC:CC:8E:00:00:02", "10.0.0.149");
        var state = DhcpJson.Deserialize<DhcpState>(await ctx.InvokeAsync(plugin, group, DhcpServerMethods.GetState, null));
        Assert.Equal(["B8:A4:4F:63:13:39"], state.Leases!.Select(l => l.Mac));
        Assert.Equal(2, DhcpJson.Deserialize<DhcpState>(await ctx.InvokeAsync(plugin, admin, DhcpServerMethods.GetState, null)).Leases!.Count);
        Assert.Empty(ctx.RefusedAccesses);

        // Live: a lease of the group's device (here matched by its address) reaches the group's page, the other one does not.
        await SaveStatic("00:40:8C:00:00:50", "10.0.0.50");
        await SaveStatic("AC:CC:8E:00:00:03", "10.0.0.151");
        await Testing.Eventually.TrueAsync(() =>
        {
            time.Advance(TimeSpan.FromMilliseconds(50));
            return ctx.EventsSeenBy(group).Any(e => e.Topic == DhcpServerMethods.LeasesTopic);
        }, "the leases reached the group's page");
        var mine = ctx.EventsSeenBy(group).Where(e => e.Topic == DhcpServerMethods.LeasesTopic)
            .SelectMany(e => DhcpJson.Deserialize<LeasesEvent>(e.PayloadJson).Changed).Select(l => l.Address).ToList();
        Assert.Contains("10.0.0.50", mine);
        Assert.All(mine, a => Assert.Contains(a, new[] { "10.0.0.148", "10.0.0.50" })); // the group's leases only
        Assert.Contains(ctx.EventsSeenBy(admin), e => e.Topic == DhcpServerMethods.LeasesTopic && e.PayloadJson!.Contains("10.0.0.151", StringComparison.Ordinal));
        Assert.Contains(ctx.EventsSeenBy(group), e => e.Topic == DhcpServerMethods.StateTopic);
    }
}
