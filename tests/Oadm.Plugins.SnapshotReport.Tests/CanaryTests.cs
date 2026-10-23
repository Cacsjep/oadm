using Oadm.Plugins.SnapshotReport.Report;

using PluginCanary = Oadm.Sdk.Testing.PluginCanary;
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
        await new PluginCanary(ctx, plugin, inside, outside)
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

    [Fact]
    public async Task A_device_the_report_cannot_reach_says_the_one_phrase()
    {
        var ctx = new Testing.FakeCoreContext();
        var camera = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.48" });
        using var service = new SnapshotService(ctx.Devices, ctx.Vapix);
        using var jobs = new ReportJobs(service);
        var gone = Guid.NewGuid(); // removed, or outside the user's devices: the same answer
        var request = new ReportRequest { Site = "Site", Items = [new ReportItem { DeviceId = camera.Id, Camera = 1 }, new ReportItem { DeviceId = gone, Camera = 1 }] };

        var entries = await jobs.CollectAsync(request, null, CancellationToken.None);

        var missing = entries[1];
        Assert.Equal(Oadm.Sdk.Devices.DeviceMessages.NoLongerAvailable(1), missing.Tile.Error);
        Assert.Equal(Oadm.Sdk.Devices.DeviceMessages.NoLongerAvailable(1), missing.Snapshot.Error);
        Assert.DoesNotContain("removed", missing.Tile.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(camera.Id, entries[0].Tile.Device.DeviceId);
    }

    [Fact]
    public async Task A_managed_device_without_video_says_so_in_the_report()
    {
        var ctx = new Testing.FakeCoreContext();
        var speaker = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.50", Category = Oadm.Sdk.Devices.DeviceCategory.Speaker });
        using var service = new SnapshotService(ctx.Devices, ctx.Vapix);
        using var jobs = new ReportJobs(service);
        var request = new ReportRequest { Site = "Site", Items = [new ReportItem { DeviceId = speaker.Id, Camera = 1 }] };

        var entry = Assert.Single(await jobs.CollectAsync(request, null, CancellationToken.None));

        Assert.Equal("The device has no video", entry.Tile.Error);
        Assert.Equal("The device has no video", entry.Snapshot.Error);
        Assert.Equal(speaker.Id, entry.Tile.Device.DeviceId);
        Assert.Equal("10.0.0.50", entry.Tile.Title);
        Assert.False(ctx.ClientsCreated.ContainsKey(speaker.Id));
    }
}
