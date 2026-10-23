using Oadm.Sdk.Plugins;

using PluginCanary = Oadm.Sdk.Testing.PluginCanary;
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
        await new PluginCanary(ctx, plugin, inside, outside)
            .AllDevicesOnly(SystemReportMethods.Start, $$"""{"deviceIds":["{{inside.Id}}"]}""")
            .AllDevicesOnly(SystemReportMethods.Status, """{"jobId":"x"}""")
            .AllDevicesOnly(SystemReportMethods.Read, """{"jobId":"x"}""")
            .AllDevicesOnly(SystemReportMethods.Delete, """{"jobId":"x"}""")
            .RunAsync();
        Assert.Empty(ctx.TaskRuns);
        Assert.False(ctx.ClientsCreated.ContainsKey(inside.Id));
    }

    [Fact]
    public async Task A_report_belongs_to_the_user_who_started_it()
    {
        var ctx = new Testing.FakeCoreContext { DataDirectory = _folder.Path };
        var device = ctx.Add(new Testing.FakeDevice());
        using var plugin = new SystemReportPlugin();
        await plugin.StartAsync(ctx, CancellationToken.None);
        var anna = ctx.CallAsAllDevices("anna");
        var ben = ctx.CallAsAllDevices("ben");

        var started = SystemReportJson.Deserialize<JobStatus>(await ctx.InvokeAsync(plugin, anna, SystemReportMethods.Start, $$"""{"deviceIds":["{{device.Id}}"]}"""));
        var owned = Assert.Single(ctx.FakeJobs.All);
        Assert.Equal(owned.Id, started.JobId);
        Assert.Equal(started.JobId, SystemReportJson.Deserialize<JobStatus>(await ctx.InvokeAsync(plugin, anna, SystemReportMethods.Status, $$"""{"jobId":"{{started.JobId}}"}""")).JobId);

        // Another user's call answers like an unknown id, and its delete changes nothing.
        var status = await Assert.ThrowsAsync<KeyNotFoundException>(() => ctx.InvokeAsync(plugin, ben, SystemReportMethods.Status, $$"""{"jobId":"{{started.JobId}}"}"""));
        var unknown = await Assert.ThrowsAsync<KeyNotFoundException>(() => ctx.InvokeAsync(plugin, ben, SystemReportMethods.Status, """{"jobId":"0123456789abcdef0123456789abcdef"}"""));
        Assert.Equal(unknown.Message, status.Message);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => ctx.InvokeAsync(plugin, ben, SystemReportMethods.Read, $$"""{"jobId":"{{started.JobId}}","offset":0}"""));
        await ctx.InvokeAsync(plugin, ben, SystemReportMethods.Delete, $$"""{"jobId":"{{started.JobId}}"}""");
        Assert.Equal(1, plugin.Jobs!.JobCount);

        // The host ends the job (the session ended): the report and its files go.
        owned.Cancel();
        await owned.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, plugin.Jobs.JobCount);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => ctx.InvokeAsync(plugin, anna, SystemReportMethods.Status, $$"""{"jobId":"{{started.JobId}}"}"""));
    }

    [Fact]
    public async Task The_owners_delete_ends_the_job()
    {
        var ctx = new Testing.FakeCoreContext { DataDirectory = _folder.Path };
        var device = ctx.Add(new Testing.FakeDevice());
        using var plugin = new SystemReportPlugin();
        await plugin.StartAsync(ctx, CancellationToken.None);
        var anna = ctx.CallAsAllDevices("anna");

        var started = SystemReportJson.Deserialize<JobStatus>(await ctx.InvokeAsync(plugin, anna, SystemReportMethods.Start, $$"""{"deviceIds":["{{device.Id}}"]}"""));
        await ctx.InvokeAsync(plugin, anna, SystemReportMethods.Delete, $$"""{"jobId":"{{started.JobId}}"}""");
        await Assert.Single(ctx.FakeJobs.All).Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, plugin.Jobs!.JobCount);
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
