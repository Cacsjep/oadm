using System.Collections.Concurrent;
using System.Net;

using Avalonia.Headless;

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
        await rig.StartStreamAsync(io.Id);
        await rig.StartStreamAsync(io.Id);
        Assert.StartsWith("Too many devices", (await rig.StartStreamAsync(io.Id)).Error, StringComparison.Ordinal);
    }

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
