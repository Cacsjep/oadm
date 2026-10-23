using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.IoMonitor.Tests;

/// <summary>Device groups (docs/spec/device-scope.md): streams of the user's devices only, owned by the user, ending when the device leaves the view.</summary>
public sealed class CanaryTests
{
    private static Testing.FakeDevice IoDevice() => new() { Apis = [new DeviceApi(IoMonitorPluginInfo.ApiId, "1.0")] };

    private static IoMonitorPlugin Plugin() => new(new IoMonitorOptions { PollInterval = TimeSpan.FromMilliseconds(20), RetryDelay = TimeSpan.FromMilliseconds(20) });

    private static string StreamId(string? reply) => IoMonitorJson.Deserialize<StartReply>(reply).StreamId ?? throw new InvalidOperationException("No stream.");

    [Fact]
    public async Task No_method_reaches_a_device_outside_the_users_group()
    {
        var ctx = new Testing.FakeCoreContext();
        var inside = ctx.Add(IoDevice());
        var outside = ctx.Add(IoDevice());
        await using var plugin = Plugin();
        await plugin.StartAsync(ctx, CancellationToken.None);
        var start = $$"""{"deviceId":"{{inside.Id}}"}""";
        await new Testing.PluginCanary(ctx, plugin, inside, outside)
            .Method(IoMonitorMethods.Start, """{"deviceId":"{device}"}""")
            .Job(IoMonitorMethods.KeepAlive, """{"streamId":"{job}"}""", async owner => StreamId(await ctx.InvokeAsync(plugin, owner, IoMonitorMethods.Start, start)))
            .Job(IoMonitorMethods.Stop, """{"streamId":"{job}"}""", async owner => StreamId(await ctx.InvokeAsync(plugin, owner, IoMonitorMethods.Start, start)))
            .RunAsync();
    }

    [Fact]
    public async Task The_reading_goes_to_the_users_page_only_and_ends_when_the_device_leaves_the_view()
    {
        var ctx = new Testing.FakeCoreContext();
        var device = ctx.Add(IoDevice());
        ctx.Cameras[device.Id].Respond = (_, _) => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""{"apiVersion":"1.0","method":"getPorts","data":{"numberOfPorts":1,"items":[{"port":"0","configurable":false,"usage":"","name":"Door","direction":"input","state":"open","normalState":"open"}]}}"""),
        };
        await using var plugin = Plugin();
        await plugin.StartAsync(ctx, CancellationToken.None);
        var user = ctx.CallAsGroup([device.Id], "tech2");
        var other = ctx.CallAsGroup([device.Id], "guard");

        var streamId = StreamId(await ctx.InvokeAsync(plugin, user, IoMonitorMethods.Start, $$"""{"deviceId":"{{device.Id}}"}"""));
        await Testing.Eventually.TrueAsync(() => ctx.EventsSeenBy(user).Count > 0, "the ports reached the page");
        Assert.Empty(ctx.EventsSeenBy(other));
        Assert.Empty(ctx.RefusedAccesses);

        ctx.DeviceList.Remove(device); // the device leaves the user's view
        await Testing.Eventually.TrueAsync(() => plugin.StreamCount == 0 && plugin.DeviceCount == 0, "the stream ended");
        var last = IoMonitorJson.Deserialize<PortsEvent>(ctx.EventsSeenBy(user)[^1].PayloadJson);
        Assert.Equal((streamId, DeviceMessages.NoLongerAvailable(1)), (last.StreamId, last.Error));
        Assert.Empty(ctx.EventsSeenBy(other));
        Assert.False(IoMonitorJson.Deserialize<KeepAliveReply>(await ctx.InvokeAsync(plugin, user, IoMonitorMethods.KeepAlive, $$"""{"streamId":"{{streamId}}"}""")).Running);
    }
}
