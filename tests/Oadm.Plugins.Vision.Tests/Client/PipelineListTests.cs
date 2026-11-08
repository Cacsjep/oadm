using System.Collections.Specialized;
using System.Diagnostics;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Client;
using Oadm.Sdk.Devices;

using Xunit.Abstractions;

namespace Oadm.Plugins.Vision.Tests.Client;

/// <summary>The pipeline list at scale: rows updated in place, camera labels in one pass over the devices.</summary>
public sealed class PipelineListTests(ITestOutputHelper output)
{
    private static VisionViewModel Page(FakePageContext ctx) => new(ctx) { Check = PageSamples.Check, DecodePicture = PageSamples.Decode };

    [Fact]
    public void A_list_event_with_the_same_rows_updates_them_in_place_without_a_reset()
    {
        using var vm = Page(PageSamples.Context());
        vm.ApplyList(PageSamples.List);
        var first = vm.Pipelines[0];
        var resets = 0;
        vm.Pipelines.CollectionChanged += (_, e) => resets += e.Action == NotifyCollectionChangedAction.Reset ? 1 : 0;

        var list = PageSamples.List;
        vm.ApplyList(list with { Pipelines = [list.Pipelines[0] with { Last = "In 13 · Out 7" }, .. list.Pipelines.Skip(1)] });
        Assert.Equal(0, resets);
        Assert.Same(first, vm.Pipelines[0]);
        Assert.Equal("In 13 · Out 7", first.Last);

        vm.ApplyList(list with { Pipelines = [.. list.Pipelines.Take(2)] }); // one gone: the list changes once
        Assert.Equal(1, resets);
        Assert.Equal(2, vm.Pipelines.Count);
    }

    [Fact]
    public async Task Camera_labels_come_from_one_pass_over_the_devices_and_follow_a_device_change()
    {
        var ctx = PageSamples.Context();
        using var vm = Page(ctx);
        var reads = ctx.DeviceReads;
        vm.ApplyList(PageSamples.List);
        Assert.Equal(reads + 1, ctx.DeviceReads); // three pipelines, two cameras: one pass
        Assert.Equal("AXIS Q1798-LE · 10.0.0.48", vm.Pipelines[0].Camera);

        reads = ctx.DeviceReads;
        vm.ApplyList(PageSamples.List); // known cameras: no pass
        Assert.Equal(reads, ctx.DeviceReads);

        var gate = ctx.DeviceList.FindIndex(d => d.Id == PageSamples.Gate.Id);
        ctx.DeviceList[gate] = PageSamples.Gate with { Address = "10.0.0.99" };
        var relabeled = new TaskCompletionSource();
        vm.CamerasRelabeled += (_, _) => relabeled.TrySetResult();
        ctx.RaiseDevicesChanged();
        Assert.Equal(reads + 1, ctx.DeviceReads);
        await relabeled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("AXIS Q1798-LE · 10.0.0.99", vm.Pipelines[0].Camera);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task Camera_labels_of_200_pipelines_at_the_device_limit_stay_fast()
    {
        var ctx = PageSamples.Context();
        ctx.DeviceList.Clear();
        for (var i = 0; i < DeviceLimits.MaxDevices; i++)
        {
            ctx.DeviceList.Add(new TestDevice($"10.{i >> 16}.{(i >> 8) & 255}.{i & 255}", "AXIS Q1798-LE"));
        }

        var list = new PipelineList
        {
            Pipelines = [.. Enumerable.Range(0, 200).Select(i => new PipelineRow
            {
                Id = "p" + i, Name = "Pipeline " + i, DeviceId = ctx.DeviceList[DeviceLimits.MaxDevices - 1 - (i * 7)].Id,
            })],
        };
        using var vm = Page(ctx);
        var watch = Stopwatch.StartNew();
        vm.ApplyList(list);
        var apply = watch.Elapsed;
        var relabeled = new TaskCompletionSource();
        vm.CamerasRelabeled += (_, _) => relabeled.TrySetResult();
        watch.Restart();
        ctx.RaiseDevicesChanged();
        var caller = watch.Elapsed; // what the UI thread waits for: the pass runs elsewhere
        await relabeled.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var done = watch.Elapsed;
        output.WriteLine($"200 pipelines, {DeviceLimits.MaxDevices:N0} devices: first list {apply.TotalMilliseconds:F1} ms, device change on the caller {caller.TotalMilliseconds:F2} ms, labels done {done.TotalMilliseconds:F1} ms");
        Assert.Equal(200, vm.Pipelines.Count);
        Assert.True(apply < TimeSpan.FromMilliseconds(100), $"the first list took {apply.TotalMilliseconds:F1} ms");
        Assert.True(caller < TimeSpan.FromMilliseconds(10), $"the device change held the caller {caller.TotalMilliseconds:F2} ms");
        Assert.True(done < TimeSpan.FromMilliseconds(200), $"the labels took {done.TotalMilliseconds:F1} ms");
    }
}
