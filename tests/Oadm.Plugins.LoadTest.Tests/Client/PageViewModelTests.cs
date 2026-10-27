using System.Diagnostics;

using Oadm.Plugins.LoadTest.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Testing;

using Xunit.Abstractions;

namespace Oadm.Plugins.LoadTest.Tests.Client;

public sealed class PageViewModelTests
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
    public async Task Nothing_is_read_before_a_choice_and_the_plan_loads_after_one()
    {
        var ctx = Context();
        using var vm = new LoadTestViewModel(ctx);
        vm.Activate();
        await Eventually.TrueAsync(() => ctx.CallsOf(LoadTestMethods.ListRuns) == 1, "the run list read");
        Assert.True(vm.ShowChooseFirst);
        Assert.Equal(0, ctx.CallsOf(LoadTestMethods.Plan));
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.Equal("Choose cameras first", vm.StartTip);

        vm.DeviceChoice.Set(DeviceChoice.Devices([ctx.DeviceList[0].Id, ctx.DeviceList[2].Id]));
        await Eventually.TrueAsync(() => vm.Plan is not null && !vm.IsPlanLoading, "the plan");
        var request = LoadTestJson.Deserialize<PlanRequest>(ctx.LastPayloadOf(LoadTestMethods.Plan));
        Assert.Equal([ctx.DeviceList[0].Id, ctx.DeviceList[2].Id], request.DeviceIds);
        Assert.Equal((LoadTestInfo.DefaultBatchSize, LoadTestInfo.DefaultPhaseMinutes), (request.BatchSize, request.PhaseMinutes));
        Assert.True(vm.ShowPlan);
        Assert.Equal(2, vm.LeftOut.Count);
        Assert.Equal("150 camera streams in 3 batches of at most 50, 2 min per phase.", vm.PlanText);
        Assert.Equal("Camera streams", vm.Card1.Title);
        Assert.True(vm.StartCommand.CanExecute(null));

        vm.SelectedDuration = LoadTestViewModel.Durations.First(d => d.Minutes == 5);
        await Eventually.TrueAsync(() => ctx.CallsOf(LoadTestMethods.Plan) == 2 && vm.Plan is not null, "the plan again");
        Assert.Equal(5, LoadTestJson.Deserialize<PlanRequest>(ctx.LastPayloadOf(LoadTestMethods.Plan)).PhaseMinutes);
    }

    [Theory]
    [InlineData("50", null)]
    [InlineData("1", null)]
    [InlineData("200", null)]
    [InlineData("0", "Enter 1 to 200.")]
    [InlineData("201", "Enter 1 to 200.")]
    [InlineData("", "Enter 1 to 200.")]
    [InlineData("ten", "Enter 1 to 200.")]
    public void The_batch_size_is_1_to_200(string text, string? error)
    {
        var ctx = Context();
        using var vm = new LoadTestViewModel(ctx);
        Assert.Null(vm.ErrorOf(nameof(vm.BatchSizeText))); // untouched form: no error shown
        vm.BatchSizeText = text;
        Assert.Equal(error, vm.ErrorOf(nameof(vm.BatchSizeText)));
        Assert.Equal(error is null, vm.IsFormValid);
    }

    [Fact]
    public async Task An_invalid_batch_size_reads_no_plan_and_blocks_Start_with_the_reason()
    {
        var ctx = Context();
        using var vm = new LoadTestViewModel(ctx);
        vm.DeviceChoice.Set(DeviceChoice.AllDevices);
        vm.Activate();
        await Eventually.TrueAsync(() => vm.Plan is not null, "the plan");
        var plans = ctx.CallsOf(LoadTestMethods.Plan);

        vm.BatchSizeText = "300";
        Assert.Null(vm.Plan);
        Assert.Equal(plans, ctx.CallsOf(LoadTestMethods.Plan));
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.Equal("Enter 1 to 200.", vm.StartTip);
    }

    [Fact]
    public async Task Start_asks_first_with_counts_time_rate_and_the_exposure_warning()
    {
        var ctx = Context();
        ctx.Answers[LoadTestMethods.Start] = _ => LoadTestJson.Serialize(Samples.Running(0));
        using var vm = new LoadTestViewModel(ctx);
        vm.DeviceChoice.Set(DeviceChoice.AllDevices);
        vm.Activate();
        await Eventually.TrueAsync(() => vm.StartCommand.CanExecute(null), "Start ready");

        ctx.ConfirmAnswer = false;
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(0, ctx.CallsOf(LoadTestMethods.Start)); // not confirmed: nothing streams
        var (title, message, confirm) = Assert.Single(ctx.Confirmations);
        Assert.Equal(("Start load test", "Start"), (title, confirm));
        Assert.Contains("150 camera streams in 3 batches, about 32 min.", message, StringComparison.Ordinal);
        Assert.Contains("Expected peak rate 215 Mbit/s, 318 Mbit/s in low light.", message, StringComparison.Ordinal);
        Assert.Contains("The exposure of these cameras changes for 6 minutes during the check. Recordings of your VMS see the brighter, noisier image.", message, StringComparison.Ordinal);

        ctx.ConfirmAnswer = true;
        await vm.StartCommand.ExecuteAsync(null);
        var request = LoadTestJson.Deserialize<StartRequest>(ctx.LastPayloadOf(LoadTestMethods.Start));
        Assert.Empty(request.DeviceIds); // All devices: the server takes the caller's video devices
        Assert.True(request.AllDevices);
        Assert.Equal((50, 2), (request.BatchSize, request.PhaseMinutes));
        Assert.True(vm.IsRunning);
        Assert.False(vm.CanEditInputs);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.True(vm.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_refused_Start_shows_the_servers_reason()
    {
        var ctx = Context();
        ctx.Answers[LoadTestMethods.Start] = _ => throw new InvalidOperationException("A check by tom@site-pc is running (40 %).");
        using var vm = new LoadTestViewModel(ctx);
        vm.DeviceChoice.Set(DeviceChoice.AllDevices);
        vm.Activate();
        await Eventually.TrueAsync(() => vm.StartCommand.CanExecute(null), "Start ready");
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal("A check by tom@site-pc is running (40 %).", vm.ErrorText);
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public void Live_events_append_graph_points_and_update_rows_in_place()
    {
        var ctx = Context();
        using var vm = new LoadTestViewModel(ctx);
        var rows = Samples.Rows(atSecond: 10);
        vm.ApplyLive(new LiveEvent(Samples.Running(10), 0, Samples.Graphs(10), rows));
        Assert.True(vm.IsRunning);
        Assert.Equal(150, vm.Rows.Count);
        Assert.Equal(10, vm.RateSeries[0].Values.Count);
        var first = vm.RateSeries;
        var row = vm.Rows.First(r => r.StreamId == rows[3].StreamId);

        var resets = 0;
        vm.Rows.CollectionChanged += (_, _) => resets++;
        var next = Samples.Graphs(11);
        var point = new RunGraphs([next.Phases[10]], [next.TotalMbps[10]], [next.AverageLossPercent[10]], [next.WorstLossPercent[10]], [next.AverageJitterMs[10]], []);
        vm.ApplyLive(new LiveEvent(Samples.Running(11), 10, point, [rows[3] with { AverageMbps = 7.5 }]));
        Assert.Equal(11, vm.RateSeries[0].Values.Count);
        Assert.NotSame(first, vm.RateSeries); // a new list: the chart redraws
        Assert.Same(row, vm.Rows.First(r => r.StreamId == rows[3].StreamId)); // updated in place
        Assert.Equal("7.5 Mbit/s", row.MbpsText);
        Assert.Equal(0, resets); // the order did not change: no reset
        Assert.Equal("Batch 1 of 3 · Ramp 25 % · 30 min left", vm.ProgressText);
    }

    [Fact]
    public void A_gap_in_the_live_points_reads_the_whole_run_once()
    {
        var ctx = Context();
        ctx.Answers[LoadTestMethods.GetRun] = _ => LoadTestJson.Serialize(Samples.RunningDetail(40));
        using var vm = new LoadTestViewModel(ctx);
        vm.ApplyLive(new LiveEvent(Samples.Running(30), 30, Samples.Graphs(1), []));
        Assert.Equal(1, ctx.CallsOf(LoadTestMethods.GetRun));
        Assert.Equal(40, vm.RateSeries[0].Values.Count);
    }

    [Fact]
    public void The_table_is_sorted_worst_first_and_resorts_with_one_reset()
    {
        var ctx = Context();
        using var vm = new LoadTestViewModel(ctx);
        vm.ShowDetail(Samples.CompletedDetail());
        Assert.Equal(PhaseResult.Interrupted, vm.Rows[0].Normal.Result); // interrupted first
        Assert.True(vm.Rows.Take(8).All(r => r.Normal.IsError || r.LowLight.IsError));
        Assert.False(vm.Rows[8].LowLight.IsError || vm.Rows[8].Normal.IsError);
        var site = vm.Rows.Single(r => r.Row.Transport == "TCP");
        Assert.Equal(CheckTexts.NotMeasured, site.PacketLossText);
        Assert.Equal(CheckTexts.SitesNoPacketLoss, site.ProblemText);

        var resets = 0;
        vm.Rows.CollectionChanged += (_, e) => resets++;
        var last = vm.Rows[^1].Row;
        var phases = last.Phases.Select(p => p.Phase == CheckPhase.Normal ? p with { Result = PhaseResult.Failed } : p).ToList();
        vm.ApplyRows([last with { Phases = phases, Problem = "Frame loss 2.1 % in Normal" }]);
        Assert.Equal(1, resets);
        Assert.Equal(last.StreamId, vm.Rows.Take(9).Last().StreamId); // now among the failed
    }

    [Fact]
    public void The_result_shows_the_verdict_cards_and_the_restore_warning()
    {
        var ctx = Context();
        using var vm = new LoadTestViewModel(ctx);
        vm.ShowDetail(Samples.CompletedDetail());
        Assert.True(vm.IsResult);
        Assert.Equal(("Cameras checked", "150", "8 failed", false), (vm.Card1.Title, vm.Card1.Value, vm.Card1.Detail, vm.Card1.IsError));
        Assert.Equal("331 Mbit/s", vm.Card2.Value);
        Assert.True(vm.Card3.IsError);
        Assert.Equal(("Headroom", "75 %", true), (vm.Card4.Title, vm.Card4.Value, vm.Card4.IsWarning));
        Assert.Single(vm.RestoreWarnings);
        Assert.Equal(5, vm.StepBars.Count);
        Assert.Equal(ChartColor.Error, vm.StepBars[^1].Color); // Low light failed
        Assert.Equal(ChartColor.Ok, vm.StepBars[0].Color);
        Assert.Equal(ChartColor.Error, vm.StepBars[3].Color); // Normal: one camera failed
        Assert.Equal(15, vm.PhaseBands.Count); // five phases per batch
        Assert.Equal(new ChartBand(0, 120, "25 %"), vm.PhaseBands[0]);
    }

    [Fact]
    public async Task Selecting_a_row_reads_that_streams_graphs()
    {
        var ctx = Context();
        ctx.Answers[LoadTestMethods.GetStream] = _ => LoadTestJson.Serialize(Samples.Stream(Samples.Row(57)));
        using var vm = new LoadTestViewModel(ctx);
        vm.ShowDetail(Samples.CompletedDetail());
        vm.SelectedRow = vm.Rows.First(r => r.StreamId == Samples.Row(57).StreamId);
        await Eventually.TrueAsync(() => vm.Detail is { IsLoading: false }, "the stream");
        var request = LoadTestJson.Deserialize<StreamRequest>(ctx.LastPayloadOf(LoadTestMethods.GetStream));
        Assert.Equal((Samples.RunId, Samples.Row(57).StreamId), (request.RunId, request.StreamId));
        Assert.Equal(Samples.BatchSeconds, vm.Detail!.RateSeries[0].Values.Count);
        Assert.Equal(5, vm.Detail.PhaseBands.Count);
        vm.CloseDetailCommand.Execute(null);
        Assert.Null(vm.Detail);
    }

    [Fact]
    public async Task A_viewer_sees_results_but_cannot_start_cancel_or_delete()
    {
        var ctx = Context();
        ctx.Denied.Add(CheckTexts.RunPermission);
        using var vm = new LoadTestViewModel(ctx);
        vm.DeviceChoice.Set(DeviceChoice.AllDevices);
        vm.Activate();
        await Eventually.TrueAsync(() => vm.Plan is not null && !vm.IsPlanLoading, "the plan");
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.Equal(CheckTexts.NotAllowed, vm.StartTip);

        vm.ApplyLive(new LiveEvent(Samples.Running(5), 0, Samples.Graphs(5), Samples.Rows(5)));
        Assert.True(vm.IsRunning);
        Assert.False(vm.CancelCommand.CanExecute(null));
        Assert.Equal(CheckTexts.NotAllowed, vm.CancelTip);

        var runs = vm.RunsViewModel;
        runs.Apply(Samples.RunList());
        runs.Selected = runs.Runs[1];
        Assert.False(runs.DeleteCommand.CanExecute(null));
        Assert.Equal(CheckTexts.DeleteNotAllowed, runs.DeleteTip);
        Assert.True(runs.OpenCommand.CanExecute(null));

        ctx.Denied.Clear();
        ctx.RaisePermissionsChanged();
        Assert.True(vm.CancelCommand.CanExecute(null));
        Assert.True(runs.DeleteCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_run_list_opens_a_run_on_the_page_and_deletes_after_asking()
    {
        var ctx = Context();
        var list = Samples.RunList();
        ctx.Answers[LoadTestMethods.ListRuns] = _ => LoadTestJson.Serialize(list);
        ctx.Answers[LoadTestMethods.GetRun] = _ => LoadTestJson.Serialize(Samples.CompletedDetail());
        ctx.Answers[LoadTestMethods.DeleteRun] = _ => "{}";
        using var vm = new LoadTestViewModel(ctx);
        await vm.ShowRunsCommand.ExecuteAsync(null);
        var runs = vm.RunsViewModel;
        Assert.Equal(3, runs.Runs.Count);
        Assert.True(runs.Runs[0].StartedUtc > runs.Runs[1].StartedUtc); // newest first

        var closed = 0;
        runs.Close = () => closed++;
        runs.Selected = runs.Runs[0];
        await runs.OpenCommand.ExecuteAsync(null);
        Assert.Equal(1, closed);
        Assert.Equal(Samples.RunId, LoadTestJson.Deserialize<RunRequest>(ctx.LastPayloadOf(LoadTestMethods.GetRun)).RunId);
        Assert.True(vm.IsResult);
        Assert.Equal(150, vm.Rows.Count);

        ctx.ConfirmAnswer = false;
        runs.Selected = runs.Runs[0];
        await runs.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(0, ctx.CallsOf(LoadTestMethods.DeleteRun));
        Assert.Equal(("Delete run", "Delete"), (ctx.Confirmations[^1].Title, ctx.Confirmations[^1].Confirm));

        ctx.ConfirmAnswer = true;
        await runs.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(Samples.RunId, LoadTestJson.Deserialize<RunRequest>(ctx.LastPayloadOf(LoadTestMethods.DeleteRun)).RunId);
        Assert.Equal(2, runs.Runs.Count);
        Assert.False(vm.ShowRun); // the deleted run left the page
    }

    [Fact]
    public void The_running_run_cannot_be_deleted()
    {
        var ctx = Context();
        using var vm = new LoadTestViewModel(ctx);
        var runs = vm.RunsViewModel;
        runs.Apply(new RunList([Samples.Running(10)], Samples.RunId));
        runs.Selected = runs.Runs[0];
        Assert.False(runs.DeleteCommand.CanExecute(null));
        Assert.Equal("Cancel the check first", runs.DeleteTip);
    }

    [Fact]
    public async Task A_running_check_shows_when_the_page_opens()
    {
        var ctx = Context();
        ctx.Answers[LoadTestMethods.ListRuns] = _ => LoadTestJson.Serialize(new RunList([Samples.Running(300)], Samples.RunId));
        ctx.Answers[LoadTestMethods.GetRun] = _ => LoadTestJson.Serialize(Samples.RunningDetail(300));
        using var vm = new LoadTestViewModel(ctx);
        vm.Activate();
        await Eventually.TrueAsync(() => vm.IsRunning && vm.Rows.Count == 150, "the running check");
        Assert.Equal(300, vm.RateSeries[0].Values.Count);

        // The pushed second after it appends.
        var next = Samples.Graphs(301);
        ctx.Push(LoadTestMethods.LiveTopic, new LiveEvent(Samples.Running(301), 300,
            new RunGraphs([next.Phases[300]], [next.TotalMbps[300]], [next.AverageLossPercent[300]], [next.WorstLossPercent[300]], [next.AverageJitterMs[300]], []), []));
        await Eventually.TrueAsync(() => vm.RateSeries[0].Values.Count == 301, "the live point");
    }

    [Fact]
    public void Ramp_cells_take_the_worst_step_the_stream_ran_in()
    {
        Assert.Equal(PhaseResult.NotApplicable, PhaseCell.Combine([PhaseResult.NotApplicable, PhaseResult.NotApplicable]).Result);
        Assert.Equal(PhaseResult.Failed, PhaseCell.Combine([PhaseResult.NotApplicable, PhaseResult.Passed, PhaseResult.Failed]).Result);
        Assert.Equal(PhaseResult.Running, PhaseCell.Combine([PhaseResult.Passed, PhaseResult.Running, PhaseResult.Pending]).Result);
        Assert.Equal(PhaseResult.Interrupted, PhaseCell.Combine([PhaseResult.Failed, PhaseResult.Interrupted]).Result);
    }

    [Fact]
    public void Thirty_thousand_rows_load_and_update()
    {
        var ctx = Context();
        using var vm = new LoadTestViewModel(ctx);
        var rows = ManyRows(30_000);
        vm.ApplyLive(new LiveEvent(Samples.Running(1), 0, Samples.Graphs(1), rows));
        Assert.Equal(30_000, vm.Rows.Count);
        vm.ApplyRows([.. rows.Take(200).Select(r => r with { AverageMbps = 9 })]);
        Assert.Equal(30_000, vm.Rows.Count);
    }

    internal static List<StreamRow> ManyRows(int count)
    {
        var template = Samples.Row(1);
        var rows = new List<StreamRow>(count);
        for (var i = 0; i < count; i++)
        {
            rows.Add(template with { StreamId = $"{i}/1", Name = "Camera " + i, FrameLossPercent = i % 97 / 100.0 });
        }

        return rows;
    }
}

[Trait("Category", "Perf")]
public sealed class PagePerfTests(ITestOutputHelper output)
{
    [Fact]
    public void Thirty_thousand_rows_update_every_second_within_budget()
    {
        using var vm = new LoadTestViewModel(new FakePageContext());
        var rows = PageViewModelTests.ManyRows(30_000);
        var watch = Stopwatch.StartNew();
        vm.ApplyLive(new LiveEvent(Samples.Running(1), 0, Samples.Graphs(1), rows));
        var load = watch.Elapsed;
        watch.Restart();
        for (var s = 1; s < 11; s++)
        {
            var changed = rows.Skip(s * 200).Take(200).Select(r => r with { FrameLossPercent = 3 }).ToList();
            var g = Samples.Graphs(s + 1);
            vm.ApplyLive(new LiveEvent(Samples.Running(s + 1), s,
                new RunGraphs([g.Phases[s]], [g.TotalMbps[s]], [g.AverageLossPercent[s]], [g.WorstLossPercent[s]], [g.AverageJitterMs[s]], []), changed));
        }

        var updates = watch.Elapsed / 10;
        output.WriteLine($"30,000 rows: load {load.TotalMilliseconds:0} ms, one live second {updates.TotalMilliseconds:0} ms");
        Assert.True(load < TimeSpan.FromSeconds(3), $"load took {load.TotalMilliseconds:0} ms");
        Assert.True(updates < TimeSpan.FromSeconds(1), $"one live second took {updates.TotalMilliseconds:0} ms");
    }
}
