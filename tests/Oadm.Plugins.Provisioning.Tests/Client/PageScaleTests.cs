using System.Diagnostics;

using Oadm.Plugins.Provisioning.Client;
using Oadm.Plugins.Provisioning.Engine;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

using Xunit.Abstractions;

namespace Oadm.Plugins.Provisioning.Tests.Client;

/// <summary>The camera table at the device limit (CLAUDE.md "scale"): search, select all and a batch of events.</summary>
public sealed class PageScaleTests(ITestOutputHelper output)
{
    private static List<CameraRow> Rows(int count) =>
    [
        .. Enumerable.Range(0, count).Select(i => Samples.Row(1, i % 10 == 0 ? "AXIS Q6135-LE" : "AXIS P3265-LVE", $"10.{i / 65_536}.{i / 256 % 256}.{i % 256}",
            i % 7 == 0 ? CameraState.Failed : CameraState.Done, 4, [NodeResult.Success("Password set.")]) with { Mac = $"AC:CC:{i / 65_536:X2}:{i / 256 % 256:X2}:{i % 256:X2}:00" }),
    ];

    private static async Task<(ProvisioningViewModel Vm, TimeSpan Load, TimeSpan Search, TimeSpan Select, TimeSpan Events)> RunAsync(int count)
    {
        var vm = new ProvisioningViewModel(Samples.Context(cameras: Rows(count)));
        var watch = Stopwatch.StartNew();
        await vm.LoadAsync();
        var load = watch.Elapsed;
        Assert.Equal(count, vm.Rows.Count);

        watch.Restart();
        vm.SearchText = "q6135";
        var search = watch.Elapsed;
        Assert.Equal((count + 9) / 10, vm.Rows.Count);
        vm.SearchText = string.Empty;

        watch.Restart();
        vm.SetSelection(vm.Rows);
        var failed = vm.RunFailedAgainCommand.CanExecute(null);
        var waiting = vm.ContinueCommand.CanExecute(null);
        var select = watch.Elapsed;
        Assert.True(failed);
        Assert.False(waiting);
        Assert.Equal(count, vm.SelectedRows.Count);

        var changed = vm.Rows.Take(500).Select(r => r.Row with { State = CameraState.Done, Version = 2 }).ToList();
        watch.Restart();
        vm.HandleEvent(new PluginEvent(ProvisioningMethods.CamerasTopic, Samples.Serialize(new CamerasEvent(changed, [vm.Rows[^1].Mac], new CameraCounts(count - 1, 0, 0, 0, count - 1, 0, 0, 0), 2))));
        var events = watch.Elapsed;
        Assert.Equal(count - 1, vm.Rows.Count);
        Assert.All(vm.Rows.Take(500), r => Assert.Equal(CameraState.Done, r.State));
        return (vm, load, search, select, events);
    }

    [Fact]
    public async Task Search_select_all_and_events_work_on_a_large_table()
    {
        var (vm, _, _, _, _) = await RunAsync(1_000);
        Assert.Equal(999, vm.CameraCount);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task Search_select_all_and_events_stay_fast_at_100000_cameras()
    {
        var (_, load, search, select, events) = await RunAsync(DeviceLimits.MaxDevices);
        output.WriteLine($"{DeviceLimits.MaxDevices:N0} cameras: load {load.TotalMilliseconds:N0} ms (JSON pages of 1,000), search {search.TotalMilliseconds:N0} ms, " +
            $"select all {select.TotalMilliseconds:N0} ms, 500 changed rows {events.TotalMilliseconds:N0} ms");
        Assert.True(search < TimeSpan.FromMilliseconds(100), $"the search took {search.TotalMilliseconds:N0} ms");
        Assert.True(select < TimeSpan.FromMilliseconds(100), $"select all took {select.TotalMilliseconds:N0} ms");
        Assert.True(events < TimeSpan.FromMilliseconds(300), $"500 changed rows took {events.TotalMilliseconds:N0} ms");
    }
}
