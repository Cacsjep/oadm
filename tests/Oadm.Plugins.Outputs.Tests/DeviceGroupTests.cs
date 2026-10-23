using Oadm.Tests.Shared;

using PluginCanary = Oadm.Sdk.Testing.PluginCanary;
using SdkTesting = Oadm.Sdk.Testing;

namespace Oadm.Plugins.Outputs.Tests;

/// <summary>"Set output" and device groups (docs/spec/device-scope.md).</summary>
public sealed class DeviceGroupTests
{
    /// <summary>
    /// Event rules are for users with all devices; the action depends on no device group and switches only the output
    /// of the device it is given, through that device's client.
    /// </summary>
    [Fact]
    public async Task NoMethodReachesADeviceOutsideTheUsersGroup()
    {
        var ctx = new SdkTesting.FakeCoreContext();
        var inside = ctx.Add(new SdkTesting.FakeDevice());
        var outside = ctx.Add(new SdkTesting.FakeDevice { Address = "192.0.2.11" });
        var host = new TaskPluginCanaryHost([], [new SetOutputAction()]);

        await new PluginCanary(ctx, host, inside, outside)
            .Method(TaskPluginCanaryHost.Action, $$$"""{"actionId":"{{{SetOutputAction.ActionId}}}","deviceId":"{device}","settings":{"port":1,"mode":"active"}}""")
            .RunAsync();
        Assert.Empty(ctx.Cameras[outside.Id].Requests);
        Assert.NotEmpty(ctx.Cameras[inside.Id].Requests);
    }
}
