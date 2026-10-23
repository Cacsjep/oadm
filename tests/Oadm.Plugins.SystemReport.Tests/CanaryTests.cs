using Oadm.Sdk.Plugins;

using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.SystemReport.Tests;

/// <summary>Device groups (docs/spec/device-scope.md): system reports stay with users who have all devices.</summary>
public sealed class CanaryTests : IDisposable
{
    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public async Task Every_method_is_for_users_with_all_devices_only()
    {
        var ctx = new Testing.FakeCoreContext { DataDirectory = _folder.Path };
        var inside = ctx.Add(new Testing.FakeDevice());
        var outside = ctx.Add(new Testing.FakeDevice());
        using var plugin = new SystemReportPlugin();
        await plugin.StartAsync(ctx, CancellationToken.None);
        await new Testing.PluginCanary(ctx, plugin, inside, outside)
            .AllDevicesOnly(SystemReportMethods.Start, $$"""{"deviceIds":["{{inside.Id}}"]}""")
            .AllDevicesOnly(SystemReportMethods.Status, """{"jobId":"x"}""")
            .AllDevicesOnly(SystemReportMethods.Read, """{"jobId":"x"}""")
            .AllDevicesOnly(SystemReportMethods.Delete, """{"jobId":"x"}""")
            .RunAsync();
        Assert.Empty(ctx.TaskRuns);
        Assert.False(ctx.ClientsCreated.ContainsKey(inside.Id));
    }

    [Fact]
    public async Task A_group_users_call_is_refused_by_the_plugin_too()
    {
        var ctx = new Testing.FakeCoreContext { DataDirectory = _folder.Path };
        var inside = ctx.Add(new Testing.FakeDevice());
        using var plugin = new SystemReportPlugin();
        await plugin.StartAsync(ctx, CancellationToken.None);
        await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => plugin.InvokeAsync(ctx.CallAsGroup([inside.Id]), SystemReportMethods.Start, "{}", CancellationToken.None));
    }
}
