using Xunit.Abstractions;

namespace Oadm.Plugins.ActionRules.Tests;

/// <summary>
/// The events of the device limit merged for the rule builder. Its setup reads every device (several minutes of every
/// core at 100,000 devices), so it runs alone: next to it the time budgets of the other scale tests failed.
/// </summary>
[Collection(nameof(EventMergeScaleTests))]
public sealed class EventMergeScaleTests(ITestOutputHelper output)
{
    private const string VirtualInputTopic = "tns1:Device/tnsaxis:IO/VirtualInput";

    [Fact]
    [Trait("Category", "Perf")]
    public async Task The_events_of_100000_devices_merge_fast()
    {
        var data = Path.Combine(Path.GetTempPath(), "oadm-action-rules-merge-" + Guid.NewGuid().ToString("N"));
        var devices = new FakeDevices();
        var cameras = new FakeCameras();
        var plugin = new ActionRulesPlugin();
        await plugin.StartAsync(new FakeContext(devices, cameras, new RecordingRunner(), new RecordingEvents(), data), CancellationToken.None);
        try
        {
            var os = new[] { "os10", "os11", "os12" };
            for (var i = 0; i < Oadm.Sdk.Devices.DeviceLimits.MaxDevices; i++)
            {
                var device = new FakeDevice { Address = $"10.{i / 65536}.{i / 256 % 256}.{i % 256}" };
                devices.Items.Add(device);
                cameras.Cameras[device.Id] = new RuleCamera(os[i % 3]);
            }

            var read = System.Diagnostics.Stopwatch.StartNew();
            await Parallel.ForEachAsync(devices.Items, async (d, ct) => await plugin.ReadAsync(d, ct));
            read.Stop();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var reply = plugin.Events(new EventsRequest([.. devices.Items.Select(d => d.Id)]));
            watch.Stop();

            output.WriteLine($"read {devices.Items.Count:N0} devices {read.ElapsedMilliseconds} ms, merge {watch.ElapsedMilliseconds} ms, {reply.Events.Count} events");
            Assert.Equal(Oadm.Sdk.Devices.DeviceLimits.MaxDevices, reply.Events.Single(e => e.Event.Topic == VirtualInputTopic).Devices);
            Assert.True(watch.ElapsedMilliseconds < 1_000, $"merge took {watch.ElapsedMilliseconds} ms");
        }
        finally
        {
            await plugin.DisposeAsync();
            if (Directory.Exists(data))
            {
                Directory.Delete(data, recursive: true);
            }
        }
    }
}

/// <summary>Runs <see cref="EventMergeScaleTests"/> alone, after the parallel tests.</summary>
[CollectionDefinition(nameof(EventMergeScaleTests), DisableParallelization = true)]
public sealed class EventMergeRunsAlone;
