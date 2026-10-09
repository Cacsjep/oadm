using System.Diagnostics;
using System.Net;

using Avalonia.Headless;

using Oadm.Plugins.ImageHealth.Client;
using Oadm.Plugins.ImageHealth.Monitoring;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.ImageHealth.Tests;

public sealed class ParserTests
{
    [Fact]
    public void The_recorded_answers_map_to_ok_pending_and_detected()
    {
        Assert.All(AihaParsing.ParseStatus(Recorded.Normal).Values, v => Assert.Equal(DetectionStates.Ok, v));
        Assert.Equal(AihaDetections.All.Order(), AihaParsing.ParseStatus(Recorded.Normal).Keys.Order());

        var pending = AihaParsing.ParseStatus(Recorded.Pending);
        Assert.Equal(
            [DetectionStates.Pending, DetectionStates.Pending, DetectionStates.Pending, DetectionStates.Ok, DetectionStates.Ok],
            [pending[AihaDetections.Blur], pending[AihaDetections.Block], pending[AihaDetections.Redirect], pending[AihaDetections.UnderExposure], pending[AihaDetections.Unsuitability]]);

        var detected = AihaParsing.ParseStatus(Recorded.Detected);
        Assert.Equal((DetectionStates.Detected, DetectionStates.Detected, DetectionStates.Pending), (detected[AihaDetections.Blur], detected[AihaDetections.Block], detected[AihaDetections.Redirect]));
    }

    [Fact]
    public void Unsuitable_disabled_other_values_missing_keys_and_broken_answers()
    {
        var status = AihaParsing.ParseStatus("{\"unsuitability\":\"unsuitable\",\"blur\":\"calibrating\",\"block\":\"disabled\"}");
        Assert.Equal(DetectionStates.Detected, status[AihaDetections.Unsuitability]);
        Assert.Equal(DetectionStates.Off, status[AihaDetections.Block]); // turned off in the app
        Assert.Equal("calibrating", status[AihaDetections.Blur]); // shown as the camera sent it
        Assert.False(status.ContainsKey(AihaDetections.Redirect));
        Assert.Throws<System.Text.Json.JsonException>(() => AihaParsing.ParseStatus("[1]"));
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => AihaParsing.ParseStatus("<html>"));

        var off = new DetectionCell(DetectionStates.Off);
        Assert.False(off.IsOk || off.IsWarning || off.IsError); // neutral chip
        Assert.True(off.SortKey > new DetectionCell(DetectionStates.Ok).SortKey);
    }
}

public sealed class MonitorTests
{
    [Fact]
    public async Task Nothing_is_asked_without_a_check_and_a_check_sorts_the_cameras()
    {
        await using var rig = await new Rig().StartAsync();
        var running = new TestDevice("10.0.0.48", "P3265-V");
        var stopped = new TestDevice("10.0.0.49", "P3265-V");
        var none = new TestDevice("10.0.0.50", "M3106-L");
        var speaker = new TestDevice("10.0.0.60", "C1310-E", Category: DeviceCategory.Speaker);
        rig.Devices.Items.AddRange([running, stopped, none, speaker]);
        var answer = Recorded.Normal;
        rig.Vapix.Answers[running.Id] = () => FakeVapix.Json(answer);
        var stoppedAnswer = HttpStatusCode.ServiceUnavailable;
        rig.Vapix.Answers[stopped.Id] = () => stoppedAnswer == HttpStatusCode.OK ? FakeVapix.Json(Recorded.Pending) : FakeVapix.Status(stoppedAnswer);
        var monitor = rig.Plugin.Monitor!;

        await Task.Delay(100);
        Assert.Equal(0, rig.Vapix.Requests); // no check: no request (no background polling)

        await CheckAsync(rig);
        Assert.Equal(3, rig.Vapix.Requests); // video devices only, once each
        var state = monitor.Snapshot(withRows: true);
        Assert.Equal((3, 1, 1, 1, 0), (state.Total, state.Running, state.NotRunning, state.WithoutApp, state.Failed));
        Assert.NotNull(state.CheckedUtc);
        var rows = state.Rows.ToDictionary(r => r.DeviceId);
        Assert.Equal(2, rows.Count);
        Assert.Equal(AppStates.Running, rows[running.Id].App);
        Assert.All(AihaDetections.All, d => Assert.Equal(DetectionStates.Ok, rows[running.Id].Detection(d)));
        Assert.Equal(AppStates.NotRunning, rows[stopped.Id].App);
        Assert.All(AihaDetections.All, d => Assert.Null(rows[stopped.Id].Detection(d)));
        Assert.Null(rows[running.Id].ChangedUtc);

        // The next check: a detection changed, the stopped app runs now, the running one was removed.
        answer = Recorded.Detected;
        stoppedAnswer = HttpStatusCode.OK;
        await CheckAsync(rig);
        rows = monitor.Snapshot(true).Rows.ToDictionary(r => r.DeviceId);
        Assert.Equal(DetectionStates.Detected, rows[running.Id].Blur);
        Assert.NotNull(rows[running.Id].ChangedUtc);
        Assert.Equal((AppStates.Running, DetectionStates.Pending), (rows[stopped.Id].App, rows[stopped.Id].Blur));
        Assert.Null(rows[stopped.Id].ChangedUtc); // the first answer of a started app is no change

        rig.Vapix.Answers[running.Id] = () => FakeVapix.Status(HttpStatusCode.NotFound);
        await CheckAsync(rig);
        Assert.DoesNotContain(monitor.Snapshot(true).Rows, r => r.DeviceId == running.Id);
        Assert.Equal(2, monitor.Snapshot(false).WithoutApp);

        var requests = rig.Vapix.Requests;
        await Task.Delay(150);
        Assert.Equal(requests, rig.Vapix.Requests); // nothing between checks
    }

    [Fact]
    public async Task A_check_while_one_runs_starts_no_second_one()
    {
        await using var rig = await new Rig().StartAsync();
        var camera = new TestDevice("10.0.0.48", "P3265-V");
        rig.Devices.Items.Add(camera);
        using var gate = new SemaphoreSlim(0);
        rig.Vapix.Answers[camera.Id] = () =>
        {
            gate.Wait();
            return FakeVapix.Json(Recorded.Normal);
        };

        var first = ImageHealthJson.Deserialize<ImageHealthState>(await rig.Plugin.InvokeAsync(ImageHealthMethods.Check, null, CancellationToken.None));
        var second = ImageHealthJson.Deserialize<ImageHealthState>(await rig.Plugin.InvokeAsync(ImageHealthMethods.Check, null, CancellationToken.None));
        Assert.True(first.Checking && second.Checking);
        gate.Release();
        await rig.Plugin.Monitor!.CurrentCheck;
        Assert.Equal(1, rig.Vapix.Requests);
    }

    [Fact]
    public async Task Failures_become_rows_and_changes_are_pushed()
    {
        await using var rig = await new Rig().StartAsync();
        var camera = new TestDevice("10.0.0.48", "P3265-V");
        var refused = new TestDevice("10.0.0.49", "P3265-V", DeviceStatus.CredentialsRequired);
        var unauthorized = new TestDevice("10.0.0.50", "P3265-V");
        var unreachable = new TestDevice("10.0.0.51", "P3265-V");
        var slow = new TestDevice("10.0.0.52", "P3265-V");
        rig.Devices.Items.AddRange([camera, refused, unauthorized, unreachable, slow]);
        rig.Vapix.Answers[camera.Id] = () => FakeVapix.Json(Recorded.Pending);
        rig.Vapix.Answers[unauthorized.Id] = () => FakeVapix.Status(HttpStatusCode.Unauthorized);
        rig.Vapix.Answers[unreachable.Id] = () => throw new HttpRequestException("Connection refused");
        rig.Vapix.Answers[slow.Id] = () => throw new TaskCanceledException(); // the request timeout
        var events = new List<PluginEvent>();
        using var cts = new CancellationTokenSource();
        var watch = Task.Run(async () =>
        {
            await foreach (var item in rig.Hub.WatchAsync(ImageHealthPluginInfo.PluginId, cts.Token))
            {
                lock (events)
                {
                    events.Add(item);
                }
            }
        });
        await Wait.UntilAsync(() => rig.Hub.WatcherCount(ImageHealthPluginInfo.PluginId) == 1);

        await CheckAsync(rig);
        var rows = rig.Plugin.Monitor!.Snapshot(true).Rows.ToDictionary(r => r.DeviceId);
        Assert.Equal(5, rows.Count);
        Assert.Equal("Credentials required - the device rejects the stored credentials", rows[refused.Id].Text);
        Assert.Equal(DeviceMessages.Unauthorized, rows[unauthorized.Id].Text);
        Assert.Equal("Unreachable - Connection refused", rows[unreachable.Id].Text);
        Assert.Equal(DeviceMessages.Timeout(TimeSpan.FromSeconds(3)), rows[slow.Id].Text);
        Assert.Equal((1, 4), (rig.Plugin.Monitor.Snapshot(false).Running, rig.Plugin.Monitor.Snapshot(false).Failed));

        await Wait.UntilAsync(() =>
        {
            lock (events)
            {
                return events.Any(e => e.Topic == ImageHealthMethods.RowsTopic && ImageHealthJson.Deserialize<RowsEvent>(e.PayloadJson).Rows.Any(r => r.Blur == DetectionStates.Pending))
                    && events.Any(e => e.Topic == ImageHealthMethods.StateTopic && !ImageHealthJson.Deserialize<ImageHealthState>(e.PayloadJson).Checking);
            }
        });
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => watch);
    }

    [Fact]
    public void The_defaults_are_the_user_decisions()
    {
        var options = new ImageHealthOptions();
        Assert.Equal((24, TimeSpan.FromSeconds(3)), (options.CheckParallelism, options.RequestTimeout));
        Assert.Equal(TimeSpan.FromSeconds(10), ImageHealthPluginInfo.AutoRefreshInterval);
    }

    [Fact]
    public async Task Twenty_four_cameras_are_asked_at_the_same_time()
    {
        await using var rig = await new Rig().StartAsync();
        var running = 0;
        var most = 0;
        for (var i = 0; i < 200; i++)
        {
            var device = new TestDevice($"10.0.1.{i}", "P3265-V");
            rig.Devices.Items.Add(device);
            rig.Vapix.Answers[device.Id] = () =>
            {
                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref most, now);
                Thread.Sleep(20);
                Interlocked.Decrement(ref running);
                return FakeVapix.Json(Recorded.Normal);
            };
        }

        await CheckAsync(rig);
        Assert.Equal(200, rig.Plugin.Monitor!.Snapshot(false).Running);
        Assert.InRange(most, 2, 24);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private static async Task CheckAsync(Rig rig)
    {
        await rig.Plugin.InvokeAsync(ImageHealthMethods.Check, null, CancellationToken.None);
        await rig.Plugin.Monitor!.CurrentCheck;
    }
}

public sealed class ViewModelTests
{
    [Fact]
    public async Task The_page_checks_once_when_shown_and_auto_refresh_checks_again_only_while_on()
    {
        await using var rig = await new Rig().StartAsync();
        var camera = new TestDevice("10.0.0.48", "P3265-V");
        rig.Devices.Items.Add(camera);
        rig.Vapix.Answers[camera.Id] = () => FakeVapix.Json(Recorded.Pending);
        var ctx = new PluginPageContext(rig.Plugin, rig.Hub);
        ImageHealthViewModel.AutoRefreshInterval = TimeSpan.FromMilliseconds(50);
        using var vm = new ImageHealthViewModel(ctx);
        Assert.False(vm.AutoRefresh); // off by default

        vm.Activate();
        await Wait.UntilAsync(() => vm.Rows.Count == 1 && vm.Rows[0].IsRunning && !vm.IsChecking);
        Assert.StartsWith("1 camera with the app running · 1 pending · checked ", vm.SummaryText, StringComparison.Ordinal);
        Assert.True(vm.Rows[0].Blur.IsWarning);
        Assert.True(vm.Rows[0].UnderExposure.IsOk);
        await Task.Delay(200);
        Assert.Equal(1, rig.Vapix.Requests); // auto refresh off: one check

        vm.AutoRefresh = true;
        await Wait.UntilAsync(() => rig.Vapix.Requests >= 3);

        vm.Deactivate(); // page hidden: no more checks
        await rig.Plugin.Monitor!.CurrentCheck;
        var requests = rig.Vapix.Requests;
        await Task.Delay(200);
        Assert.Equal(requests, rig.Vapix.Requests);
    }

    [Fact]
    public void Five_thousand_cameras_update_search_and_summarize_fast()
    {
        var vm = new ImageHealthViewModel(new PluginPageContext(new ImageHealthPlugin(), new Oadm.Core.Plugins.PluginEventHub()));
        var rows = Enumerable.Range(0, 5_000).Select(i => Row(i, DetectionStates.Ok)).ToList();
        var watch = Stopwatch.StartNew();
        vm.ApplyState(new ImageHealthState(false, 5_000, 5_000, 5_000, 0, 0, 0, null, rows), replaceRows: true);
        var load = watch.ElapsedMilliseconds;

        watch.Restart();
        vm.ApplyRows([.. rows.Take(1_000).Select(r => r with { Blur = DetectionStates.Detected })], [rows[^1].DeviceId]);
        var update = watch.ElapsedMilliseconds;

        watch.Restart();
        vm.ApplyState(new ImageHealthState(false, 5_000, 5_000, 4_999, 0, 0, 0, null, rows.Take(4_999).ToList()), replaceRows: true);
        vm.SearchText = "10.0.1.";
        var search = watch.ElapsedMilliseconds;

        Console.WriteLine($"5,000 rows: load {load} ms, 1,000 changes + 1 removed {update} ms, check reply + search {search} ms");
        Assert.Equal(256, vm.Rows.Count); // 10.0.1.0 .. 10.0.1.255
        Assert.True(load + update + search < 2_000);
    }

    internal static ImageHealthRow Row(int i, string state) => new(
        Guid.NewGuid(), $"10.0.{i / 256}.{i % 256}", "P3265-V", AppStates.Running, null, state, state, state, state, state, null);
}

/// <summary>Renders the page offscreen like the client shell. OADM_SCREENSHOT_DIR writes PNGs.</summary>
public sealed class HeadlessPageTests
{
    [Fact]
    public async Task Page_renders_every_state()
    {
        var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
        await session.Dispatch(() =>
        {
            var vm = new ImageHealthViewModel(new PluginPageContext(new ImageHealthPlugin(), new Oadm.Core.Plugins.PluginEventHub()));
            var view = new ImageHealthView();
            var window = Headless.Host(view);
            window.Show();
            view.DataContext = vm; // after attaching: no check, deterministic content
            const string ok = DetectionStates.Ok;
            const string pending = DetectionStates.Pending;
            const string detected = DetectionStates.Detected;
            const string off = DetectionStates.Off;
            vm.ApplyState(new ImageHealthState(false, 7, 7, 3, 1, 2, 1, null,
            [
                new ImageHealthRow(Guid.NewGuid(), "10.0.0.48", "P3265-V", AppStates.Running, null, ok, ok, ok, off, ok, null),
                new ImageHealthRow(Guid.NewGuid(), "10.0.0.52", "Q3548-LVE", AppStates.Running, null, detected, detected, pending, ok, ok, new DateTimeOffset(2026, 10, 9, 18, 21, 7, TimeSpan.Zero)),
                new ImageHealthRow(Guid.NewGuid(), "10.0.0.61", "M3215-LVE", AppStates.Running, null, pending, pending, pending, ok, ok, new DateTimeOffset(2026, 10, 9, 18, 20, 52, TimeSpan.Zero)),
                new ImageHealthRow(Guid.NewGuid(), "10.0.0.70", "M3106-L Mk II", AppStates.NotRunning, null, null, null, null, null, null, null),
                new ImageHealthRow(Guid.NewGuid(), "10.0.0.202", "M3215-LVE", AppStates.Error, "Credentials required - the device rejects the stored credentials", null, null, null, null, null, null),
            ]), replaceRows: true);
            Headless.Pump();
            Assert.Equal(5, vm.Rows.Count);
            Assert.Equal("3 cameras with the app running · 1 with a detection · 1 pending · 1 not running · 2 without the app · 1 could not be checked", vm.SummaryText);
            Headless.Capture(window, "image-health-dashboard.png");
            window.Close();
        }, CancellationToken.None);
    }
}
