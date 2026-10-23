using Oadm.Plugins.Network.Model;
using Oadm.Tests.Shared;

using PluginCanary = Oadm.Sdk.Testing.PluginCanary;
using SdkTesting = Oadm.Sdk.Testing;

namespace Oadm.Plugins.Network.Tests;

/// <summary>Network settings and Assign IP address for users with a device group (docs/spec/device-scope.md).</summary>
public sealed class DeviceGroupTests
{
    [Fact]
    public async Task NoMethodReachesADeviceOutsideTheUsersGroup()
    {
        var ctx = new SdkTesting.FakeCoreContext();
        var inside = ctx.Add(new SdkTesting.FakeDevice { Address = "10.0.0.48" });
        var outside = ctx.Add(new SdkTesting.FakeDevice { Address = "10.0.0.49" });
        var settings = new NetworkSettingsTaskPlugin(ReachabilityOptions.Default, TimeProvider.System, new FakeAddressProbe());
        var assign = new AssignIpTaskPlugin(ReachabilityOptions.Default, TimeProvider.System, new FakeAddressProbe());
        var host = new TaskPluginCanaryHost([settings, assign]);

        // The tasks start through the user's call; the queries read only the queried device (the host checks it) and take
        // no device ids: their group cases are below.
        await new PluginCanary(ctx, host, inside, outside)
            .Method(TaskPluginCanaryHost.Run, $$"""{"pluginId":"{{settings.Id}}","deviceIds":["{inside}","{device}"]}""")
            .Method(TaskPluginCanaryHost.Run, $$"""{"pluginId":"{{assign.Id}}","deviceIds":["{inside}","{device}"]}""")
            .RunAsync();
    }

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
}
