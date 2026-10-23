using Oadm.Tests.Shared;

using PluginCanary = Oadm.Sdk.Testing.PluginCanary;
using SdkTesting = Oadm.Sdk.Testing;

namespace Oadm.Plugins.Users.Tests;

/// <summary>Users for users with a device group (docs/spec/device-scope.md).</summary>
public sealed class DeviceGroupTests
{
    /// <summary>
    /// The tasks start through the user's call only. The queries read only the queried device, which the host checks
    /// against the user's view; they take no device ids.
    /// </summary>
    [Fact]
    public async Task NoMethodReachesADeviceOutsideTheUsersGroup()
    {
        var ctx = new SdkTesting.FakeCoreContext();
        var inside = ctx.Add(new SdkTesting.FakeDevice());
        var outside = ctx.Add(new SdkTesting.FakeDevice { Address = "192.0.2.11" });
        var host = new TaskPluginCanaryHost([new UsersTaskPlugin()]);

        await new PluginCanary(ctx, host, inside, outside)
            .Method(TaskPluginCanaryHost.Run, $$"""{"pluginId":"{{UsersTaskPlugin.PluginId}}","deviceIds":["{inside}","{device}"]}""")
            .RunAsync();
    }
}
