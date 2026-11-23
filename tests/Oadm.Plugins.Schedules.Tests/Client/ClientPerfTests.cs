using System.Diagnostics;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Schedules.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;

using Xunit.Abstractions;

namespace Oadm.Plugins.Schedules.Tests.Client;

/// <summary>The wizard's device list at the device limit (CLAUDE.md "scale"): one pass, no per-device lookups.</summary>
public sealed class ClientPerfTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Perf")]
    public async Task The_devices_of_a_choice_are_listed_fast_at_the_device_limit()
    {
        var ctx = Samples.Context();
        ctx.DeviceList.Clear();
        ctx.DeviceList.AddRange(Enumerable.Range(0, DeviceLimits.MaxDevices).Select(i => (IDeviceInfo)new FakeDevice(
            Guid.NewGuid(), $"10.{i >> 16 & 255}.{i >> 8 & 255}.{i & 255}", "AXIS P3265-LVE", null, i % 10 == 0 ? ["Site A"] : [])));
        var editor = new ScheduleEditorViewModel(ctx, null, new FakeTimeProvider(Samples.Now));
        await editor.LoadAsync();

        var watch = Stopwatch.StartNew();
        editor.SetChoice(DeviceChoice.AllDevices);
        var all = watch.Elapsed;
        watch.Restart();
        editor.SetChoice(DeviceChoice.Devices([ctx.DeviceList[1].Id], ["Site A"]));
        var tagged = watch.Elapsed;

        output.WriteLine($"All devices: {all.TotalMilliseconds:F0} ms, a tag and a device: {tagged.TotalMilliseconds:F0} ms");
        Assert.Equal(DeviceLimits.MaxDevices / 10 + 1, editor.ChosenDevices.Count);
        Assert.True(all < TimeSpan.FromSeconds(3), $"All devices took {all.TotalMilliseconds:F0} ms");
        Assert.True(tagged < TimeSpan.FromSeconds(3), $"A tag took {tagged.TotalMilliseconds:F0} ms");
    }
}
