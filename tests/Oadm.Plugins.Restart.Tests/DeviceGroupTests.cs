using Oadm.Tests.Shared;

using PluginCanary = Oadm.Sdk.Testing.PluginCanary;
using SdkTesting = Oadm.Sdk.Testing;

namespace Oadm.Plugins.Restart.Tests;

/// <summary>Restart for users with a device group (docs/spec/device-scope.md). The task's own tests are in Oadm.Core.Tests.</summary>
public sealed class DeviceGroupTests
{
    /// <summary>The task has no dialog and no query: it starts only through the user's call and restarts only its own device.</summary>
    [Fact]
    public async Task NoMethodReachesADeviceOutsideTheUsersGroup()
    {
        var ctx = new SdkTesting.FakeCoreContext();
        var inside = ctx.Add(new SdkTesting.FakeDevice());
        var outside = ctx.Add(new SdkTesting.FakeDevice { Address = "192.0.2.11" });
        var host = new TaskPluginCanaryHost([new RestartTaskPlugin()]);

        await new PluginCanary(ctx, host, inside, outside)
            .Method(TaskPluginCanaryHost.Run, $$"""{"pluginId":"{{RestartTaskPlugin.PluginId}}","deviceIds":["{inside}","{device}"]}""")
            .RunAsync();
        Assert.All(ctx.TaskRuns, r => Assert.DoesNotContain(outside.Id, r.DeviceIds));
        Assert.Single(ctx.TaskRuns); // only the run inside the group started
    }
}
