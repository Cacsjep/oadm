using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

using Oadm.Core.Plugins;
using Oadm.Plugins.NtpServer.Protocol;
using Oadm.Plugins.NtpServer.Upstream;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.NtpServer.Tests;

/// <summary>
/// The plugin in process on 127.0.0.1 with a random high port (injectable port; the real port 123 is never bound) and
/// the real core plugin event hub.
/// </summary>
public sealed class ServerIntegrationTests : IAsyncLifetime
{
    private readonly InMemoryPluginSettingsProvider _settings = new();
    private readonly PluginEventHub _hub = new();
    private readonly FakeResolver _resolver = new();
    private NtpServerPlugin _plugin = null!;

    public async Task InitializeAsync()
    {
        _plugin = new NtpServerPlugin(Options.Test(_resolver));
        await _plugin.StartAsync(new TestCoreContext(_settings.GetSettings(NtpServerPluginInfo.PluginId), _hub.For(NtpServerPluginInfo.PluginId)), CancellationToken.None);
    }

    public async Task DisposeAsync() => await _plugin.DisposeAsync();

    private NtpServerService Service => _plugin.Service!;

    private IPEndPoint Endpoint => Service.Endpoints[0];

    private async Task<SaveReply> SaveAsync(bool enabled = true, string? interfaceId = "lo-id", string? upstream = null) =>
        NtpJson.Deserialize<SaveReply>(await _plugin.InvokeAsync(NtpServerMethods.Save, NtpJson.Serialize(new SaveRequest(enabled, interfaceId, upstream)), CancellationToken.None));

    private async Task<NtpState> StateAsync() =>
        NtpJson.Deserialize<NtpState>(await _plugin.InvokeAsync(NtpServerMethods.GetState, null, CancellationToken.None));

    [Fact]
    public async Task Stopped_by_default()
    {
        var state = await StateAsync();
        Assert.False(state.Config.Enabled);
        Assert.Equal("Stopped", state.Status.Text);
        Assert.Equal(NtpStatusInfo.Neutral, state.Status.Kind);
        Assert.Equal(["all", "lo-id", "eth-id"], state.Interfaces.Select(i => i.Id));
        Assert.Empty(Service.Endpoints);
    }

    [Fact]
    public async Task A_client_on_loopback_gets_a_valid_mode_4_answer_it_accepts()
    {
        var reply = await SaveAsync();
        Assert.True(reply.Saved);
        Assert.Equal(NtpStatusInfo.Ok, reply.State.Status.Kind);
        Assert.Equal($"Running on 127.0.0.1:{Endpoint.Port}", reply.State.Status.Text);
        Assert.NotEqual(123, Endpoint.Port);

        // Our own SNTP client: it validates mode, origin, LI, stratum and timestamps like a camera does.
        var sample = await UpstreamClient.QueryAsync(Endpoint, TimeSpan.FromSeconds(2), TimeProvider.System, CancellationToken.None);
        Assert.Equal(10, sample.Stratum);
        Assert.InRange(Math.Abs(sample.OffsetSeconds), 0, 0.05);
        Assert.InRange(sample.DelaySeconds, 0, 0.5);

        // The raw answer: LI 0 (synchronized), stratum 10, LOCL, valid timestamps.
        var (answer, origin, t1, t4) = await NtpProbe.QueryAsync(Endpoint, TimeSpan.FromSeconds(2));
        Assert.NotNull(answer);
        var a = answer.Value;
        Assert.Equal(NtpMode.Server, a.Mode);
        Assert.Equal(NtpLeap.NoWarning, a.Leap);
        Assert.Equal(10, a.Stratum);
        Assert.Equal("LOCL", NtpPacket.AsciiCode(a.ReferenceId));
        Assert.Equal(origin, a.OriginateTimestamp);
        var t2 = NtpTimestamp.ToDateTime(a.ReceiveTimestamp, t1);
        var t3 = NtpTimestamp.ToDateTime(a.TransmitTimestamp, t1);
        Assert.InRange(t2, t1.AddMilliseconds(-50), t4.AddMilliseconds(50));
        Assert.True(t3 >= t2);
        Assert.InRange(t3, t1.AddMilliseconds(-50), t4.AddMilliseconds(50));

        // Logged, newest first, with the client address.
        await Wait.UntilAsync(() => Service.Log.LastSeq >= 2);
        var entry = (await StateAsync()).Requests[0];
        Assert.Equal("127.0.0.1", entry.Client);
        Assert.Equal(RequestEntry.Answered, entry.Result);
        Assert.NotNull(entry.OffsetMilliseconds);
    }

    [Fact]
    public async Task Requests_are_pushed_live_to_the_page_watchers()
    {
        await SaveAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = new List<PluginEvent>();
        var watching = Task.Run(async () =>
        {
            await foreach (var item in _hub.WatchAsync(NtpServerPluginInfo.PluginId, cts.Token))
            {
                lock (received)
                {
                    received.Add(item);
                }

                if (item.Topic == NtpServerMethods.RequestsTopic)
                {
                    return;
                }
            }
        });
        await Wait.UntilAsync(() => _hub.WatcherCount(NtpServerPluginInfo.PluginId) == 1);

        await NtpProbe.QueryAsync(Endpoint, TimeSpan.FromSeconds(2));
        await watching;
        var requests = received.Single(e => e.Topic == NtpServerMethods.RequestsTopic);
        var entries = NtpJson.Deserialize<RequestsEvent>(requests.PayloadJson).Entries;
        Assert.Equal("127.0.0.1", Assert.Single(entries).Client);
    }

    [Fact]
    public async Task Control_and_malformed_packets_get_no_answer()
    {
        await SaveAsync();
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var control = new byte[NtpPacket.Length];
        new NtpPacket { Version = 2, Mode = NtpMode.Control, TransmitTimestamp = 1 }.Write(control);
        await socket.SendToAsync(control, SocketFlags.None, Endpoint);
        await socket.SendToAsync(new byte[] { 0x1B, 0, 0 }, SocketFlags.None, Endpoint); // short
        var monlist = new byte[8];
        monlist[0] = 0x17; // mode 7 (ntpd monlist)
        await socket.SendToAsync(monlist, SocketFlags.None, Endpoint);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await socket.ReceiveFromAsync(new byte[1024], SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cts.Token));
        Assert.Equal(0, Service.Log.LastSeq);
    }

    [Fact]
    public async Task Rate_limited_clients_get_one_kiss_of_death_and_one_log_entry()
    {
        await SaveAsync();
        var answers = new List<NtpPacket>();
        for (var i = 0; i < 12; i++)
        {
            var (answer, _, _, _) = await NtpProbe.QueryAsync(Endpoint, TimeSpan.FromMilliseconds(200));
            if (answer is { } a)
            {
                answers.Add(a);
            }
        }

        Assert.Equal(8, answers.Count(a => a.Stratum == 10));
        Assert.Single(answers, a => a.Stratum == 0 && NtpPacket.AsciiCode(a.ReferenceId) == "RATE");
        var log = Service.Log.Snapshot();
        Assert.Equal(8, log.Count(e => e.Result == RequestEntry.Answered));
        Assert.Single(log, e => e.Result == RequestEntry.RateLimited);
    }

    [Fact]
    public async Task Requests_are_answered_within_a_few_ms_while_the_upstream_hangs()
    {
        await using var upstream = new FakeUpstream(UpstreamBehavior.Answer);
        Assert.True((await SaveAsync(upstream: upstream.HostText)).Saved);
        upstream.Behavior = UpstreamBehavior.NoAnswer;

        // From now on every upstream query waits for its full timeout; serving must not care.
        var slowest = TimeSpan.Zero;
        for (var i = 0; i < 6; i++)
        {
            var watch = Stopwatch.StartNew();
            var (answer, _, _, _) = await NtpProbe.QueryAsync(Endpoint, TimeSpan.FromSeconds(1));
            watch.Stop();
            Assert.NotNull(answer);
            Assert.Equal(NtpLeap.NoWarning, answer.Value.Leap);
            slowest = watch.Elapsed > slowest ? watch.Elapsed : slowest;
            await Task.Delay(150);
        }

        Assert.True(slowest < TimeSpan.FromMilliseconds(100), $"slowest answer took {slowest.TotalMilliseconds} ms");
    }

    [Fact]
    public async Task Upstream_on_save_is_validated_then_served_and_an_unreachable_one_falls_back_with_a_warning()
    {
        await using var upstream = new FakeUpstream();
        var reply = await SaveAsync(upstream: upstream.HostText);
        Assert.True(reply.Saved);
        Assert.StartsWith("Stratum 2, offset", reply.UpstreamResult, StringComparison.Ordinal);
        Assert.Equal(3, reply.State.Stratum);
        Assert.True(reply.State.Upstream!.Reachable);

        var (answer, _, _, _) = await NtpProbe.QueryAsync(Endpoint, TimeSpan.FromSeconds(1));
        Assert.Equal(3, answer!.Value.Stratum);
        Assert.Equal(0x7F000001u, answer.Value.ReferenceId);

        upstream.Behavior = UpstreamBehavior.NoAnswer;
        await Wait.UntilAsync(() => Service.GetState(false).Status.Kind == NtpStatusInfo.Warning, TimeSpan.FromSeconds(10));
        var state = Service.GetState(false);
        Assert.Equal($"Upstream {upstream.HostText} does not answer, using this computer's time", state.Status.Text);
        Assert.Equal(10, state.Stratum);
        (answer, _, _, _) = await NtpProbe.QueryAsync(Endpoint, TimeSpan.FromSeconds(1));
        Assert.Equal(10, answer!.Value.Stratum);
        Assert.Equal(NtpLeap.NoWarning, answer.Value.Leap);

        upstream.Behavior = UpstreamBehavior.Answer;
        await Wait.UntilAsync(() => Service.GetState(false).Stratum == 3, TimeSpan.FromSeconds(10));
        Assert.Equal(NtpStatusInfo.Ok, Service.GetState(false).Status.Kind);
    }

    [Fact]
    public async Task A_dead_or_invalid_upstream_is_not_saved_and_the_server_keeps_running()
    {
        await SaveAsync();
        await using var dead = new FakeUpstream(UpstreamBehavior.NoAnswer);
        var reply = await SaveAsync(upstream: dead.HostText);
        Assert.False(reply.Saved);
        Assert.Equal("127.0.0.1: No answer within 300 ms.", reply.UpstreamError);
        Assert.Null(Service.Config.Upstream);
        Assert.NotNull((await NtpProbe.QueryAsync(Endpoint, TimeSpan.FromSeconds(1))).Answer);

        reply = await SaveAsync(upstream: "not a host");
        Assert.False(reply.Saved);
        Assert.Equal("Enter one host name or IP address.", reply.UpstreamError);

        _resolver.Answer = _ => null;
        reply = await SaveAsync(upstream: "time.invalid");
        Assert.Equal("time.invalid: time.invalid could not be resolved.", reply.UpstreamError);
    }

    [Fact]
    public async Task Clock_offset_over_one_second_is_a_warning()
    {
        await using var upstream = new FakeUpstream { ClockOffset = TimeSpan.FromSeconds(3.2) };
        var reply = await SaveAsync(upstream: upstream.HostText);
        Assert.Equal(NtpStatusInfo.Warning, reply.State.Status.Kind);
        Assert.Equal("Server clock differs from upstream by 3.2 s", reply.State.Status.Text);
    }

    [Fact]
    public async Task Settings_are_persisted_and_restored_on_server_start()
    {
        await SaveAsync();
        await _plugin.StopAsync(CancellationToken.None);

        await using var restarted = new NtpServerPlugin(Options.Test(_resolver));
        await restarted.StartAsync(new TestCoreContext(_settings.GetSettings(NtpServerPluginInfo.PluginId), null), CancellationToken.None);
        var state = restarted.Service!.GetState(full: false);
        Assert.True(state.Config.Enabled);
        Assert.Equal("lo-id", state.Config.InterfaceId);
        Assert.Equal("Loopback", state.Config.InterfaceName);
        Assert.Equal(NtpStatusInfo.Ok, state.Status.Kind);
        Assert.NotNull((await NtpProbe.QueryAsync(restarted.Service.Endpoints[0], TimeSpan.FromSeconds(1))).Answer);
    }

    [Fact]
    public async Task Port_in_use_is_an_error_and_retried_until_free()
    {
        var blocker = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        blocker.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)blocker.LocalEndPoint!).Port;

        await using var plugin = new NtpServerPlugin(Options.Test() with { Port = port });
        await plugin.StartAsync(new TestCoreContext(new InMemoryPluginSettingsProvider().GetSettings(NtpServerPluginInfo.PluginId), null), CancellationToken.None);
        var reply = NtpJson.Deserialize<SaveReply>(await plugin.InvokeAsync(NtpServerMethods.Save, NtpJson.Serialize(new SaveRequest(true, "lo-id", null)), CancellationToken.None));
        Assert.True(reply.Saved);
        Assert.Equal(NtpStatusInfo.Error, reply.State.Status.Kind);
        Assert.Equal($"Port {port} is in use by another program", reply.State.Status.Text);

        blocker.Dispose();
        await Wait.UntilAsync(() => plugin.Service!.GetState(false).Status.Kind == NtpStatusInfo.Ok);
        Assert.Equal($"Running on 127.0.0.1:{port}", plugin.Service!.GetState(false).Status.Text);
    }

    [Fact]
    public async Task A_missing_interface_is_an_error()
    {
        await SaveAsync(interfaceId: "eth-id");
        Assert.Equal(NtpStatusInfo.Error, Service.GetState(false).Status.Kind); // 192.0.2.17 does not exist on this machine
        Assert.Equal("Interface Ethernet is not available", Service.GetState(false).Status.Text);

        await Assert.ThrowsAsync<ArgumentException>(() => SaveAsync(interfaceId: "gone-id"));
        await Assert.ThrowsAsync<ArgumentException>(() => _plugin.InvokeAsync("nope", null, CancellationToken.None));
    }

    [Fact]
    public async Task Disable_stops_serving()
    {
        await SaveAsync();
        var endpoint = Endpoint;
        await SaveAsync(enabled: false);
        Assert.Equal("Stopped", Service.GetState(false).Status.Text);
        Assert.Null((await NtpProbe.QueryAsync(endpoint, TimeSpan.FromMilliseconds(300))).Answer);
    }
}
