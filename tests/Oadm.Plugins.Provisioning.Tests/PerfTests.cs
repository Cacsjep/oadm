using System.Diagnostics;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Testing;

using Xunit.Abstractions;

namespace Oadm.Plugins.Provisioning.Tests;

/// <summary>100,000 devices in OADM (CLAUDE.md "scale"): the free-address check and the tasks of a burst of new cameras stay fast.</summary>
public sealed class PerfTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Perf")]
    public async Task The_free_address_check_and_a_burst_of_new_cameras_stay_fast_at_100000_devices()
    {
        await using var h = new Harness();
        h.Devices.AddRange(Enumerable.Range(0, DeviceLimits.MaxDevices - 3_000).Select(i => new FakeDevice { Serial = $"ACCC8E{i:X6}", Address = $"10.{i / 65_536}.{i / 256 % 256}.{i % 256}" }));
        h.Devices.AddRange(Enumerable.Range(0, 1_000).Select(i => new FakeDevice { Serial = $"B8A44F{i:X6}", Address = $"192.168.{16 + (i / 250)}.{i % 250 + 1}" }));
        await h.StartAsync(h.Profile());

        var watch = Stopwatch.StartNew();
        for (var i = 1; i <= 20; i++)
        {
            var take = await h.Engine.TakeAddressAsync(Harness.Camera(i), Harness.CheckedPool(), CancellationToken.None);
            Assert.Equal($"192.168.15.{9 + i}", take.Address);
        }

        var takes = watch.Elapsed;
        var burst = Enumerable.Range(1, 2_000).Select(i => h.AutoAdd.Managed(h.AutoAdd.Cam(Harness.Serial(i)), $"172.16.{i / 250}.{i % 250 + 1}")).ToList();
        watch.Restart();
        foreach (var device in burst)
        {
            h.AutoAdd.Report(device); // as the add page's Commit of 2,000 factory-new cameras reports them
        }

        await Eventually.TrueAsync(() => h.ProvisionedDevices.Count == 2_000, "every camera got its task");
        var starts = watch.Elapsed;
        output.WriteLine($"20 address takes at {h.Devices.Count:N0} devices: {takes.TotalMilliseconds:N0} ms; tasks of 2,000 new cameras: {starts.TotalMilliseconds:N0} ms in {h.ProvisionRuns.Count} runs");
        Assert.Equal(2_000, h.ProvisionedDevices.Distinct().Count());
        Assert.True(takes < TimeSpan.FromSeconds(6), $"20 takes took {takes.TotalMilliseconds:N0} ms");
        Assert.True(starts < TimeSpan.FromSeconds(10), $"the starts took {starts.TotalMilliseconds:N0} ms");
    }
}
