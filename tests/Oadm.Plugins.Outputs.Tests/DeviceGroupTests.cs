using Oadm.Tests.Shared;

using PluginCanary = Oadm.Sdk.Testing.PluginCanary;
using SdkTesting = Oadm.Sdk.Testing;

namespace Oadm.Plugins.Outputs.Tests;

/// <summary>"Set output" in rules of a device group (docs/spec/device-scope.md).</summary>
public sealed class DeviceGroupTests
{
    /// <summary>
    /// The rule engine picks the targets of a rule within the owner's devices; the action itself switches only the output
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
