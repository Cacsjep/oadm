using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.AppManagement.Reading;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.AppManagement.Tests;

/// <summary>Device groups (docs/spec/device-scope.md): every method with a device outside the group answers like an unknown id.</summary>
public sealed class CanaryTests
{
    [Fact]
    public async Task Every_method_keeps_to_the_callers_group()
    {
        var ctx = new FakeCoreContext();
        var app = Samples.App("demo", "Demo");
        var inside = ctx.Add(Samples.ManagedDevice("10.0.0.1"), Samples.Camera(app));
        var outside = ctx.Add(Samples.ManagedDevice("10.0.0.2"), Samples.Camera(app));
        await using var plugin = new AppManagementPlugin(new AppManagementOptions { Time = new FakeTimeProvider(), EventInterval = TimeSpan.FromHours(1) });
        await plugin.StartAsync(ctx, CancellationToken.None);

        await new PluginCanary(ctx, plugin, inside, outside)
            .Method(AppManagementMethods.Read, """{"deviceIds":["{inside}","{device}"]}""")
            .Method(AppManagementMethods.Run, """{"action":"stop","items":[{"application":"demo","deviceIds":["{inside}","{device}"]}]}""")
            .Method(AppManagementMethods.Follow, $$"""{"taskIds":["{{Guid.NewGuid()}}"],"deviceIds":["{inside}","{device}"]}""")
            .Method(AppManagementMethods.GetLog, """{"deviceId":"{device}","application":"demo"}""")
            .Method(AppManagementMethods.GetSettings, """{"application":"demo","deviceIds":["{inside}","{device}"]}""")
            .WithoutDevices(AppManagementMethods.GetState, "the progress of the caller's own session")
            .WithoutDevices(AppManagementMethods.GetRows, "the rows of the caller's own session, pruned to the caller's devices", """{"skip":0}""")
            .RunAsync();
        Assert.Empty(ctx.Cameras[outside.Id].Requests);
    }
}
