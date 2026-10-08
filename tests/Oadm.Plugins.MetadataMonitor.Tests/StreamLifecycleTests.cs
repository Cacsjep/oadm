using Oadm.Sdk.Devices;

namespace Oadm.Plugins.MetadataMonitor.Tests;

/// <summary>The server part with a fake RTSP source: start, batches, stop, reconnect, refusals, lease (page gone).</summary>
public sealed class StreamLifecycleTests
{
    [Fact]
    public async Task Start_streams_numbered_messages_in_batches_and_stop_tears_down()
    {
        await using var rig = await new Rig().StartAsync();
        await using var log = new EventLog(rig.Hub);

        var reply = await rig.StartStreamAsync();
        Assert.Null(reply.Error);
        Assert.NotNull(reply.StreamId);
        var source = rig.Streams.Last!;
        foreach (var document in RecordedEvents.Documents)
        {
            source.Write(document);
        }

        await Wait.UntilAsync(() => log.Messages.Count == RecordedEvents.Documents.Count - 1);
        var messages = log.Messages;
        Assert.Equal(Enumerable.Range(1, messages.Count).Select(i => (long)i), messages.Select(m => m.Seq));
        Assert.All(log.Batches, b => Assert.Equal(reply.StreamId, b.StreamId));
        Assert.All(messages, m => Assert.Equal(new DateTimeOffset(2026, 10, 8, 9, 30, 1, TimeSpan.Zero), m.CaptureUtc));
        await Wait.UntilAsync(() => log.States.LastOrDefault()?.Messages == messages.Count);
        Assert.Equal([MonitorStates.Connecting, MonitorStates.Live], log.States.Select(s => s.State).Distinct().Take(2));

        await rig.StopStreamAsync(reply.StreamId!);
        Assert.True(source.Disposed);
        await Wait.UntilAsync(() => log.States[^1].State == MonitorStates.Stopped);
        Assert.Equal(0, rig.Plugin.StreamCount);
    }

    [Fact]
    public async Task A_broken_connection_reconnects_with_backoff_and_keeps_numbering()
    {
        await using var rig = await new Rig().StartAsync();
        await using var log = new EventLog(rig.Hub);
        var reply = await rig.StartStreamAsync();
        var first = rig.Streams.Last!;
        first.Write(RecordedEvents.Notification("tns1:Device/tnsaxis:IO/VirtualInput", "Initialized", 1, 0));
        await Wait.UntilAsync(() => log.Messages.Count == 1);
        first.LostDocuments = 2;

        rig.Streams.Refuse(DeviceStreamError.Unreachable, "Unreachable - Connection refused");
        first.Break();
        await Wait.UntilAsync(() => rig.Streams.Opened.Count == 2);
        var second = rig.Streams.Last!;
        second.Write(RecordedEvents.Notification("tns1:Device/tnsaxis:IO/VirtualInput", "Changed", 1, 1));
        await Wait.UntilAsync(() => log.Messages.Count == 2);

        Assert.True(first.Disposed);
        Assert.Equal(3, rig.Streams.Attempts);
        Assert.Equal(2, log.Messages[^1].Seq);
        var states = log.States;
        Assert.Contains(states, s => s.State == MonitorStates.Reconnecting && s.Text == "Unreachable - the connection was lost");
        Assert.Contains(states, s => s.State == MonitorStates.Reconnecting && s.Text == "Unreachable - Connection refused");
        await Wait.UntilAsync(() => log.States[^1] is { State: MonitorStates.Live, Messages: 2, Lost: 2 });
        await rig.StopStreamAsync(reply.StreamId!);
        Assert.True(second.Disposed);
    }

    [Fact]
    public async Task Rejected_credentials_during_a_reconnect_end_the_stream_with_an_error()
    {
        await using var rig = await new Rig().StartAsync();
        await using var log = new EventLog(rig.Hub);
        await rig.StartStreamAsync();
        rig.Streams.Refuse(DeviceStreamError.Unauthorized, "Unauthorized - HTTP 401");
        rig.Streams.Last!.Break();

        await Wait.UntilAsync(() => log.States.LastOrDefault()?.State == MonitorStates.Error);
        Assert.Equal("Unauthorized - HTTP 401", log.States[^1].Text);
        Assert.Equal(2, rig.Streams.Attempts);
    }

    [Theory]
    [InlineData(DeviceStreamError.Unauthorized, "Unauthorized - HTTP 401")]
    [InlineData(DeviceStreamError.NotSupported, "The device has no event stream")]
    [InlineData(DeviceStreamError.Unreachable, "Unreachable - Connection refused")]
    public async Task Start_errors_are_returned_as_text(DeviceStreamError error, string text)
    {
        await using var rig = await new Rig().StartAsync();
        rig.Streams.Refuse(error, text);

        var reply = await rig.StartStreamAsync();

        Assert.Null(reply.StreamId);
        Assert.Equal(text, reply.Error);
        Assert.Equal(0, rig.Plugin.StreamCount);
    }

    [Theory]
    [InlineData(DeviceStatus.CertificateChanged, "Certificate changed. Remove the device and add it again to trust the new certificate.")]
    [InlineData(DeviceStatus.CredentialsRequired, "Credentials required - the device rejects the stored credentials")]
    [InlineData(DeviceStatus.PasswordNotSet, "Password not set - the device is in factory default")]
    public async Task Devices_with_a_bad_status_are_refused_without_connecting(DeviceStatus status, string text)
    {
        await using var rig = await new Rig().StartAsync();
        var device = new TestDevice("10.0.0.49", "P3265-V", status);
        rig.Devices.Items.Add(device);

        var reply = await rig.StartStreamAsync(device.Id);

        Assert.Equal(text, reply.Error);
        Assert.Equal(0, rig.Streams.Attempts);
    }

    [Fact]
    public async Task Unknown_device_and_a_host_without_event_streams_are_refused()
    {
        await using (var rig = await new Rig().StartAsync())
        {
            Assert.Equal("The device was removed from OADM.", (await rig.StartStreamAsync(Guid.NewGuid())).Error);
        }

        await using var old = await new Rig().StartAsync(withStreams: false);
        Assert.Equal("This OADM server cannot open device event streams", (await old.StartStreamAsync()).Error);
    }

    [Fact]
    public async Task A_stream_without_keep_alive_ends_when_its_page_is_gone()
    {
        await using var rig = await new Rig(Rig.ShortLease()).StartAsync();
        await using var log = new EventLog(rig.Hub);
        var reply = await rig.StartStreamAsync();

        // Keep-alives (every 100 ms for 1.5 s) hold it open past the 1 s lease...
        for (var i = 0; i < 15; i++)
        {
            await Task.Delay(100);
            await rig.Plugin.InvokeAsync(MetadataMethods.KeepAlive, MetadataJson.Serialize(new StreamRequest(reply.StreamId!)), CancellationToken.None);
        }

        Assert.Equal(1, rig.Plugin.StreamCount);

        // ...without them the server ends it (client closed).
        await Wait.UntilAsync(() => rig.Plugin.StreamCount == 0);
        await Wait.UntilAsync(() => rig.Streams.Last!.Disposed); // closed right after it is removed (asynchronously)
        await Wait.UntilAsync(() => log.States[^1].State == MonitorStates.Stopped);
    }

    [Fact]
    public async Task A_burst_above_the_batch_limit_drops_the_oldest_and_counts_them()
    {
        await using var rig = await new Rig(new MetadataMonitorOptions { BatchInterval = TimeSpan.FromSeconds(30) }).StartAsync(); // one flush: on Stop
        await using var log = new EventLog(rig.Hub);
        var reply = await rig.StartStreamAsync();
        var source = rig.Streams.Last!;
        for (var i = 1; i <= 2000; i++)
        {
            source.Write(RecordedEvents.Notification("tns1:Device/tnsaxis:IO/VirtualInput", "Initialized", i, 0));
        }

        await Wait.UntilAsync(() => rig.Plugin.StreamCount == 1);
        await Task.Delay(200);
        await rig.StopStreamAsync(reply.StreamId!);
        await Wait.UntilAsync(() => log.States.LastOrDefault()?.State == MonitorStates.Stopped);

        var messages = log.Messages;
        Assert.Equal(MetadataMonitorPluginInfo.MaxBatchMessages, messages.Count);
        Assert.Equal(1501, messages[0].Seq); // the newest 500 of 2,000
        Assert.Equal(1500, log.Batches.Sum(b => b.Dropped));
        Assert.Equal(1500, log.States[^1].Lost);
        Assert.Equal(2000, log.States[^1].Messages);
    }

    [Fact]
    public async Task Large_batches_are_split_below_the_event_size_limit()
    {
        await using var rig = await new Rig(new MetadataMonitorOptions { BatchInterval = TimeSpan.FromSeconds(30), MaxEventCharacters = 20_000 }).StartAsync();
        await using var log = new EventLog(rig.Hub);
        var reply = await rig.StartStreamAsync();
        for (var i = 1; i <= 100; i++)
        {
            rig.Streams.Last!.Write(RecordedEvents.Notification("tns1:Device/tnsaxis:IO/VirtualInput", "Initialized", i, 0));
        }

        await Task.Delay(200);
        await rig.StopStreamAsync(reply.StreamId!);
        await Wait.UntilAsync(() => log.Messages.Count == 100);
        Assert.True(log.Batches.Count > 1);
        Assert.Equal(Enumerable.Range(1, 100).Select(i => (long)i), log.Messages.Select(m => m.Seq));
    }

    [Fact]
    public async Task Stopping_the_plugin_ends_every_stream()
    {
        var rig = await new Rig().StartAsync();
        await rig.StartStreamAsync();
        await rig.StartStreamAsync();
        Assert.Equal(2, rig.Plugin.StreamCount);

        await rig.Plugin.StopAsync(CancellationToken.None);

        Assert.Equal(0, rig.Plugin.StreamCount);
        Assert.All(rig.Streams.Opened, s => Assert.True(s.Disposed));
        await rig.DisposeAsync();
    }
}
