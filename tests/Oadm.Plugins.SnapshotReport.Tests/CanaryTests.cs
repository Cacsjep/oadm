using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.SnapshotReport.Tests;

/// <summary>Device groups (docs/spec/device-scope.md): only devices of the user's view, reports owned by their user.</summary>
public sealed class CanaryTests
{
    private static string JobId(string? reply) => SnapshotReportJson.Deserialize<ReportJobStatus>(reply).JobId;

    [Fact]
    public async Task No_method_reaches_a_device_outside_the_users_group()
    {
        var ctx = new Testing.FakeCoreContext();
        var inside = ctx.Add(new Testing.FakeDevice());
        var outside = ctx.Add(new Testing.FakeDevice());
        using var plugin = new SnapshotReportPlugin();
        await plugin.StartAsync(ctx, CancellationToken.None);
        var report = $$"""{"site":"Site","items":[{"deviceId":"{{inside.Id}}","camera":1}]}""";
        Task<string> OwnersReport(Testing.FakeCallContext owner) => ctx.InvokeAsync(plugin, owner, SnapshotReportMethods.GenerateReport, report).ContinueWith(t => JobId(t.Result), TaskScheduler.Default);
        await new Testing.PluginCanary(ctx, plugin, inside, outside)
            .Method(SnapshotReportMethods.ListSources, """{"deviceIds":["{inside}","{device}"]}""")
            .Method(SnapshotReportMethods.Snapshot, """{"deviceId":"{device}","camera":1}""")
            .Method(SnapshotReportMethods.GenerateReport, """{"site":"Site","items":[{"deviceId":"{inside}","camera":1},{"deviceId":"{device}","camera":1}]}""")
            .Job(SnapshotReportMethods.ReportStatus, """{"jobId":"{job}"}""", OwnersReport)
            .Job(SnapshotReportMethods.ReadReport, """{"jobId":"{job}","offset":0}""", OwnersReport)
            .Job(SnapshotReportMethods.DeleteReport, """{"jobId":"{job}"}""", OwnersReport)
            .RunAsync();
    }

    [Fact]
    public async Task All_cameras_means_the_cameras_of_the_users_view()
    {
        var ctx = new Testing.FakeCoreContext();
        var inside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.48" });
        ctx.Add(new Testing.FakeDevice { Address = "10.0.0.49" });
        using var plugin = new SnapshotReportPlugin();
        await plugin.StartAsync(ctx, CancellationToken.None);
        var group = ctx.CallAsGroup([inside.Id]);

        var tiles = SnapshotReportJson.Deserialize<ListSourcesResult>(await ctx.InvokeAsync(plugin, group, SnapshotReportMethods.ListSources, "{}")).Tiles;

        Assert.Equal([inside.Id], tiles.Select(t => t.Device.DeviceId).Distinct());
        Assert.Empty(ctx.RefusedAccesses);
        Assert.False(ctx.ClientsCreated.ContainsKey(ctx.DeviceList[1].Id));
    }
}
