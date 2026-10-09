using System.Diagnostics;
using System.Globalization;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.VapixCommander.Tests;

/// <summary>
/// HARD RULE "scale to thousands of devices" on the server side of VAPIX Commander: compatibility of 5,000 devices,
/// a rollout to 5,000 devices (one batched task start) and the rollout bookkeeping of 5,000 tasks stay linear.
/// The budgets include the first call (JIT) on a busy CI runner; quadratic work on 5,000 items would still exceed them.
/// </summary>
[Trait("Category", "Timing")] // time budgets
public sealed class ScaleTests
{
    private const int Count = 5000;

    private static async Task<(VapixCommanderPlugin Plugin, FakeCoreContext Ctx, List<FakeDevice> Devices)> StartAsync()
    {
        var plugin = new VapixCommanderPlugin(Samples.Directory);
        var ctx = new FakeCoreContext();
        var devices = Enumerable.Range(0, Count).Select(i => new FakeDevice
        {
            Address = string.Create(CultureInfo.InvariantCulture, $"10.{i / 65536}.{i / 256 % 256}.{i % 256}"),
            Apis = i % 25 == 0 ? [new DeviceApi("param-cgi", "1.0")] : Samples.P3265Apis,
            Category = i % 25 == 0 ? DeviceCategory.Speaker : DeviceCategory.Camera,
        }).ToList();
        ctx.DeviceList.All.AddRange(devices);
        await plugin.StartAsync(ctx, CancellationToken.None);
        return (plugin, ctx, devices);
    }

    [Fact]
    public async Task Compatibility_of_5000_devices_is_one_pass()
    {
        var (plugin, _, devices) = await StartAsync();
        var request = CommandJson.Write(new CompatibilityRequest
        {
            DeviceIds = [.. devices.Select(d => d.Id)],
            Commands =
            [
                new CommandRef { Source = CommandSources.Library, Id = "common.brand.read" },
                new CommandRef { Source = CommandSources.Library, Id = "common.basicdeviceinfo.read" },
            ],
        });

        var watch = Stopwatch.StartNew();
        var reply = CommandJson.Read<CompatibilityReply>(await plugin.InvokeAsync(CommanderMethods.CheckCompatibility, request, CancellationToken.None));
        watch.Stop();

        Assert.Equal(Count, reply.Devices.Count);
        Assert.Equal(Count / 25, reply.Devices.Count(d => d.Commands[1].State == CompatibilityState.MissingApi));
        Assert.Equal(devices[7].Id, reply.Devices[7].DeviceId);
        Assert.True(watch.ElapsedMilliseconds < 5000, $"took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Rollout_to_5000_devices_starts_all_tasks_in_one_batch()
    {
        var (plugin, ctx, devices) = await StartAsync();
        var runner = (RecordingRunner)ctx.Tasks;
        var request = CommandJson.Write(new RolloutRequest
        {
            DeviceIds = [.. devices.Select(d => d.Id)],
            Commands = [new RolloutCommand { Command = new CommandRef { Source = CommandSources.Library, Id = "common.brand.read" } }],
            Confirmed = true,
            StopOnFirstError = true,
        });

        var watch = Stopwatch.StartNew();
        var reply = CommandJson.Read<RolloutReply>(await plugin.InvokeAsync(CommanderMethods.Rollout, request, CancellationToken.None));
        watch.Stop();

        Assert.Null(reply.Error);
        Assert.Equal(Count, reply.TaskIds.Count);
        var run = Assert.Single(runner.Runs); // one call for all devices, not one per device
        Assert.Equal(Count, run.Devices.Count);
        Assert.True(watch.ElapsedMilliseconds < 5000, $"took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Rollout_bookkeeping_of_5000_tasks_is_linear()
    {
        var registry = new RolloutRegistry();
        var rollout = registry.Get(Guid.NewGuid(), stopOnFirstError: false);
        var tasks = Enumerable.Range(0, Count).Select(_ => Guid.NewGuid()).ToList();

        var watch = Stopwatch.StartNew();
        Assert.Empty(rollout.SetTaskIds(tasks));
        foreach (var task in tasks)
        {
            Assert.True(rollout.Join(task));
            rollout.Finish(task);
        }

        watch.Stop();
        Assert.Equal(0, registry.Count); // dropped when the last task finished
        Assert.True(watch.ElapsedMilliseconds < 2500, $"took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Stopped_rollout_of_5000_tasks_cancels_the_rest_once_and_is_dropped()
    {
        var registry = new RolloutRegistry();
        var rollout = registry.Get(Guid.NewGuid(), stopOnFirstError: true);
        var tasks = Enumerable.Range(0, Count).Select(_ => Guid.NewGuid()).ToList();
        rollout.SetTaskIds(tasks);
        foreach (var task in tasks.Take(10))
        {
            rollout.Join(task);
        }

        var watch = Stopwatch.StartNew();
        var cancel = rollout.Abort("10.0.0.1: failed");
        Assert.Empty(rollout.Abort("again"));
        foreach (var task in tasks.Take(10))
        {
            rollout.Finish(task);
        }

        watch.Stop();
        Assert.Equal(Count - 10, cancel.Count);
        Assert.False(rollout.Join(tasks[^1]));
        Assert.Equal(0, registry.Count);
        Assert.True(watch.ElapsedMilliseconds < 2500, $"took {watch.ElapsedMilliseconds} ms");
    }
}
