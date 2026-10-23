using Oadm.Plugins.DeviceConfig.Configure;
using Oadm.Tests.Shared;

using PluginCanary = Oadm.Sdk.Testing.PluginCanary;
using SdkTesting = Oadm.Sdk.Testing;

namespace Oadm.Plugins.DeviceConfig.Tests;

/// <summary>Export, import and configure for users with a device group (docs/spec/device-scope.md).</summary>
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
        var host = new TaskPluginCanaryHost([new ExportConfigTask(), new ImportConfigTask(), new ConfigureDevicesTask(), new CreateConfigFileTask()]);

        await new PluginCanary(ctx, host, inside, outside)
            .Method(TaskPluginCanaryHost.Run, $$"""{"pluginId":"{{DeviceConfigIds.Import}}","deviceIds":["{inside}","{device}"]}""")
            .Method(TaskPluginCanaryHost.Run, $$"""{"pluginId":"{{ConfigureIds.Configure}}","deviceIds":["{inside}","{device}"]}""")
            .RunAsync();
    }
}
