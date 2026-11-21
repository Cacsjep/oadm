using System.Diagnostics;
using System.Globalization;

using Oadm.Plugins.Overlays.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Testing;
using Oadm.Sdk.Vapix;

using Xunit.Abstractions;

namespace Oadm.Plugins.Overlays.Tests;

/// <summary>HARD RULE "scale to thousands of devices": the dialog and CanRun with the most devices (DeviceLimits.MaxDevices).</summary>
public sealed class ScaleTests(ITestOutputHelper output)
{
    private const int Count = DeviceLimits.MaxDevices;

    private static IReadOnlyList<IDeviceInfo> Devices()
    {
        var apis = Fixtures.P3265ApiList();
        IReadOnlyList<DeviceApi> none = [new DeviceApi("param-cgi", "1.0")];
        return [.. Enumerable.Range(0, Count).Select(i => (IDeviceInfo)new FakeDevice
        {
            Address = string.Create(CultureInfo.InvariantCulture, $"10.{i / 65536}.{i / 256 % 256}.{i % 256}"),
            Apis = i % 10 == 0 ? none : apis,
        })];
    }

    [Fact]
    public async Task The_dialog_with_100000_devices_reads_one_camera_and_counts_them_all() => await OpenAsync();

    [Fact]
    [Trait("Category", "Perf")]
    public async Task The_dialog_with_100000_devices_opens_fast()
    {
        var elapsed = await OpenAsync();
        output.WriteLine($"Opening with {Count:N0} devices took {elapsed.TotalMilliseconds:0} ms");
        Assert.True(elapsed < TimeSpan.FromSeconds(1), $"Opening took {elapsed.TotalMilliseconds:0} ms");
    }

    private static async Task<TimeSpan> OpenAsync()
    {
        var devices = Devices();
        var queries = 0;
        var watch = Stopwatch.StartNew();
        var vm = new OverlaysDialogViewModel(OverlaysMode.Add, devices, devices, (_, _, _) =>
        {
            Interlocked.Increment(ref queries);
            return Task.FromResult<string?>(OverlaysJson.Serialize(ViewModelTests.Read()));
        }, _ => Task.FromResult<IDeviceInfo?>(null));
        await vm.LoadAsync(CancellationToken.None);
        vm.Rows[0].Text = "%F %X";
        watch.Stop();

        Assert.Equal(1, queries); // never one query per selected device
        Assert.Equal(string.Create(CultureInfo.CurrentCulture, $"{Count / 10:N0} of {Count:N0} cameras are not supported."), vm.UnsupportedText);
        Assert.Equal(Count - (Count / 10), vm.CompatibleCount);
        Assert.True(vm.CanApply);
        return watch.Elapsed;
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void CanRun_over_100000_devices_is_cheap()
    {
        var devices = Devices();
        var plugin = new AddOverlaysTask();
        var watch = Stopwatch.StartNew();
        var runnable = 0;
        for (var round = 0; round < 10; round++)
        {
            runnable += devices.Count(plugin.CanRun);
        }

        watch.Stop();
        Assert.Equal(10 * (Count - (Count / 10)), runnable);
        Assert.True(watch.ElapsedMilliseconds < 5000, $"{10 * Count:N0} CanRun calls took {watch.ElapsedMilliseconds} ms");
    }
}
