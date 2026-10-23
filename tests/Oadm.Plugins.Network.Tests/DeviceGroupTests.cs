using Oadm.Plugins.Network.Model;

using SdkTesting = Oadm.Sdk.Testing;

namespace Oadm.Plugins.Network.Tests;

/// <summary>
/// The address check of Assign IP address for users with a device group (docs/spec/device-scope.md). The tasks start
/// through the user's call, which the host scopes (server <c>CanaryTests</c>, case TaskService/Run).
/// </summary>
public sealed class DeviceGroupTests
{
    [Fact]
    public async Task The_address_check_of_a_group_user_says_only_in_use_for_a_device_outside_the_group()
    {
        var ctx = new SdkTesting.FakeCoreContext();
        var inside = ctx.Add(new SdkTesting.FakeDevice { Address = "10.0.0.48", Serial = "ACCC8E000048" });
        ctx.Add(new SdkTesting.FakeDevice { Address = "10.0.0.49", Serial = "ACCC8E000049" });
        ctx.Add(new SdkTesting.FakeDevice { Address = "10.0.0.50", Serial = "ACCC8E000050", SiteId = Guid.NewGuid() }); // another network
        var plugin = new AssignIpTaskPlugin(ReachabilityOptions.Default, TimeProvider.System, new FakeAddressProbe());
        var request = new AddressCheckRequest(["10.0.0.48", "10.0.0.49", "10.0.0.50", "10.0.0.51"]).ToJson();

        var group = AddressCheckResponse.Parse(await ctx.QueryAsync(plugin, ctx.CallAsGroup([inside.Id]), inside, AddressCheck.QueryMethod, request));

        var managed = Assert.Single(group.Managed);
        Assert.Equal(inside.Id, managed.DeviceId);
        var hidden = Assert.Single(group.Probed, p => p.Address == "10.0.0.49");
        Assert.True(hidden.InUse);
        Assert.True(hidden.UsedByDevice);
        Assert.Null(hidden.DeviceId);
        Assert.Null(hidden.Device);
        Assert.Equal("In use", hidden.InUseText);
        Assert.False(group.Probed.Single(p => p.Address == "10.0.0.50").InUse); // another site's network
        Assert.False(group.Probed.Single(p => p.Address == "10.0.0.48").UsedByDevice); // the user's own device is named instead
        Assert.False(group.Probed.Single(p => p.Address == "10.0.0.51").InUse);
        Assert.Empty(ctx.RefusedAccesses);
        var row = new AssignmentRow(new AssignmentDevice(inside.Id, "10.0.0.48"), "10.0.0.49");
        var known = group.Managed.Concat(group.Probed).GroupBy(s => s.Address).ToDictionary(g => g.Key, g => g.Last());
        Assert.Equal("In use", AddressConflicts.Find([row], 24, null, known)[0]);

        // A user with all devices: unchanged, the device is named.
        var all = AddressCheckResponse.Parse(await ctx.QueryAsync(plugin, ctx.CallAsAllDevices(), inside, AddressCheck.QueryMethod, request));
        Assert.Contains(all.Managed, m => m.Address == "10.0.0.49" && m.Device == "P3265-V ACCC8E000049");
        Assert.DoesNotContain(all.Probed, p => p.UsedByDevice);
    }

    [Fact]
    public async Task The_address_check_probes_only_networks_the_users_group_names()
    {
        var ctx = new SdkTesting.FakeCoreContext();
        var site = Guid.NewGuid();
        var local = ctx.Add(new SdkTesting.FakeDevice { Address = "10.0.0.48" }); // on the server's network, in the group by a tag
        var remote = ctx.Add(new SdkTesting.FakeDevice { Address = "192.168.1.48", SiteId = site });
        var probe = new FakeAddressProbe();
        probe.Answers["10.0.0.60"] = new AddressProbeResult(AnswersPing: true, AnswersTcp: false);
        var plugin = new AssignIpTaskPlugin(ReachabilityOptions.Default, TimeProvider.System, probe);
        var request = new AddressCheckRequest(["10.0.0.60", "10.0.0.61"]).ToJson();

        // A group without "This server": nothing on the server's network is probed; the managed devices still count.
        var group = ctx.CallAsGroup([local.Id, remote.Id]);
        group.Sites = new HashSet<Guid> { site };
        var answer = AddressCheckResponse.Parse(await ctx.QueryAsync(plugin, group, local, AddressCheck.QueryMethod, request));
        Assert.Empty(probe.Probed);
        Assert.All(answer.Probed, p => Assert.False(p.InUse));
        Assert.Single(answer.Managed, m => m.DeviceId == local.Id);

        // The group's own site is probed, and so is the server's network once the group names "This server".
        await ctx.QueryAsync(plugin, group, remote, AddressCheck.QueryMethod, new AddressCheckRequest(["192.168.1.60"]).ToJson());
        Assert.Equal(["192.168.1.60"], probe.Probed);
        group.ThisServer = true;
        answer = AddressCheckResponse.Parse(await ctx.QueryAsync(plugin, group, local, AddressCheck.QueryMethod, request));
        Assert.True(answer.Probed.Single(p => p.Address == "10.0.0.60").InUse);

        // A user with all devices probes every route.
        probe.Probed.Clear();
        await ctx.QueryAsync(plugin, ctx.CallAsAllDevices(), local, AddressCheck.QueryMethod, request);
        Assert.Equal(2, probe.Probed.Count);
    }
}
