using Oadm.Plugins.LoadTest.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.LoadTest.Tests.Client;

/// <summary>The page with the server's paging, All devices, the 1,000-camera limit, the run permission and the stream note.</summary>
public sealed class PageServerAdditionsTests
{
    private static FakePageContext Context(int cameras = 3)
    {
        var ctx = new FakePageContext();
        for (var i = 0; i < cameras; i++)
        {
            ctx.DeviceList.Add(new TestDevice($"10.0.0.{i + 10}", "P3265-LVE"));
        }

        ctx.Answers[LoadTestMethods.ListRuns] = _ => LoadTestJson.Serialize(new RunList([], null));
        ctx.Answers[LoadTestMethods.Plan] = _ => LoadTestJson.Serialize(Samples.Plan());
        return ctx;
    }

    [Fact]
    public async Task All_devices_sends_no_ids()
    {
        var ctx = Context();
        using var vm = new LoadTestViewModel(ctx);
        vm.Activate();

        vm.DeviceChoice.Set(DeviceChoice.AllDevices);
        await Eventually.TrueAsync(() => vm.Plan is not null && !vm.IsPlanLoading, "the plan");

        var request = LoadTestJson.Deserialize<PlanRequest>(ctx.LastPayloadOf(LoadTestMethods.Plan));
        Assert.True(request.AllDevices);
        Assert.Empty(request.DeviceIds);

        ctx.Answers[LoadTestMethods.Start] = _ => LoadTestJson.Serialize(Samples.Running(0));
        await vm.StartCommand.ExecuteAsync(null);
        var start = LoadTestJson.Deserialize<StartRequest>(ctx.LastPayloadOf(LoadTestMethods.Start));
        Assert.True(start.AllDevices);
        Assert.Empty(start.DeviceIds);
    }

    [Fact]
    public async Task A_plan_over_1000_cameras_shows_the_refusal_and_keeps_Start_disabled()
    {
        var ctx = Context();
        ctx.Answers[LoadTestMethods.Plan] = _ => LoadTestJson.Serialize(new CheckPlan(30_000, 0, TimeSpan.Zero, 0, 0, [], LoadTestInfo.TooManyStreams));
        using var vm = new LoadTestViewModel(ctx);
        vm.Activate();

        vm.DeviceChoice.Set(DeviceChoice.AllDevices);
        await Eventually.TrueAsync(() => vm.Plan is not null && !vm.IsPlanLoading, "the plan");

        Assert.Equal("A check takes at most 1,000 cameras. Choose fewer.", vm.ErrorText);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.Equal(LoadTestInfo.TooManyStreams, vm.StartTip);
    }

    [Fact]
    public void The_run_permission_is_the_plugins_run_key()
    {
        var ctx = Context();
        ctx.Denied.Add(PermissionKeysOf("run"));
        using var vm = new LoadTestViewModel(ctx);

        Assert.False(vm.MayRun);
        Assert.Equal("oadm.load-test:" + LoadTestInfo.RunPermission, CheckTexts.RunPermission);
    }

    [Fact]
    public async Task A_run_opens_page_by_page_into_the_table()
    {
        var ctx = Context();
        var rows = Samples.Rows().Take(12).ToList();
        var requests = new List<RunRequest>();
        ctx.Answers[LoadTestMethods.GetRun] = payload =>
        {
            var request = LoadTestJson.Deserialize<RunRequest>(payload);
            requests.Add(request);
            var page = rows.Skip(request.Skip).Take(5).ToList(); // the server's page size here: 5
            return LoadTestJson.Serialize(new RunDetail(Samples.Completed(), request.Skip == 0 ? Samples.Graphs(30) : new RunGraphs([], [], [], [], [], []), page, rows.Count));
        };
        using var vm = new LoadTestViewModel(ctx);

        await vm.OpenRunAsync(Samples.RunId, CancellationToken.None);

        Assert.Equal([0, 5, 10], requests.Select(r => r.Skip));
        Assert.Equal(12, vm.Rows.Count);
        Assert.Equal(rows.Skip(5).Select(r => r.StreamId), vm.Rows.Skip(5).Select(r => r.StreamId));
        Assert.False(vm.IsRunLoading);
    }

    [Fact]
    public void The_note_shows_as_secondary_text_in_the_table_and_the_panel()
    {
        var note = "UDP is blocked between the camera and the server, packet loss not measured";
        var row = Samples.Rows().First(r => r.Problem is null) with { Transport = "TCP", PacketLossPercent = null, Note = note };

        var vm = new StreamRowViewModel(row);
        var detail = new StreamDetailViewModel(row);

        Assert.Equal(note, vm.ProblemText);
        Assert.False(vm.HasProblem);
        Assert.Equal(note, detail.NoteText);
        Assert.True(detail.HasNote);
        Assert.False(detail.HasProblem);
    }

    private static string PermissionKeysOf(string key) => Oadm.Sdk.Plugins.PermissionKeys.Of(LoadTestInfo.PluginId, key);
}
