using System.Diagnostics;
using System.Globalization;

using Oadm.Plugins.Network.Model;
using Oadm.Sdk.Devices;

using Xunit.Abstractions;

using StepRun = Oadm.Sdk.Testing.StepRun;

namespace Oadm.Plugins.Network.Tests;

/// <summary>The device tasks of one run parse and validate the batch payload once (<see cref="NetworkRunCache"/>).</summary>
public sealed class RunCacheTests(ITestOutputHelper output)
{
    private static readonly ReachabilityOptions Fast =
        new(TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(60), TimeSpan.FromSeconds(30));

    private static string DnsPayload(params Guid[] ids) => new NetworkPayload
    {
        Dns = new DnsChange(false, ["10.0.0.2"]),
        Devices = ids.ToDictionary(id => id, _ => new DeviceAssignment()),
    }.ToJson();

    private static Task RunAsync(Sdk.Plugins.ITaskPlugin plugin, RecordingContext ctx, Guid id, string payload) =>
        StepRun.RunAsync(ctx.Steps, () => plugin.ExecuteAsync(ctx, new FakeDevice(id), payload, CancellationToken.None));

    [Fact]
    public async Task The_devices_of_a_run_share_one_parse_and_validation()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var payload = DnsPayload(a, b);
        var plugin = new NetworkSettingsTaskPlugin(Fast, TimeProvider.System, new FakeAddressProbe());
        var first = new FakeNetworkVapix();
        var second = new FakeNetworkVapix();

        await RunAsync(plugin, new RecordingContext(first), a, payload);
        await RunAsync(plugin, new RecordingContext(second), b, new string(payload.AsSpan())); // the same text, another string

        Assert.Equal(1, plugin.Runs.Parses);
        Assert.Single(first.Writes);
        Assert.Single(second.Writes);

        // Another run is another payload.
        var c = Guid.NewGuid();
        await RunAsync(plugin, new RecordingContext(new FakeNetworkVapix()), c, DnsPayload(c));
        Assert.Equal(2, plugin.Runs.Parses);
    }

    [Fact]
    public async Task An_invalid_payload_fails_every_device_with_the_same_text_and_is_validated_once()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var payload = new NetworkPayload
        {
            Dns = new DnsChange(false, ["not an address"]),
            Devices = new Dictionary<Guid, DeviceAssignment> { [a] = new(), [b] = new() },
        }.ToJson();
        var plugin = new NetworkSettingsTaskPlugin(Fast, TimeProvider.System, new FakeAddressProbe());
        var vapix = new FakeNetworkVapix();

        var first = await Assert.ThrowsAsync<NetworkValidationException>(() => RunAsync(plugin, new RecordingContext(vapix), a, payload));
        var second = await Assert.ThrowsAsync<NetworkValidationException>(() => RunAsync(plugin, new RecordingContext(vapix), b, payload));

        Assert.EndsWith("Nothing was changed.", first.Message, StringComparison.Ordinal);
        Assert.Equal(first.Message, second.Message);
        Assert.Equal(first.Errors, second.Errors);
        Assert.Equal(1, plugin.Runs.Parses);
        Assert.Empty(vapix.Sent);
        Assert.Equal(0, vapix.ApiListCalls);
    }

    [Fact]
    public async Task Assign_ip_keeps_its_own_validation()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        var payload = DnsPayload(a, b); // no IPv4 section: Network settings takes it, Assign IP address refuses it
        var plugin = new AssignIpTaskPlugin(Fast, TimeProvider.System, new FakeAddressProbe());
        var vapix = new FakeNetworkVapix();

        var first = await Assert.ThrowsAsync<NetworkValidationException>(() => RunAsync(plugin, new RecordingContext(vapix), a, payload));
        var second = await Assert.ThrowsAsync<NetworkValidationException>(() => RunAsync(plugin, new RecordingContext(vapix), b, payload));

        Assert.Contains("choose DHCP or an IP address range", first.Message, StringComparison.Ordinal);
        Assert.Equal(first.Message, second.Message);
        Assert.Equal(1, plugin.Runs.Parses);
        Assert.Empty(vapix.Sent);
    }

    [Fact]
    public void Tasks_that_start_together_wait_for_one_parse()
    {
        var ids = Enumerable.Range(0, 2000).Select(_ => Guid.NewGuid()).ToArray();
        var payload = DnsPayload(ids);
        var cache = new NetworkRunCache(TimeProvider.System, PayloadValidator.ThrowIfInvalid);

        var payloads = new NetworkPayload[32];
        Parallel.For(0, payloads.Length, i => payloads[i] = cache.For(payload));

        Assert.Equal(1, cache.Parses);
        Assert.All(payloads, p => Assert.Same(payloads[0], p));
        Assert.True(payloads[0].Devices.ContainsKey(ids[^1]));
    }

    [Fact]
    public void A_run_is_kept_ten_minutes_and_at_most_sixteen_runs_at_a_time()
    {
        var time = new ManualTime();
        var cache = new NetworkRunCache(time, PayloadValidator.ThrowIfInvalid);
        var payload = DnsPayload(Guid.NewGuid());

        var run = cache.For(payload);
        Assert.Same(run, cache.For(payload));
        time.Now += NetworkRunCache.KeepFor - TimeSpan.FromSeconds(1);
        Assert.Same(run, cache.For(payload));
        time.Now += TimeSpan.FromSeconds(1);
        Assert.NotSame(run, cache.For(payload)); // parsed again
        Assert.Equal(2, cache.Parses);

        for (var i = 0; i < 40; i++)
        {
            cache.For(DnsPayload(Guid.NewGuid()));
        }

        Assert.True(cache.Count <= NetworkRunCache.MaxRuns, $"{cache.Count} runs kept");
        Assert.Throws<NetworkValidationException>(() => cache.For(null));
        Assert.Throws<NetworkValidationException>(() => cache.For("{not json"));
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void A_run_on_100000_devices_validates_its_payload_once()
    {
        const int Count = DeviceLimits.MaxDevices;
        var ids = Enumerable.Range(0, Count).Select(_ => Guid.NewGuid()).ToArray();
        var payload = new NetworkPayload
        {
            Ipv4 = new Ipv4Change(Ipv4Mode.Dhcp),
            HostName = new HostNameChange(false),
            Devices = ids.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => new DeviceAssignment(HostName: string.Create(CultureInfo.InvariantCulture, $"cam-{p.i}"))),
        }.ToJson();
        var cache = new NetworkRunCache(TimeProvider.System, PayloadValidator.ThrowIfInvalid);

        var watch = Stopwatch.StartNew();
        var parsed = cache.For(payload);
        var first = watch.ElapsedMilliseconds;

        watch.Restart();
        for (var i = 1; i < Count; i++)
        {
            Assert.Same(parsed, cache.For(payload));
        }

        var rest = watch.ElapsedMilliseconds;
        Assert.Equal("cam-99999", parsed.Devices[ids[^1]].HostName);
        output.WriteLine($"Network run cache, {Count:N0} devices: first task {first} ms, the other {Count - 1:N0} tasks {rest} ms");
        Assert.Equal(1, cache.Parses);
        // Measured: the first task 566 ms (before, every task paid it twice), the other 99,999 together 84 ms (found by
        // reference, the payload text is never hashed). Budgets about 10x.
        Assert.True(first < 6_000, $"The first task took {first} ms (measured 566 ms)");
        Assert.True(rest < 1_000, $"{Count - 1:N0} tasks took {rest} ms after the first (measured 84 ms)");
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
