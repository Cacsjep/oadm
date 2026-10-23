using Oadm.Sdk.Devices;

using PluginCanary = Oadm.Sdk.Testing.PluginCanary;
using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.MetadataMonitor.Tests;

/// <summary>Device groups (docs/spec/device-scope.md): streams of the user's devices, owned by the user, ending when the device leaves the view.</summary>
public sealed class CanaryTests
{
    [Fact]
    public async Task No_method_reaches_a_device_outside_the_users_group()
    {
        var ctx = new Testing.FakeCoreContext();
        var inside = ctx.Add(new Testing.FakeDevice());
        var outside = ctx.Add(new Testing.FakeDevice());
        await using var plugin = new MetadataMonitorPlugin();
        await plugin.StartAsync(ctx, CancellationToken.None);

        // Another user's stream (the fake host offers no event streams, so the job stands for it).
        Task<string> OthersStream(Testing.FakeCallContext owner) =>
            Task.FromResult(ctx.FakeJobs.Start(owner, "stream", (_, token) => Task.Delay(Timeout.Infinite, token)).Id);
        await new PluginCanary(ctx, plugin, inside, outside)
            .Method(MetadataMethods.Start, """{"deviceId":"{device}"}""")
            .Method(MetadataMethods.GetRtspPort, """{"deviceId":"{device}"}""")
            .Job(MetadataMethods.KeepAlive, """{"streamId":"{job}"}""", OthersStream)
            .Job(MetadataMethods.Stop, """{"streamId":"{job}"}""", OthersStream)
            .RunAsync();
    }

    [Fact]
    public async Task Another_users_stream_answers_like_an_unknown_one_and_keeps_running()
    {
        await using var rig = await new Rig().StartAsync();
        var other = Testing.FakeCallContext.Over(rig.Devices, eventStreams: rig.Streams, userName: "guard");
        await using var log = new EventLog(rig.Hub, other.Watcher);
        var reply = await rig.StartStreamAsync();

        foreach (var method in new[] { MetadataMethods.KeepAlive, MetadataMethods.Stop })
        {
            var foreign = await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.Plugin.InvokeAsync(other, method, MetadataJson.Serialize(new StreamRequest(reply.StreamId!)), CancellationToken.None));
            var unknown = await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.Plugin.InvokeAsync(other, method, MetadataJson.Serialize(new StreamRequest("unknown")), CancellationToken.None));
            Assert.Equal((MetadataMethods.StreamEnded, MetadataMethods.StreamEnded), (foreign.Message, unknown.Message));
        }

        Assert.Equal(1, rig.Plugin.StreamCount);
        rig.Streams.Last!.Write(RecordedEvents.Notification("tns1:Device/tnsaxis:IO/VirtualInput", "Changed", 1, 1));
        await Wait.UntilAsync(() => rig.Streams.Last!.Consumed == 1);
        await rig.StopStreamAsync(reply.StreamId!);
        Assert.Empty(log.States); // nothing of it reached the other user's page
    }

    [Fact]
    public async Task A_stream_ends_with_an_error_when_its_device_leaves_the_users_view()
    {
        await using var rig = await new Rig().StartAsync();
        var group = Testing.FakeCallContext.OverGroup([rig.Camera.Id], rig.Devices, eventStreams: rig.Streams, userName: "tech2");
        await using var log = new EventLog(rig.Hub, group.Watcher);
        var reply = MetadataJson.Deserialize<StartReply>(await rig.Plugin.InvokeAsync(group, MetadataMethods.Start, MetadataJson.Serialize(new StartRequest(rig.Camera.Id)), CancellationToken.None));
        Assert.NotNull(reply.StreamId);

        rig.Streams.Last!.Fail(new DevicesNotAvailableException()); // what the host's stream does when the device leaves the view
        await Wait.UntilAsync(() => log.States.LastOrDefault()?.State == MonitorStates.Error);
        Assert.Equal(DeviceMessages.NoLongerAvailable(1), log.States[^1].Text);
        await Wait.UntilAsync(() => rig.Streams.Last!.Disposed);
        Assert.Equal(1, rig.Streams.Attempts); // never opened again

        // A device outside the group answers like an unknown one.
        rig.Devices.Items.Clear();
        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => rig.Plugin.InvokeAsync(group, MetadataMethods.Start, MetadataJson.Serialize(new StartRequest(rig.Camera.Id)), CancellationToken.None));
    }
}
