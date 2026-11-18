using System.Diagnostics;

using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Provisioning.Nodes;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Testing;

using Xunit.Abstractions;

namespace Oadm.Plugins.Provisioning.Tests;

/// <summary>100,000 devices in OADM (CLAUDE.md "scale"): the free-address check and the camera table stay fast.</summary>
public sealed class PerfTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Perf")]
    public async Task The_free_address_check_and_the_camera_table_stay_fast_at_100000_devices()
    {
        await using var h = new Harness();
        h.Devices.AddRange(Enumerable.Range(0, DeviceLimits.MaxDevices - 1_000).Select(i => new FakeDevice { Serial = $"ACCC8E{i:X6}", Address = $"10.{i / 65_536}.{i / 256 % 256}.{i % 256}" }));
        h.Devices.AddRange(Enumerable.Range(0, 1_000).Select(i => new FakeDevice { Serial = $"B8A44F{i:X6}", Address = $"192.168.{16 + (i / 250)}.{i % 250 + 1}" }));
        await h.StartAsync(h.Profile(Harness.Node("ip", NodeKind.SetIp, Harness.Pool())));

        var watch = Stopwatch.StartNew();
        for (var i = 1; i <= 20; i++)
        {
            var take = await h.Engine.TakeAddressAsync(Harness.Mac(i), Harness.Pool(), CancellationToken.None);
            Assert.Equal($"192.168.15.{9 + i}", take.Address);
        }

        var takes = watch.Elapsed;
        for (var i = 1; i <= 2_000; i++)
        {
            h.Ack(i, hasPassword: true);
        }

        await h.RunUntilAsync(() => h.Engine.Counts().NotNew == 2_000, "every row looked at");
        watch.Restart();
        var rows = 0;
        string? after = null;
        do
        {
            var page = h.Engine.Page(after, 500);
            rows += page.Rows.Count;
            after = page.Next;
        }
        while (after is not null);

        var pages = watch.Elapsed;
        output.WriteLine($"20 address takes at {h.Devices.Count:N0} devices: {takes.TotalMilliseconds:N0} ms; 2,000 rows in pages of 500: {pages.TotalMilliseconds:N0} ms");
        Assert.Equal(2_000, rows);
        Assert.True(takes < TimeSpan.FromSeconds(6), $"20 takes took {takes.TotalMilliseconds:N0} ms");
        Assert.True(pages < TimeSpan.FromSeconds(1), $"the pages took {pages.TotalMilliseconds:N0} ms");
    }
}
