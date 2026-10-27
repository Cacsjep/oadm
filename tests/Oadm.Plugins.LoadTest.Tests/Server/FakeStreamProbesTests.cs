using Microsoft.Extensions.Time.Testing;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.LoadTest.Tests.Server;

/// <summary>The Sdk.Testing fake of the stream probes the plugin tests rely on.</summary>
public sealed class FakeStreamProbesTests
{
    [Fact]
    public async Task Counters_add_up_the_script_over_the_whole_seconds_since_the_start()
    {
        var time = new FakeTimeProvider();
        var probes = new FakeStreamProbes(time);
        var id = Guid.NewGuid();
        probes.Add(id).Configure(id, p => p.Script = s => s == 1 ? FakeStreamSecond.Silent() : FakeStreamSecond.Healthy());

        await using var probe = await probes.OpenAsync(id, new StreamProbeRequest(1), CancellationToken.None);
        Assert.Equal(0, probe.Read().Packets);
        time.Advance(TimeSpan.FromSeconds(3.5));

        var counters = probe.Read();
        Assert.Equal(600, counters.Packets);
        Assert.Equal(90, counters.ExpectedFrames);
        Assert.Equal(60, counters.Frames);
        Assert.Equal(("h265", 1920, 1080, 30), (probe.Codec, probe.Width, probe.Height, probe.Fps));
    }

    [Fact]
    public async Task Opens_can_wait_fail_and_streams_can_break()
    {
        var time = new FakeTimeProvider();
        var probes = new FakeStreamProbes(time);
        var id = Guid.NewGuid();
        var gate = new TaskCompletionSource();
        probes.Add(id).Configure(id, p => p.OpenGate = gate);

        var opening = probes.OpenAsync(id, new StreamProbeRequest(1), CancellationToken.None);
        await Eventually.TrueAsync(() => probes.OpensWaiting == 1, "the open waiting");
        gate.SetResult();
        var probe = (FakeStreamProbe)await opening;
        Assert.Equal(0, probes.OpensWaiting);

        probe.Fail("gone");
        await Assert.ThrowsAsync<DeviceStreamException>(() => probe.Completion);
        await probe.DisposeAsync();
        Assert.True(probe.IsDisposed);

        probes.OpenFailures[id] = new DeviceStreamException("refused");
        await Assert.ThrowsAsync<DeviceStreamException>(() => probes.OpenAsync(id, new StreamProbeRequest(1), CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => probes.OpenAsync(Guid.NewGuid(), new StreamProbeRequest(1), CancellationToken.None));
        Assert.Equal(2, probes.OpensFor(id));
    }

    [Fact]
    public async Task The_context_refuses_the_probes_in_a_group_users_call()
    {
        var ctx = new FakeCoreContext();
        var device = ctx.Add(new FakeDevice());
        ctx.FakeStreamProbes.Add(device.Id);
        var plugin = new ProbePlugin(ctx);

        await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => ctx.InvokeAsync(plugin, ctx.CallAsGroup([device.Id]), "list", null));
        Assert.Contains("StreamProbes", ctx.RefusedAccesses);
        Assert.Equal("1", await ctx.InvokeAsync(plugin, ctx.CallAsAllDevices(), "list", null));
    }

    private sealed class ProbePlugin(FakeCoreContext ctx) : ICorePlugin
    {
        public string Id => "test.probes";

        public string DisplayName => "Probes";

        public string? IconKey => null;

        public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

        public Task StartAsync(ICorePluginContext context, CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public async Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
            (await ctx.StreamProbes!.ListSourcesAsync(ctx.DeviceList[0].Id, ct)).Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
