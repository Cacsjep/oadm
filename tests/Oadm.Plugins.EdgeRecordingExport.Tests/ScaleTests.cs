using System.Diagnostics;

using Oadm.Plugins.EdgeRecordingExport.Client;
using Oadm.Sdk.Client.Controls;

using Xunit.Abstractions;

namespace Oadm.Plugins.EdgeRecordingExport.Tests;

/// <summary>30,000 cameras: the choice by tag, the search rows and the summary stay fast.</summary>
[Trait("Category", "Perf")]
public sealed class ScaleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Thirty_thousand_cameras_are_chosen_searched_and_summed_up_quickly()
    {
        var rig = new Rig();
        for (var i = 0; i < Oadm.Sdk.Devices.DeviceLimits.MaxDevices; i++)
        {
            var camera = FakeCamera.WithClips(i % 10, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10));
            rig.Add(camera, $"10.{i / 65536}.{i / 256 % 256}.{i % 256}", tags: i % 2 == 0 ? "Even" : "Odd");
        }

        var vm = new EdgeRecordingViewModel(rig.Page) { AllRecordings = true };
        var watch = Stopwatch.StartNew();
        vm.SetChoice(DeviceChoice.Devices([], ["Even", "Odd"]));
        Assert.Equal(Oadm.Sdk.Devices.DeviceLimits.MaxDevices, vm.ChosenCameras().Count);
        var choose = watch.ElapsedMilliseconds;

        watch.Restart();
        await vm.SearchCommand.ExecuteAsync(null);
        var search = watch.ElapsedMilliseconds;
        output.WriteLine($"Choose: {choose} ms, search of 30,000 cameras (4 at a time, in process): {search} ms");

        Assert.Equal(Oadm.Sdk.Devices.DeviceLimits.MaxDevices, vm.Rows.Count);
        Assert.StartsWith("30,000 cameras · 135,000 recordings · about ", vm.SummaryText, StringComparison.Ordinal);
        Assert.True(choose < 500, $"choose {choose} ms");
        Assert.True(search < 30_000, $"search {search} ms");
    }
}
