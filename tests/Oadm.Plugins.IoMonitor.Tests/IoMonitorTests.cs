using System.Collections.Concurrent;
using System.Net;

using Avalonia.Headless;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.IoMonitor.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.IoMonitor.Tests;

public sealed class ServerTests
{
    [Fact]
    public void The_recorded_A9210_answer_has_relay_outputs_and_inputs()
    {
        var ports = IoMonitorPlugin.ParsePorts(FakeVapix.Recorded);

        Assert.Equal(11, ports.Count);
        Assert.Equal(["RELAY", "O1", "O2", "O3"], ports.Where(p => p.IsOutput).Select(p => p.Name));
        Assert.Equal(["I1", "I2", "I3", "I/O 1", "I/O 2", "I4", "I5"], ports.Where(p => !p.IsOutput).Select(p => p.Name));
        Assert.All(ports, p => Assert.False(p.Active));
        Assert.True(ports.Single(p => p.Name == "I/O 1").Configurable);
        Assert.Equal("Relay", ports[0].Usage);
        Assert.Equal("Bad port", Assert.Throws<InvalidOperationException>(() => IoMonitorPlugin.ParsePorts("""{"error":{"code":2104,"message":"Bad port"}}""")).Message);
    }

    [Fact]
    public async Task A_stream_pushes_the_ports_and_then_only_changes()
    {
        await using var rig = await new Rig().StartAsync();
        var device = new TestDevice("10.0.0.70", "A9210");
        rig.Devices.Items.Add(device);
        var events = await WatchAsync(rig);

        var reply = await rig.StartStreamAsync(device.Id);
        Assert.NotNull(reply.StreamId);
        await Wait.UntilAsync(() => events.Count == 1);
        var first = IoMonitorJson.Deserialize<PortsEvent>(events.Single().PayloadJson);
        Assert.Equal(reply.StreamId, first.StreamId);
        Assert.Equal(11, first.Ports.Count);

        var requests = rig.Vapix.Requests;
        await Wait.UntilAsync(() => rig.Vapix.Requests >= requests + 5);
        Assert.Single(events); // nothing changed: nothing pushed

        rig.Vapix.SetClosed("0", "1"); // relay and I1 active
        await Wait.UntilAsync(() => events.Count == 2);
        var changed = IoMonitorJson.Deserialize<PortsEvent>(events.Last().PayloadJson);
        Assert.Equal(["RELAY", "I1"], changed.Ports.Where(p => p.Active).Select(p => p.Name));
    }

    [Fact]
    public async Task A_failed_read_is_pushed_and_the_stream_recovers()
    {
        await using var rig = await new Rig().StartAsync();
        var device = new TestDevice("10.0.0.70", "A9210");
        rig.Devices.Items.Add(device);
        var events = await WatchAsync(rig);
        await rig.StartStreamAsync(device.Id);
        await Wait.UntilAsync(() => events.Count == 1);

        rig.Vapix.Override = () => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        await Wait.UntilAsync(() => events.Count == 2);
        var failed = IoMonitorJson.Deserialize<PortsEvent>(events.Last().PayloadJson);
        Assert.Equal(DeviceMessages.Unauthorized, failed.Error);
        Assert.Equal(11, failed.Ports.Count); // the last ports stay

        rig.Vapix.Override = null;
        await Wait.UntilAsync(() => events.Count == 3);
        Assert.Null(IoMonitorJson.Deserialize<PortsEvent>(events.Last().PayloadJson).Error);
    }

    [Fact]
    public async Task Stop_and_a_missing_keep_alive_end_the_reading()
    {
        await using var rig = await new Rig(TimeSpan.FromMilliseconds(300)).StartAsync();
        var device = new TestDevice("10.0.0.70", "A9210");
        rig.Devices.Items.Add(device);

        var reply = await rig.StartStreamAsync(device.Id);
        await rig.Plugin.InvokeAsync(IoMonitorMethods.Stop, IoMonitorJson.Serialize(new StreamRequest(reply.StreamId)), CancellationToken.None);
        Assert.Equal(0, rig.Plugin.StreamCount);
        var requests = rig.Vapix.Requests;
        await Task.Delay(150);
        Assert.Equal(requests, rig.Vapix.Requests);

        await rig.StartStreamAsync(device.Id); // no keep-alive: stopped after the lease
        await Wait.UntilAsync(() => rig.Plugin.StreamCount == 0);
    }

    [Theory]
    [InlineData(DeviceStatus.CredentialsRequired, DeviceMessages.CredentialsRequired)]
    [InlineData(DeviceStatus.PasswordNotSet, DeviceMessages.PasswordNotSet)]
    [InlineData(DeviceStatus.CertificateChanged, DeviceMessages.CertificateChanged)]
    public async Task Devices_that_refuse_the_login_are_not_asked(DeviceStatus status, string message)
    {
        await using var rig = await new Rig().StartAsync();
        var device = new TestDevice("10.0.0.70", "A9210", Status: status);
        rig.Devices.Items.Add(device);

        Assert.Equal(message, (await rig.StartStreamAsync(device.Id)).Error);
        Assert.Equal(0, rig.Vapix.Requests);
    }

    [Fact]
    public async Task Devices_without_ports_and_too_many_streams_are_refused()
    {
        await using var rig = await new Rig().StartAsync();
        var camera = new TestDevice("10.0.0.48", "P3265-V", HasIo: false);
        var io = new TestDevice("10.0.0.70", "A9210");
        rig.Devices.Items.AddRange([camera, io]);

        Assert.Equal("The device has no inputs or outputs to show", (await rig.StartStreamAsync(camera.Id)).Error);
        Assert.Equal(DeviceMessages.Removed, (await rig.StartStreamAsync(Guid.NewGuid())).Error);
        var io2 = new TestDevice("10.0.0.71", "A9210");
        var io3 = new TestDevice("10.0.0.72", "A9210");
        rig.Devices.Items.AddRange([io2, io3]);
        Assert.NotNull((await rig.StartStreamAsync(io.Id)).StreamId);
        Assert.NotNull((await rig.StartStreamAsync(io.Id)).StreamId); // a second page of the same device counts once
        Assert.NotNull((await rig.StartStreamAsync(io2.Id)).StreamId);
        Assert.StartsWith("Too many devices", (await rig.StartStreamAsync(io3.Id)).Error, StringComparison.Ordinal);
        Assert.NotNull((await rig.StartStreamAsync(io.Id)).StreamId);
        Assert.Equal(2, rig.Plugin.DeviceCount);
    }

    [Fact]
    public async Task Starts_at_the_same_time_never_pass_the_device_limit()
    {
        await using var rig = await new Rig().StartAsync();
        var devices = Enumerable.Range(1, 10).Select(i => new TestDevice($"10.0.0.{70 + i}", "A9210")).ToList();
        rig.Devices.Items.AddRange(devices);

        var replies = await Task.WhenAll(devices.Select(d => Task.Run(() => rig.StartStreamAsync(d.Id))));

        Assert.Equal(2, replies.Count(r => r.StreamId is not null));
        Assert.Equal(2, rig.Plugin.DeviceCount);
    }

    [Fact]
    [Trait("Category", "Timing")]
    public async Task Two_pages_of_one_device_share_one_reading()
    {
        await using var rig = await new Rig().StartAsync();
        var device = new TestDevice("10.0.0.70", "A9210");
        rig.Devices.Items.Add(device);
        var events = await WatchAsync(rig);

        var first = (await rig.StartStreamAsync(device.Id)).StreamId;
        await Wait.UntilAsync(() => events.Count == 1);
        var second = (await rig.StartStreamAsync(device.Id)).StreamId;
        await Wait.UntilAsync(() => events.Count == 2); // the second page gets the last read at once
        Assert.Equal([first, second], events.Select(e => IoMonitorJson.Deserialize<PortsEvent>(e.PayloadJson).StreamId));
        Assert.Equal(1, rig.Plugin.DeviceCount);
        Assert.Equal(2, rig.Plugin.StreamCount);

        // one read per poll for both pages: the requests grow like one stream (every 20 ms), not two
        var requests = rig.Vapix.Requests;
        await Task.Delay(400);
        Assert.InRange(rig.Vapix.Requests - requests, 1, 25);

        rig.Vapix.SetClosed("0");
        await Wait.UntilAsync(() => events.Count == 4); // each page gets the change
        Assert.Equal(new[] { first, second }.Order(), events.Skip(2).Select(e => IoMonitorJson.Deserialize<PortsEvent>(e.PayloadJson).StreamId).Order());

        await rig.Plugin.InvokeAsync(IoMonitorMethods.Stop, IoMonitorJson.Serialize(new StreamRequest(first)), CancellationToken.None);
        Assert.Equal(1, rig.Plugin.DeviceCount); // the other page still shows it
        await rig.Plugin.InvokeAsync(IoMonitorMethods.Stop, IoMonitorJson.Serialize(new StreamRequest(second)), CancellationToken.None);
        Assert.Equal(0, rig.Plugin.DeviceCount);
    }

    [Fact]
    public async Task Each_read_takes_the_current_client_of_the_factory()
    {
        await using var rig = await new Rig().StartAsync();
        var device = new TestDevice("10.0.0.70", "A9210");
        rig.Devices.Items.Add(device);
        var events = await WatchAsync(rig);
        await rig.StartStreamAsync(device.Id);
        await Wait.UntilAsync(() => events.Count == 1);

        rig.Vapix.ReplaceClient(); // address, credentials or certificate changed: the old client is disposed
        var requests = rig.Vapix.Requests;
        await Wait.UntilAsync(() => rig.Vapix.Requests >= requests + 5);
        Assert.Single(events); // no "disposed" error was pushed

        rig.Vapix.CreateError = new InvalidOperationException("The device was removed from OADM.");
        await Wait.UntilAsync(() => events.Count == 2);
        Assert.Equal(DeviceMessages.Unreachable("The device was removed from OADM."), IoMonitorJson.Deserialize<PortsEvent>(events.Last().PayloadJson).Error);
    }

    [Fact]
    public async Task After_a_timeout_the_next_read_waits_one_second()
    {
        var time = new FakeTimeProvider();
        await using var rig = await new Rig(retryDelay: TimeSpan.FromSeconds(1), time: time).StartAsync();
        var device = new TestDevice("10.0.0.70", "A9210");
        rig.Devices.Items.Add(device);
        var events = await WatchAsync(rig);
        rig.Vapix.Override = () =>
        {
            if (rig.Vapix.Requests == 1)
            {
                time.Advance(TimeSpan.FromSeconds(3)); // the request timed out after 3 s
            }

            throw new TaskCanceledException();
        };

        await rig.StartStreamAsync(device.Id);
        await Wait.UntilAsync(() => events.Count == 1);
        Assert.Equal(DeviceMessages.Timeout(TimeSpan.FromSeconds(3)), IoMonitorJson.Deserialize<PortsEvent>(events.Single().PayloadJson).Error);
        await Task.Delay(100);
        Assert.Equal(1, rig.Vapix.Requests);

        time.Advance(TimeSpan.FromMilliseconds(900));
        await Task.Delay(100);
        Assert.Equal(1, rig.Vapix.Requests);
        time.Advance(TimeSpan.FromMilliseconds(100));
        await Wait.UntilAsync(() => rig.Vapix.Requests == 2);
    }

    [Fact]
    public async Task No_start_is_accepted_after_the_plugin_stopped()
    {
        await using var rig = await new Rig().StartAsync();
        var device = new TestDevice("10.0.0.70", "A9210");
        rig.Devices.Items.Add(device);
        await rig.StartStreamAsync(device.Id);

        await rig.Plugin.StopAsync(CancellationToken.None);

        Assert.Equal(0, rig.Plugin.DeviceCount);
        Assert.NotNull((await rig.StartStreamAsync(device.Id)).Error);
        Assert.Equal(0, rig.Plugin.DeviceCount);
    }

    [Fact]
    public async Task A_keep_alive_says_whether_the_stream_still_runs()
    {
        await using var rig = await new Rig().StartAsync();
        var device = new TestDevice("10.0.0.70", "A9210");
        rig.Devices.Items.Add(device);
        var id = (await rig.StartStreamAsync(device.Id)).StreamId;

        Assert.True(await KeepAliveAsync(rig, id));
        Assert.False(await KeepAliveAsync(rig, "unknown"));
    }

    private static async Task<bool> KeepAliveAsync(Rig rig, string? streamId) =>
        IoMonitorJson.Deserialize<KeepAliveReply>(await rig.Plugin.InvokeAsync(IoMonitorMethods.KeepAlive, IoMonitorJson.Serialize(new StreamRequest(streamId)), CancellationToken.None)).Running;

    private static async Task<ConcurrentQueue<PluginEvent>> WatchAsync(Rig rig)
    {
        var events = new ConcurrentQueue<PluginEvent>();
        _ = Task.Run(async () =>
        {
            await foreach (var item in rig.Hub.WatchAsync(IoMonitorPluginInfo.PluginId, CancellationToken.None))
            {
                events.Enqueue(item);
            }
        });
        await Wait.UntilAsync(() => rig.Hub.WatcherCount(IoMonitorPluginInfo.PluginId) > 0); // subscribed before the first push
        return events;
    }
}

public sealed class PageTests
{
    [Fact]
    public async Task The_page_lists_devices_with_ports_and_shows_them_live()
    {
        await using var rig = await new Rig(TimeSpan.FromSeconds(1)).StartAsync();
        var io = new TestDevice("10.0.0.70", "AXIS A9210");
        var camera = new TestDevice("10.0.0.48", "P3265-V", HasIo: false);
        rig.Devices.Items.AddRange([io, camera]);
        var ctx = new PluginPageContext(rig.Plugin, rig.Hub, rig.Devices.Items);
        IoMonitorViewModel.KeepAliveInterval = TimeSpan.FromMilliseconds(50);
        using var vm = new IoMonitorViewModel(ctx);

        Assert.Equal("10.0.0.70 · A9210", Assert.Single(vm.Devices).Text);
        Assert.Equal("Choose a device.", vm.EmptyText);
        vm.Activate();
        vm.SelectedDevice = vm.Devices[0];
        await Wait.UntilAsync(() => vm.IsLive);

        Assert.Equal(["RELAY", "O1", "O2", "O3"], vm.Outputs.Select(t => t.Name));
        Assert.Equal(7, vm.Inputs.Count);
        Assert.Equal("Port 1", vm.Outputs[0].Number);
        Assert.Equal("Inactive", vm.Outputs[0].ActiveText);

        rig.Vapix.SetClosed("0");
        await Wait.UntilAsync(() => vm.Outputs[0].Active);
        Assert.Equal("Active", vm.Outputs[0].ActiveText);
        Assert.Equal("Closed, normally open · Relay", vm.Outputs[0].Detail);
        await Task.Delay(1_500); // the keep-alives (every 50 ms) hold the stream beyond the 1 s lease
        Assert.Equal(1, rig.Plugin.StreamCount);

        vm.Deactivate();
        await Wait.UntilAsync(() => rig.Plugin.StreamCount == 0);
    }

    [Fact]
    [Trait("Category", "Timing")]
    public async Task Failed_keep_alives_are_retried()
    {
        await using var rig = await new Rig(TimeSpan.FromSeconds(1)).StartAsync();
        var io = new TestDevice("10.0.0.70", "A9210");
        rig.Devices.Items.Add(io);
        var ctx = new PluginPageContext(rig.Plugin, rig.Hub, rig.Devices.Items);
        IoMonitorViewModel.KeepAliveInterval = TimeSpan.FromMilliseconds(50);
        using var vm = new IoMonitorViewModel(ctx);
        vm.Activate();
        vm.SelectedDevice = vm.Devices[0];
        await Wait.UntilAsync(() => vm.IsLive);

        ctx.FailMethod = IoMonitorMethods.KeepAlive; // the connection drops for a moment
        await Task.Delay(300);
        ctx.FailMethod = null;
        await Task.Delay(1_200); // beyond the 1 s lease: only the keep-alives after the drop hold the stream

        Assert.Equal(1, rig.Plugin.StreamCount);
        Assert.Single(ctx.Calls, IoMonitorMethods.Start);
    }

    [Fact]
    public async Task A_reading_that_overtakes_the_start_reply_is_shown()
    {
        // The server sends a new stream's first reading at once; it can arrive before the page knows the stream id.
        await using var rig = await new Rig().StartAsync();
        rig.Devices.Items.Add(new TestDevice("10.0.0.70", "A9210"));
        var ctx = new PluginPageContext(rig.Plugin, rig.Hub, rig.Devices.Items) { Watch = false };
        using var vm = new IoMonitorViewModel(ctx);
        IoPort relay = new("0", "RELAY", IsOutput: true, "closed", "open", "Relay", Configurable: true);
        ctx.BeforeStartReply = id => vm.HandleEvent(new PluginEvent(
            IoMonitorMethods.PortsTopic,
            IoMonitorJson.Serialize(new PortsEvent(id, [relay], null, DateTimeOffset.UtcNow))));
        vm.Activate();
        vm.SelectedDevice = vm.Devices[0];

        await Wait.UntilAsync(() => vm.IsLive);
        Assert.True(Assert.Single(vm.Outputs).Active);
    }

    [Fact]
    public async Task A_stream_the_server_forgot_is_started_again()
    {
        await using var rig = await new Rig().StartAsync();
        var io = new TestDevice("10.0.0.70", "A9210");
        rig.Devices.Items.Add(io);
        var ctx = new PluginPageContext(rig.Plugin, rig.Hub, rig.Devices.Items);
        IoMonitorViewModel.KeepAliveInterval = TimeSpan.FromMilliseconds(50);
        using var vm = new IoMonitorViewModel(ctx);
        vm.Activate();
        vm.SelectedDevice = vm.Devices[0];
        await Wait.UntilAsync(() => vm.IsLive);

        // The server restarts: while it is gone the page's calls fail (connection lost), as they do for a real client.
        // Without that a keep-alive between the stop and the start would restart the stream on the stopped plugin.
        ctx.FailMethod = IoMonitorMethods.KeepAlive;
        await rig.Plugin.StopAsync(CancellationToken.None);
        await rig.StartAsync();
        ctx.FailMethod = null;

        await Wait.UntilAsync(() => ctx.Calls.Count(c => c == IoMonitorMethods.Start) == 2);
        await Wait.UntilAsync(() => vm.IsLive && rig.Plugin.StreamCount == 1);
        rig.Vapix.SetClosed("0");
        await Wait.UntilAsync(() => vm.Outputs.Count > 0 && vm.Outputs[0].Active);
    }

    [Fact]
    public async Task A_device_list_change_keeps_the_stream_running()
    {
        await using var rig = await new Rig().StartAsync();
        var io = new TestDevice("10.0.0.70", "A9210");
        rig.Devices.Items.Add(io);
        var ctx = new PluginPageContext(rig.Plugin, rig.Hub, rig.Devices.Items);
        using var vm = new IoMonitorViewModel(ctx);
        vm.Activate();
        vm.SelectedDevice = vm.Devices[0];
        await Wait.UntilAsync(() => vm.IsLive);
        var changed = 0;
        vm.Devices.CollectionChanged += (_, _) => changed++;

        rig.Devices.Items.Add(new TestDevice("10.0.0.71", "A9188"));
        ctx.RaiseDevicesChanged();

        Assert.Equal(1, changed); // one reset
        Assert.Equal(2, vm.Devices.Count);
        Assert.Equal(io.Id, vm.SelectedDevice?.Id);
        Assert.Contains(vm.SelectedDevice!, vm.Devices);
        await Task.Delay(200);
        Assert.True(vm.IsLive);
        Assert.Single(ctx.Calls, IoMonitorMethods.Start);
        Assert.DoesNotContain(IoMonitorMethods.Stop, ctx.Calls);
        Assert.Equal(1, rig.Plugin.StreamCount);

        rig.Devices.Items.Remove(io); // removed from OADM: the reading stops
        ctx.RaiseDevicesChanged();
        Assert.Null(vm.SelectedDevice);
        await Wait.UntilAsync(() => rig.Plugin.StreamCount == 0);
    }

    [Fact]
    public async Task An_error_shows_next_to_the_last_ports()
    {
        await using var rig = await new Rig().StartAsync();
        var io = new TestDevice("10.0.0.70", "A9210", Status: DeviceStatus.CredentialsRequired);
        rig.Devices.Items.Add(io);
        using var vm = new IoMonitorViewModel(new PluginPageContext(rig.Plugin, rig.Hub, rig.Devices.Items));
        vm.Activate();
        vm.SelectedDevice = vm.Devices[0];

        await Wait.UntilAsync(() => vm.HasError);
        Assert.Equal(DeviceMessages.CredentialsRequired, vm.ErrorText);
        Assert.Equal("Error", vm.State);
    }

    [Fact]
    public async Task Page_renders_inputs_and_outputs()
    {
        var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
        var done = await session.Dispatch(async () =>
        {
            await using var rig = await new Rig().StartAsync();
            rig.Vapix.SetClosed("0", "2", "7");
            var io = new TestDevice("10.0.0.70", "AXIS A9210");
            rig.Devices.Items.Add(io);
            var vm = new IoMonitorViewModel(new PluginPageContext(rig.Plugin, rig.Hub, rig.Devices.Items));
            var window = Headless.Host(new IoMonitorView { DataContext = vm });
            window.Show();
            vm.SelectedDevice = vm.Devices[0];
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!vm.IsLive && DateTime.UtcNow < until)
            {
                Headless.Pump();
                await Task.Delay(20);
            }

            Headless.Pump();
            Assert.True(vm.IsLive);
            Headless.Capture(window, "io-monitor-page.png");
            window.Close();
            vm.Dispose();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }
}
