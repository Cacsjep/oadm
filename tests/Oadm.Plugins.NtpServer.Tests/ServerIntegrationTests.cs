using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

using Oadm.Core.Plugins;
using Oadm.Plugins.NtpServer.Protocol;
using Oadm.Plugins.NtpServer.Upstream;
using Oadm.Sdk.Network;
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

    /// <summary>Generous wait for an answer that must come: a busy machine may answer loopback slowly.</summary>
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(30);

    private InMemoryPluginSettingsProvider _restartSettings = new();

    /// <summary>
    /// Restarts the plugin with other options (rate limit, upstream timeouts); with <paramref name="keepSettings"/> it keeps
    /// what was saved since the previous restart.
    /// </summary>
    private async Task RestartWithAsync(NtpServerOptions options, bool keepSettings = false)
    {
        await _plugin.DisposeAsync();
        if (!keepSettings)
        {
            _restartSettings = new InMemoryPluginSettingsProvider();
        }

        _plugin = new NtpServerPlugin(options);
        await _plugin.StartAsync(new TestCoreContext(_restartSettings.GetSettings(NtpServerPluginInfo.PluginId), _hub.For(NtpServerPluginInfo.PluginId)), CancellationToken.None);
    }

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
        Assert.Equal(ServiceStatus.Neutral, state.Status.Kind);
        Assert.Equal(["all", "lo-id", "eth-id"], state.Interfaces.Select(i => i.Id));
        Assert.Empty(Service.Endpoints);
    }

    [Fact]
    public async Task A_client_on_loopback_gets_a_valid_mode_4_answer_it_accepts()
    {
        var reply = await SaveAsync();
        Assert.True(reply.Saved);
        Assert.Equal(ServiceStatus.Ok, reply.State.Status.Kind);
        Assert.Equal($"Running on 127.0.0.1:{Endpoint.Port}", reply.State.Status.Text);
        Assert.NotEqual(123, Endpoint.Port);

        // Our own SNTP client: it validates mode, origin, LI, stratum and timestamps like a camera does.
        var sample = await UpstreamClient.QueryAsync(Endpoint, AnswerTimeout, TimeProvider.System, CancellationToken.None);
        Assert.Equal(10, sample.Stratum);
        // Same clock on both sides: the offset error is at most half the round trip (slow on a busy machine).
        Assert.InRange(Math.Abs(sample.OffsetSeconds), 0, (sample.DelaySeconds / 2) + 0.05);
        Assert.InRange(sample.DelaySeconds, 0, AnswerTimeout.TotalSeconds);

        // The raw answer: LI 0 (synchronized), stratum 10, LOCL, valid timestamps.
        var (answer, origin, t1, t4) = await NtpProbe.QueryAsync(Endpoint, AnswerTimeout);
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
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

        await NtpProbe.QueryAsync(Endpoint, AnswerTimeout);
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

        // Then a good request: the server reads in order, so its answer is the first and only one that comes back.
        var request = new byte[NtpPacket.Length];
        new NtpPacket { Version = 4, Mode = NtpMode.Client, Poll = 6, TransmitTimestamp = 42 }.Write(request);
        await socket.SendToAsync(request, SocketFlags.None, Endpoint);
        using var cts = new CancellationTokenSource(AnswerTimeout);
        var buffer = new byte[1024];
        var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cts.Token);
        Assert.True(NtpPacket.TryRead(buffer.AsSpan(0, received.ReceivedBytes), out var answer));
        Assert.Equal(NtpMode.Server, answer.Mode);
        Assert.Equal(42ul, answer.OriginateTimestamp);
        await Wait.UntilAsync(() => Service.Log.LastSeq >= 1);
        Assert.Equal(1, Service.Log.LastSeq); // only the good request is in the log
        Assert.Equal(RequestEntry.Answered, Assert.Single(Service.Log.Snapshot()).Result);
    }

    [Fact]
    public async Task Rate_limited_clients_get_one_kiss_of_death_and_one_log_entry()
    {
        // No refill during the test, however long the queries take: 8 answers, one RATE, then silence.
        await RestartWithAsync(Options.Test(_resolver) with { RateLimit = new() { RefillSeconds = 3600 } });
        await SaveAsync();
        var answers = new List<NtpPacket>();
        for (var i = 0; i < 12; i++)
        {
            // The first 9 are answered (8 and the Kiss-o'-Death); the dropped ones get a short wait.
            var (answer, _, _, _) = await NtpProbe.QueryAsync(Endpoint, i < 9 ? AnswerTimeout : TimeSpan.FromMilliseconds(200));
            if (answer is { } a)
            {
                answers.Add(a);
            }
        }

        Assert.Equal(8, answers.Count(a => a.Stratum == 10));
        Assert.Single(answers, a => a.Stratum == 0 && NtpPacket.AsciiCode(a.ReferenceId) == "RATE");
        await Wait.UntilAsync(() => Service.Log.Snapshot().Count(e => e.Result == RequestEntry.Answered) == 8);
        var log = Service.Log.Snapshot();
        Assert.Equal(8, log.Count(e => e.Result == RequestEntry.Answered));
        Assert.Single(log, e => e.Result == RequestEntry.RateLimited);
    }

    [Fact]
    public async Task Requests_are_answered_while_the_upstream_hangs()
    {
        await AnswerWhileTheUpstreamHangsAsync();
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task Requests_are_answered_within_a_few_ms_while_the_upstream_hangs()
    {
        var slowest = await AnswerWhileTheUpstreamHangsAsync();
        // Measured below 10 ms; 1 s leaves room for a busy machine.
        Assert.True(slowest < TimeSpan.FromSeconds(1), $"slowest answer took {slowest.TotalMilliseconds} ms");
    }

    /// <summary>
    /// The upstream takes the Save query, then never answers again (its next query waits 10 minutes): every request is
    /// still answered at once with the upstream's state. Returns the slowest answer.
    /// </summary>
    private async Task<TimeSpan> AnswerWhileTheUpstreamHangsAsync()
    {
        await RestartWithAsync(Options.Test(_resolver) with { Upstream = Options.FastUpstream with { QueryTimeout = TimeSpan.FromMinutes(10) } });
        await using var upstream = new FakeUpstream(UpstreamBehavior.Answer);
        Assert.True((await SaveAsync(upstream: upstream.HostText)).Saved);
        upstream.Behavior = UpstreamBehavior.NoAnswer;
        await Wait.UntilAsync(() => upstream.Requests >= 2); // the monitor's next query waits now

        var slowest = TimeSpan.Zero;
        for (var i = 0; i < 6; i++)
        {
            var watch = Stopwatch.StartNew();
            var (answer, _, _, _) = await NtpProbe.QueryAsync(Endpoint, AnswerTimeout);
            watch.Stop();
            Assert.NotNull(answer);
            Assert.Equal(NtpLeap.NoWarning, answer.Value.Leap);
            Assert.Equal(3, answer.Value.Stratum);
            slowest = watch.Elapsed > slowest ? watch.Elapsed : slowest;
        }

        Assert.Equal(2, upstream.Requests);
        return slowest;
    }

    [Fact]
    public async Task Upstream_on_save_is_validated_then_served_and_an_unreachable_one_falls_back_with_a_warning()
    {
        // A 30 s query timeout: the Save query and the polls get their answers however busy the machine is.
        await RestartWithAsync(Options.Test(_resolver) with { Upstream = Options.FastUpstream with { QueryTimeout = AnswerTimeout } });
        await using var upstream = new FakeUpstream();
        var reply = await SaveAsync(upstream: upstream.HostText);
        Assert.True(reply.Saved);
        Assert.Matches(@"^.+ off, \d+ ms round trip$", reply.UpstreamResult);
        Assert.Equal(3, reply.State.Stratum);
        Assert.True(reply.State.Upstream!.Reachable);

        var (answer, _, _, _) = await NtpProbe.QueryAsync(Endpoint, AnswerTimeout);
        Assert.Equal(3, answer!.Value.Stratum);
        Assert.Equal(0x7F000001u, answer.Value.ReferenceId);

        // The saved upstream with the short timeout, so a dead upstream falls back quickly: served again first, then dead.
        await RestartWithAsync(Options.Test(_resolver) with { Upstream = Options.FastUpstream with { FailuresBeforeUnreachable = 2 } }, keepSettings: true);
        await Wait.UntilAsync(() => Service.GetState(false).Stratum == 3, TimeSpan.FromSeconds(60));
        upstream.Behavior = UpstreamBehavior.NoAnswer;
        await Wait.UntilAsync(() => Service.GetState(false).Status.Kind == ServiceStatus.Warning, TimeSpan.FromSeconds(60));
        var state = Service.GetState(false);
        Assert.Equal($"Upstream {upstream.HostText} does not answer, using this computer's time", state.Status.Text);
        Assert.Equal(10, state.Stratum);
        (answer, _, _, _) = await NtpProbe.QueryAsync(Endpoint, AnswerTimeout);
        Assert.Equal(10, answer!.Value.Stratum);
        Assert.Equal(NtpLeap.NoWarning, answer.Value.Leap);

        upstream.Behavior = UpstreamBehavior.Answer;
        // A poll stalled twice by the machine may fall back again for a moment: the good state is waited for as a whole.
        await Wait.UntilAsync(() => Service.GetState(false) is { Stratum: 3, Status.Kind: ServiceStatus.Ok }, TimeSpan.FromSeconds(60));
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
        Assert.NotNull((await NtpProbe.QueryAsync(Endpoint, AnswerTimeout)).Answer);

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
        await RestartWithAsync(Options.Test(_resolver) with { Upstream = Options.FastUpstream with { QueryTimeout = AnswerTimeout } });
        await using var upstream = new FakeUpstream { ClockOffset = TimeSpan.FromSeconds(3.2) };
        var reply = await SaveAsync(upstream: upstream.HostText);
        Assert.Equal(ServiceStatus.Warning, reply.State.Status.Kind);
        // 3.2 s on an idle machine; a busy one answers loopback unevenly (the offset error is half the asymmetry).
        var offset = reply.State.Upstream!.OffsetMilliseconds!.Value / 1000;
        Assert.InRange(offset, 2.2, 4.2);
        Assert.Matches(@"^Server clock differs from upstream by \d\.\d s$", reply.State.Status.Text);
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
        Assert.Equal(ServiceStatus.Ok, state.Status.Kind);
        Assert.NotNull((await NtpProbe.QueryAsync(restarted.Service.Endpoints[0], AnswerTimeout)).Answer);
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
        Assert.Equal(ServiceStatus.Error, reply.State.Status.Kind);
        Assert.Equal($"Port {port} is used by another program.", reply.State.Status.Text);

        blocker.Dispose();
        await Wait.UntilAsync(() => plugin.Service!.GetState(false).Status.Kind == ServiceStatus.Ok);
        Assert.Equal($"Running on 127.0.0.1:{port}", plugin.Service!.GetState(false).Status.Text);
    }

    [Fact]
    public async Task A_missing_interface_is_an_error()
    {
        await SaveAsync(interfaceId: "eth-id");
        Assert.Equal(ServiceStatus.Error, Service.GetState(false).Status.Kind); // 192.0.2.17 does not exist on this machine
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
