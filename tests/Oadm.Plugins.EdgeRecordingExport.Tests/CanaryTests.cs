using PluginCanary = Oadm.Sdk.Testing.PluginCanary;
using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.EdgeRecordingExport.Tests;

/// <summary>Device groups (docs/spec/device-scope.md): search and export reach only cameras of the user's view.</summary>
public sealed class CanaryTests
{
    [Fact]
    public async Task No_method_reaches_a_camera_outside_the_users_group()
    {
        var ctx = new Testing.FakeCoreContext();
        var inside = ctx.Add(new Testing.FakeDevice());
        var outside = ctx.Add(new Testing.FakeDevice());
        var plugin = new EdgeRecordingExportPlugin();
        await plugin.StartAsync(ctx, CancellationToken.None);
        await new PluginCanary(ctx, plugin, inside, outside)
            .Method(EdgeRecordingMethods.Search, """{"deviceId":"{device}"}""")
            .Download(EdgeRecordingMethods.Export, """{"deviceId":"{device}","recordingId":"20261023_101500_1A2B_B8A44F631339","diskId":"SD_DISK"}""")
            .RunAsync();
    }
}
