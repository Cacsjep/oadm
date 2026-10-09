using System.Collections.Concurrent;
using System.Diagnostics;

using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Plugins;
using Oadm.Plugins.DhcpServer.Leases;
using Oadm.Plugins.DhcpServer.Protocol;
using Oadm.Plugins.DhcpServer.Serving;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.DhcpServer.Tests;

/// <summary>"Automatically add Axis devices that get an address" and following managed devices, with a fake server side.</summary>
public sealed class AutoAddTests
{
    private static readonly ulong Axis1 = Mac.Of("B8:A4:4F:63:13:39");
    private static readonly ulong Axis2 = Mac.Of("AC:CC:8E:5F:60:71");
    private static readonly ulong Axis3 = Mac.Of("00:40:8C:12:34:56");
    private static readonly ulong Axis4 = Mac.Of("E8:27:25:AB:CD:EF");
    private static readonly ulong Other = Mac.Of("3C:52:82:11:22:33");

    [Fact]
    public void The_axis_blocks_are_known_and_others_are_not()
    {
        Assert.True(AxisOui.IsAxis(Axis1));
        Assert.True(AxisOui.IsAxis(Axis2));
        Assert.True(AxisOui.IsAxis(Axis3));
        Assert.True(AxisOui.IsAxis(Axis4));
        Assert.False(AxisOui.IsAxis(Other));
        Assert.Equal("B8A44F631339", AxisOui.Serial(Axis1));
    }

    [Fact]
    public async Task An_unmanaged_axis_device_is_added_once_per_lease_change()
    {
        var fake = new FakeAutoAdd();
        await using var adder = new DhcpDeviceAutoAdd(fake, () => true);

        Assert.True(adder.OnLeased(Axis1, Ip.Of("10.0.0.100")));
        await adder.WhenIdleAsync(Timeout());
        Assert.False(adder.OnLeased(Axis1, Ip.Of("10.0.0.100"))); // renewal of the same lease
        await adder.WhenIdleAsync(Timeout());

        Assert.Equal([("follow", "B8A44F631339", "10.0.0.100"), ("add", "B8A44F631339", "10.0.0.100")], fake.Calls.ToArray());

        // A new address is a lease change: handled again.
        Assert.True(adder.OnLeased(Axis1, Ip.Of("10.0.0.101")));
        await adder.WhenIdleAsync(Timeout());
        Assert.Equal(4, fake.Calls.Count);
        Assert.All(fake.Sources, s => Assert.Equal(DhcpDeviceAutoAdd.Source, s));
    }

    [Fact]
    public async Task Non_axis_devices_are_never_handed_to_the_server()
    {
        var fake = new FakeAutoAdd();
        await using var adder = new DhcpDeviceAutoAdd(fake, () => true);

        Assert.False(adder.OnLeased(Other, Ip.Of("10.0.0.100")));
        await adder.WhenIdleAsync(Timeout());

        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task Setting_off_adds_nothing_but_managed_devices_are_still_followed()
    {
        var fake = new FakeAutoAdd();
        fake.Managed["ACCC8E5F6071"] = DeviceFollowResult.Moved;
        await using var adder = new DhcpDeviceAutoAdd(fake, () => false);

        adder.OnLeased(Axis1, Ip.Of("10.0.0.100"));
        adder.OnLeased(Axis2, Ip.Of("10.0.0.101"));
        await adder.WhenIdleAsync(Timeout());

        Assert.DoesNotContain(fake.Calls, c => c.Kind == "add");
        Assert.Contains(("follow", "ACCC8E5F6071", "10.0.0.101"), fake.Calls);
    }

    [Fact]
    public async Task Managed_devices_are_followed_and_never_added()
    {
        var fake = new FakeAutoAdd();
        fake.Managed["B8A44F631339"] = DeviceFollowResult.Unchanged;
        fake.Managed["ACCC8E5F6071"] = DeviceFollowResult.Moved;
        await using var adder = new DhcpDeviceAutoAdd(fake, () => true);

        adder.OnLeased(Axis1, Ip.Of("10.0.0.48"));
        adder.OnLeased(Axis2, Ip.Of("10.0.0.120"));
        await adder.WhenIdleAsync(Timeout());

        Assert.DoesNotContain(fake.Calls, c => c.Kind == "add");
        Assert.Equal(2, fake.Calls.Count(c => c.Kind == "follow"));
    }

    [Fact]
    public async Task An_unreachable_device_is_tried_once_more_after_the_retry_delay()
    {
        var time = Clock.New();
        var fake = new FakeAutoAdd { AddResult = DeviceAutoAddResult.Unreachable };
        await using var adder = new DhcpDeviceAutoAdd(fake, () => true, new DeviceAutoAddOptions { RetryDelay = TimeSpan.FromSeconds(30) }, time);

        adder.OnLeased(Axis1, Ip.Of("10.0.0.100"));
        await Wait.UntilAsync(() => adder.WaitingRetries == 1);
        Assert.Equal(1, fake.Calls.Count(c => c.Kind == "add"));
        Assert.Equal(1, adder.Pending); // waiting for the retry

        time.Advance(TimeSpan.FromSeconds(29));
        await Task.Delay(100);
        Assert.Equal(1, fake.Calls.Count(c => c.Kind == "add"));

        time.Advance(TimeSpan.FromSeconds(1));
        await adder.WhenIdleAsync(Timeout());
        Assert.Equal(2, fake.Calls.Count(c => c.Kind == "add"));

        // Still unreachable: no third attempt.
        time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(100);
        Assert.Equal(2, fake.Calls.Count(c => c.Kind == "add"));
    }

    [Fact]
    public async Task A_moved_device_that_does_not_answer_yet_is_verified_again_after_the_retry_delay()
    {
        var time = Clock.New();
        var fake = new FakeAutoAdd();
        fake.Managed["B8A44F631339"] = DeviceFollowResult.NotVerified;
        await using var adder = new DhcpDeviceAutoAdd(fake, () => true, new DeviceAutoAddOptions { RetryDelay = TimeSpan.FromSeconds(30) }, time);

        adder.OnLeased(Axis1, Ip.Of("10.0.0.130"));
        await Wait.UntilAsync(() => adder.WaitingRetries == 1);
        fake.Managed["B8A44F631339"] = DeviceFollowResult.Moved;
        time.Advance(TimeSpan.FromSeconds(30));
        await adder.WhenIdleAsync(Timeout());

        Assert.Equal(2, fake.Calls.Count(c => c.Kind == "follow"));
        Assert.DoesNotContain(fake.Calls, c => c.Kind == "add");
    }

    [Fact]
    public async Task A_retry_is_dropped_when_the_lease_changed_meanwhile()
    {
        var time = Clock.New();
        var fake = new FakeAutoAdd { AddResult = DeviceAutoAddResult.Unreachable };
        await using var adder = new DhcpDeviceAutoAdd(fake, () => true, new DeviceAutoAddOptions { RetryDelay = TimeSpan.FromSeconds(30) }, time);

        adder.OnLeased(Axis1, Ip.Of("10.0.0.100"));
        await Wait.UntilAsync(() => adder.WaitingRetries == 1);
        fake.AddResult = DeviceAutoAddResult.Added;
        adder.OnLeased(Axis1, Ip.Of("10.0.0.101"));
        await Wait.UntilAsync(() => fake.Calls.Count(c => c.Kind == "add") == 2 && adder.Pending == 1);
        time.Advance(TimeSpan.FromSeconds(30));
        await adder.WhenIdleAsync(Timeout());

        Assert.Equal(["10.0.0.100", "10.0.0.101"], fake.Calls.Where(c => c.Kind == "add").Select(c => c.Address));
    }

    [Fact]
    [Trait("Category", "Timing")] // the peak is reached only while 100 ms adds overlap
    public async Task At_most_four_devices_are_handled_at_the_same_time()
    {
        var fake = new FakeAutoAdd { Latency = TimeSpan.FromMilliseconds(100) };
        await using var adder = new DhcpDeviceAutoAdd(fake, () => true);

        for (var i = 0; i < 20; i++)
        {
            adder.OnLeased(0xB8A44F000000UL + (ulong)i, Ip.Of("10.0.0.100") + (uint)i);
        }

        await adder.WhenIdleAsync(Timeout(20));

        Assert.Equal(20, fake.Calls.Count(c => c.Kind == "add"));
        Assert.Equal(4, adder.PeakParallel);
        Assert.Equal(4, fake.PeakConcurrent);
    }

    [Fact]
    public async Task A_failing_server_side_never_stops_the_queue()
    {
        var fake = new FakeAutoAdd { Throw = true };
        await using var adder = new DhcpDeviceAutoAdd(fake, () => true);

        adder.OnLeased(Axis1, Ip.Of("10.0.0.100"));
        await adder.WhenIdleAsync(Timeout());
        fake.Throw = false;
        adder.OnLeased(Axis2, Ip.Of("10.0.0.101"));
        await adder.WhenIdleAsync(Timeout());

        Assert.Contains(("add", "ACCC8E5F6071", "10.0.0.101"), fake.Calls);
    }

    [Fact]
    [Trait("Category", "Timing")] // time budget
    public async Task The_engine_reports_every_ack_and_never_waits_for_the_server_side()
    {
        var fake = new FakeAutoAdd { Block = new TaskCompletionSource() };
        var store = new LeaseStore();
        using var engine = new DhcpEngine(store, new FakeProbe(), new DhcpEngineOptions(), Clock.New())
        {
            Scope = DhcpScope.Create(new DhcpNetworkInfo("eth-id", "Ethernet", "10.0.0.17", 24, null, [], null), Ip.Of("10.0.0.100"), Ip.Of("10.0.0.199")),
        };
        await using var adder = new DhcpDeviceAutoAdd(fake, () => true);
        engine.Leased = (mac, address) => adder.OnLeased(mac, address);

        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++)
        {
            var mac = 0xB8A44F100000UL + (ulong)i;
            var offer = await engine.HandleAsync(new DhcpMessage { Op = DhcpMessage.BootRequest, TransactionId = (uint)i, MessageType = DhcpMessageType.Discover, Mac = mac }, default);
            var ack = await engine.HandleAsync(
                new DhcpMessage { Op = DhcpMessage.BootRequest, TransactionId = (uint)i, MessageType = DhcpMessageType.Request, Mac = mac, ServerId = offer!.Message.ServerId, RequestedAddress = offer.Message.YourAddress },
                default);
            Assert.Equal(DhcpMessageType.Ack, ack!.Message.MessageType);
        }

        // Ten ACKs while every automatic add hangs: the engine answered at once.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), watch.Elapsed.ToString());
        await Wait.UntilAsync(() => fake.Calls.Count(c => c.Kind == "add") == 4);
        Assert.Equal(10, adder.Pending);
        fake.Block.SetResult();
        await adder.WhenIdleAsync(Timeout());
        Assert.Equal(10, fake.Calls.Count(c => c.Kind == "add"));
    }

    [Fact]
    public async Task The_whole_server_adds_a_device_that_gets_a_lease_when_the_setting_is_on()
    {
        var network = new FakeDhcpNetwork();
        var fake = new FakeAutoAdd();
        var settings = new InMemoryPluginSettingsProvider();
        var hub = new PluginEventHub();
        await using var plugin = new DhcpServerPlugin(Options.Test(network));
        await plugin.StartAsync(new TestCoreContext(settings.GetSettings(DhcpServerPluginInfo.PluginId), hub.For(DhcpServerPluginInfo.PluginId), autoAdd: fake), default);

        // Off (default): an Axis camera gets its address, nothing is added.
        var reply = DhcpJson.Deserialize<DhcpSaveReply>(await plugin.InvokeAsync(DhcpServerMethods.Save, DhcpJson.Serialize(Options.Enable()), default));
        Assert.True(reply.Saved);
        Assert.False(reply.State.Config.AutoAddAxisDevices);
        await using (var camera = new TestClient(network, Axis1))
        {
            Assert.Equal(Ip.Of("10.0.0.100"), await camera.AcquireAsync());
        }

        await plugin.Service!.AutoAdd!.WhenIdleAsync(Timeout());
        Assert.DoesNotContain(fake.Calls, c => c.Kind == "add");

        // On: saved, restored on the page, and the next Axis device is added; a PC is not.
        reply = DhcpJson.Deserialize<DhcpSaveReply>(await plugin.InvokeAsync(DhcpServerMethods.Save, DhcpJson.Serialize(Options.Enable() with { AutoAddAxisDevices = true }), default));
        Assert.True(reply.Saved);
        Assert.True(reply.State.Config.AutoAddAxisDevices);
        var stored = DhcpJson.Deserialize<DhcpConfig>(await settings.GetSettings(DhcpServerPluginInfo.PluginId).GetAsync(DhcpServerService.ConfigKey, default));
        Assert.True(stored.AutoAddAxisDevices);

        await using (var camera = new TestClient(network, Axis2))
        {
            Assert.Equal(Ip.Of("10.0.0.101"), await camera.AcquireAsync());
        }

        await using (var pc = new TestClient(network, Other))
        {
            Assert.NotNull(await pc.AcquireAsync());
        }

        await Wait.UntilAsync(() => fake.Calls.Any(c => c.Kind == "add"));
        await plugin.Service!.AutoAdd!.WhenIdleAsync(Timeout());
        Assert.Equal([("add", "ACCC8E5F6071", "10.0.0.101")], fake.Calls.Where(c => c.Kind == "add").ToArray());
        Assert.DoesNotContain(fake.Calls, c => c.Serial == "3C5282112233");
    }

    private static CancellationToken Timeout(int seconds = 5) => new CancellationTokenSource(TimeSpan.FromSeconds(seconds)).Token;

    /// <summary>The server side: records calls, answers configurable results, can be slow, block or throw.</summary>
    private sealed class FakeAutoAdd : IDeviceAutoAdd
    {
        private int _concurrent;

        public ConcurrentQueue<(string Kind, string Serial, string Address)> Calls { get; } = new();

        public ConcurrentQueue<string> Sources { get; } = new();

        public ConcurrentDictionary<string, DeviceFollowResult> Managed { get; } = new();

        public DeviceAutoAddResult AddResult { get; set; } = DeviceAutoAddResult.Added;

        public TimeSpan Latency { get; init; }

        public TaskCompletionSource? Block { get; init; }

        public bool Throw { get; set; }

        public int PeakConcurrent { get; private set; }

        public Task<DeviceFollowResult> FollowAsync(string serial, string address, string source, CancellationToken ct)
        {
            Calls.Enqueue(("follow", serial, address));
            Sources.Enqueue(source);
            return Task.FromResult(Managed.TryGetValue(serial, out var result) ? result : DeviceFollowResult.NotManaged);
        }

        public async Task<DeviceAutoAddOutcome> AddAsync(string address, string expectedSerial, string source, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _concurrent);
            lock (this)
            {
                PeakConcurrent = Math.Max(PeakConcurrent, now);
            }

            try
            {
                Calls.Enqueue(("add", expectedSerial, address));
                if (Block is not null)
                {
                    await Block.Task.WaitAsync(ct);
                }

                if (Latency > TimeSpan.Zero)
                {
                    await Task.Delay(Latency, ct);
                }

                if (Throw)
                {
                    throw new InvalidOperationException("server side failed");
                }

                return new DeviceAutoAddOutcome(AddResult, AddResult == DeviceAutoAddResult.Added ? Guid.NewGuid() : null, AddResult.ToString());
            }
            finally
            {
                Interlocked.Decrement(ref _concurrent);
            }
        }
    }
}
