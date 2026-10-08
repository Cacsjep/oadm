using Oadm.Plugins.MetadataMonitor.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.MetadataMonitor.Tests;

/// <summary>The page view model against the in-process plugin, the real event hub and a fake RTSP source.</summary>
public sealed class PageViewModelTests : IAsyncLifetime, IDisposable
{
    private readonly string _settingsPath = Path.Combine(Path.GetTempPath(), "oadm-mm-tests", Guid.NewGuid().ToString("N"), "client.json");
    private Rig _rig = null!;
    private PluginPageContext _ctx = null!;

    public async Task InitializeAsync()
    {
        _rig = await new Rig(new MetadataMonitorOptions { BatchInterval = TimeSpan.FromMilliseconds(20), LeaseTimeout = TimeSpan.FromSeconds(30) }).StartAsync();
        _ctx = new PluginPageContext(_rig.Plugin, _rig.Hub);
        _ctx.DeviceList.Add(_rig.Camera);
        foreach (var device in _rig.Devices.Items.Skip(1))
        {
            _ctx.DeviceList.Add(device);
        }
    }

    public async Task DisposeAsync() => await _rig.DisposeAsync();

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_settingsPath)!, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void Cameras_are_video_and_io_devices_sorted_by_ip_and_filtered_by_the_search()
    {
        _ctx.DeviceList.Add(new TestDevice("10.0.0.100", "P1465-LE"));
        _ctx.DeviceList.Add(new TestDevice("10.0.0.9", "A9188", Category: DeviceCategory.IoModule));
        _ctx.DeviceList.Add(new TestDevice("10.0.0.20", "C1310-E", Category: DeviceCategory.Speaker));
        _ctx.DeviceList.Add(new TestDevice("cam.example.com", "Q1656"));
        using var vm = NewViewModel();

        Assert.Equal(["A9188 (10.0.0.9)", "P3265-V (10.0.0.48)", "P1465-LE (10.0.0.100)", "Q1656 (cam.example.com)"], vm.Cameras.Select(c => c.Label));

        vm.SelectedCamera = vm.Cameras[1];
        vm.CameraSearch = "q16";
        Assert.Equal(["P3265-V (10.0.0.48)", "Q1656 (cam.example.com)"], vm.Cameras.Select(c => c.Label)); // the selected one stays
        Assert.Equal("P3265-V (10.0.0.48)", vm.SelectedCamera!.Label);
        vm.CameraSearch = string.Empty;
        Assert.Equal(4, vm.Cameras.Count);
    }

    [Fact]
    public async Task Start_shows_live_messages_and_stop_ends_the_stream()
    {
        using var vm = NewViewModel();
        vm.Activate();
        await Wait.UntilAsync(() => _rig.Hub.WatcherCount(MetadataMonitorPluginInfo.PluginId) == 1);
        Assert.False(vm.StartStopCommand.CanExecute(null)); // no camera yet
        Assert.Equal("Choose a camera first", vm.StartStopTip);

        vm.SelectedCamera = vm.Cameras[0];
        await vm.StartStopCommand.ExecuteAsync(null);
        Assert.True(vm.IsRunning);
        Assert.Equal("Stop", vm.StartStopText);
        foreach (var document in RecordedEvents.Documents)
        {
            _rig.Streams.Last!.Write(document);
        }

        await Wait.UntilAsync(() => vm.Messages.Count == RecordedEvents.Documents.Count - 1);
        await Wait.UntilAsync(() => vm.StatusText == $"Live · {vm.Messages.Count} messages");
        Assert.True(vm.IsStatusOk);
        var row = vm.Messages.First(r => r.Topic == "Device/IO/VirtualInput");
        Assert.Equal("Event", row.Category);
        Assert.Equal("Initialized", row.Operation);
        Assert.StartsWith("[INIT] port = ", row.Info, StringComparison.Ordinal);
        Assert.Equal("2026-10-08 09:30:01.000", row.CaptureTime);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}$", row.Timestamp);
        Assert.Equal(129 - 1, vm.Messages[^1].Seq);

        await vm.StartStopCommand.ExecuteAsync(null);
        Assert.False(vm.IsRunning);
        Assert.True(_rig.Streams.Last!.Disposed);
        Assert.Equal("Stopped", vm.StatusText);
        Assert.Equal(0, _rig.Plugin.StreamCount);
        Assert.Equal(128, vm.Messages.Count); // the list stays
    }

    [Fact]
    public async Task Start_error_is_shown_in_the_status_chip()
    {
        _rig.Streams.Refuse(DeviceStreamError.Unauthorized, "Unauthorized - HTTP 401");
        using var vm = NewViewModel();
        vm.SelectedCamera = vm.Cameras[0];

        await vm.StartStopCommand.ExecuteAsync(null);

        Assert.False(vm.IsRunning);
        Assert.True(vm.IsStatusError);
        Assert.Equal("Unauthorized - HTTP 401", vm.StatusText);
        Assert.Equal("Start", vm.StartStopText);
    }

    [Fact]
    public async Task Switching_the_camera_and_leaving_the_page_stop_the_stream()
    {
        _ctx.DeviceList.Add(new TestDevice("10.0.0.60", "M3106"));
        _rig.Devices.Items.Add(_ctx.DeviceList[^1]);
        using var vm = NewViewModel();
        vm.Activate();
        vm.SelectedCamera = vm.Cameras[0];
        await vm.StartStopCommand.ExecuteAsync(null);
        Assert.Equal(1, _rig.Plugin.StreamCount);

        vm.SelectedCamera = vm.Cameras[1];
        await Wait.UntilAsync(() => _rig.Plugin.StreamCount == 0);
        Assert.False(vm.IsRunning);

        await vm.StartStopCommand.ExecuteAsync(null);
        Assert.Equal(1, _rig.Plugin.StreamCount);
        vm.Deactivate();
        await Wait.UntilAsync(() => _rig.Plugin.StreamCount == 0);
    }

    [Fact]
    public async Task Keep_alives_are_sent_while_running()
    {
        var interval = MetadataMonitorViewModel.KeepAliveInterval;
        MetadataMonitorViewModel.KeepAliveInterval = TimeSpan.FromMilliseconds(30);
        try
        {
            using var vm = NewViewModel();
            vm.SelectedCamera = vm.Cameras[0];
            await vm.StartStopCommand.ExecuteAsync(null);
            await Wait.UntilAsync(() => _ctx.Calls.Count(c => c == MetadataMethods.KeepAlive) >= 3);
            await vm.StartStopCommand.ExecuteAsync(null);
            var count = _ctx.Calls.Count(c => c == MetadataMethods.KeepAlive);
            await Task.Delay(100);
            Assert.Equal(count, _ctx.Calls.Count(c => c == MetadataMethods.KeepAlive));
        }
        finally
        {
            MetadataMonitorViewModel.KeepAliveInterval = interval;
        }
    }

    [Fact]
    public void Filter_is_live_and_case_insensitive_over_topic_info_and_xml()
    {
        using var vm = NewViewModel();
        vm.ApplyMessages(Messages(1, 10, "Device/IO/VirtualInput"));
        vm.ApplyMessages(Messages(11, 5, "Storage/Alert"));

        vm.FilterText = "storage";
        Assert.Equal(5, vm.Messages.Count);
        Assert.Equal("5 of 15 messages", vm.SummaryText);
        vm.FilterText = "PORT = 3;";
        Assert.Equal([3L], vm.Messages.Select(m => m.Seq));
        vm.FilterText = "tnsaxis:io"; // raw XML
        Assert.Equal(10, vm.Messages.Count);

        // New batches are filtered too.
        vm.ApplyMessages(Messages(16, 2, "Device/IO/VirtualInput"));
        Assert.Equal(12, vm.Messages.Count);
        vm.FilterText = string.Empty;
        Assert.Equal(17, vm.Messages.Count);
        Assert.Equal("17 messages", vm.SummaryText);
    }

    [Fact]
    public void Clear_empties_the_list_and_selection()
    {
        using var vm = NewViewModel();
        vm.ApplyMessages(Messages(1, 10, "Device/IO/VirtualInput"));
        vm.SelectedMessage = vm.Messages[3];

        vm.ClearCommand.Execute(null);

        Assert.Empty(vm.Messages);
        Assert.Null(vm.SelectedMessage);
        Assert.Equal("No messages", vm.SummaryText);
        Assert.False(vm.HasDetail);
    }

    [Fact]
    public void Newest_ten_thousand_are_kept_and_the_selection_survives_batches()
    {
        using var vm = NewViewModel();
        var resets = 0;
        vm.Messages.CollectionChanged += (_, e) => resets++;
        vm.ApplyMessages(Messages(1, 9_000, "Device/IO/VirtualInput"));
        vm.SelectedMessage = vm.Messages[5_000];
        var selected = vm.SelectedMessage;
        var appended = 0;
        vm.MessagesAppended += (_, _) => appended++;

        vm.ApplyMessages(Messages(9_001, 2_000, "Device/IO/VirtualInput"));

        Assert.Equal(MetadataMonitorPluginInfo.MaxClientMessages, vm.Messages.Count);
        Assert.Equal(1_001, vm.Messages[0].Seq);
        Assert.Equal(11_000, vm.Messages[^1].Seq);
        Assert.Same(selected, vm.SelectedMessage);
        Assert.Equal(1, appended);
        Assert.Equal(3, resets); // first batch, then trim + append: one step each

        // The trimmed row loses the selection.
        vm.SelectedMessage = vm.Messages[0];
        vm.ApplyMessages(Messages(11_001, 10, "Device/IO/VirtualInput"));
        Assert.Null(vm.SelectedMessage);
    }

    [Fact]
    public void A_grid_reset_does_not_clear_the_selection()
    {
        using var vm = NewViewModel();
        vm.ApplyMessages(Messages(1, 10, "Device/IO/VirtualInput"));
        vm.SelectedMessage = vm.Messages[2];
        var pushedBack = false;
        vm.Messages.CollectionChanged += (_, _) => vm.SelectedMessage = null; // what a DataGrid does on Reset
        vm.PropertyChanged += (_, e) => pushedBack |= e.PropertyName == nameof(vm.SelectedMessage);

        vm.ApplyMessages(Messages(11, 5, "Device/IO/VirtualInput"));

        Assert.Equal(3, vm.SelectedMessage!.Seq);
        Assert.True(pushedBack);
    }

    [Fact]
    public async Task Detail_shows_the_xml_pretty_or_raw_and_copies_what_is_shown()
    {
        using var vm = NewViewModel();
        string? copied = null;
        vm.CopyText = text =>
        {
            copied = text;
            return Task.CompletedTask;
        };
        vm.ApplyMessages(Messages(1, 1, "Device/IO/VirtualInput"));
        Assert.False(vm.CopyCommand.CanExecute(null));
        Assert.Equal("Select a message to see its XML", vm.DetailTitle);

        vm.SelectedMessage = vm.Messages[0];
        Assert.Equal("#1 · Device/IO/VirtualInput", vm.DetailTitle);
        Assert.True(vm.IsPretty);
        await vm.CopyCommand.ExecuteAsync(null);
        Assert.Contains("\n  <wsnt:Topic", copied, StringComparison.Ordinal);

        vm.ShowRawCommand.Execute(null);
        Assert.True(vm.IsRaw);
        await vm.CopyCommand.ExecuteAsync(null);
        Assert.Equal(vm.SelectedMessage.Xml, copied);
    }

    [Fact]
    public void Autoscroll_is_on_by_default_and_the_detail_height_is_remembered()
    {
        using (var vm = NewViewModel())
        {
            Assert.True(vm.Autoscroll);
            Assert.Equal(260, vm.DetailHeight);
            vm.DetailHeight = 333;
        }

        using var again = NewViewModel();
        Assert.Equal(333, again.DetailHeight);
    }

    [Fact]
    public void Events_of_other_streams_are_ignored()
    {
        using var vm = NewViewModel();
        vm.HandleEvent(new PluginEvent(MetadataMethods.MessagesTopic, MetadataJson.Serialize(new MessagesEvent("other", Messages(1, 3, "x"), 0))));
        vm.HandleEvent(new PluginEvent(MetadataMethods.StateTopic, MetadataJson.Serialize(new MonitorState("other", Guid.Empty, MonitorStates.Error, "boom", 0, 0))));

        Assert.Empty(vm.Messages);
        Assert.False(vm.IsStatusError);
    }

    internal static List<MetadataMessage> Messages(long firstSeq, int count, string topic)
    {
        var list = new List<MetadataMessage>(count);
        for (var i = 0; i < count; i++)
        {
            var seq = firstSeq + i;
            var axisTopic = "tns1:" + topic.Replace("IO/", "tnsaxis:IO/", StringComparison.Ordinal);
            var xml = $"<wsnt:NotificationMessage xmlns:wsnt=\"http://docs.oasis-open.org/wsn/b-2\" xmlns:tns1=\"http://www.onvif.org/ver10/topics\" xmlns:tnsaxis=\"http://www.axis.com/2009/event/topics\"><wsnt:Topic>{axisTopic}</wsnt:Topic><wsnt:Message>port {seq}</wsnt:Message></wsnt:NotificationMessage>";
            var time = new DateTimeOffset(2026, 10, 8, 9, 30, 0, TimeSpan.Zero).AddMilliseconds(seq);
            list.Add(new MetadataMessage(seq, time, MessageCategories.Event, topic, time, "Initialized", $"[INIT] port = {seq}; active = 0;", xml));
        }

        return list;
    }

    private MetadataMonitorViewModel NewViewModel() => new(_ctx, new MetadataClientSettingsStore(_settingsPath));
}
