using System.Net;

using Oadm.Core.Plugins;
using Oadm.Core.Tasks;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.VapixCommander.Tests;

/// <summary>Rollouts through the real task engine and core plugin host (one task per device, steps, stop on first error).</summary>
#pragma warning disable CA1001 // Disposed by xUnit through IAsyncLifetime.DisposeAsync.
public sealed class RolloutEngineTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private readonly PluginRegistry _registry = new();
    private readonly FakeDevices _devices = new();
    private readonly FakeVapixFactory _vapix = new();
    private readonly VapixCommanderPlugin _plugin = new(Samples.Directory);
    private TaskEngine _engine = null!;
    private CorePluginHost _host = null!;

    public async Task InitializeAsync()
    {
        _engine = new TaskEngine(new InMemoryTaskStore(), _registry, _devices, _vapix, options: new TaskEngineOptions { MaxParallelTasksPerPlugin = 2 });
        _host = new CorePluginHost(_registry, _devices, _vapix, _engine, new InMemoryPluginSettingsProvider(), secrets: TestSecrets.Create());
        Assert.True(_registry.RegisterCorePlugin(_plugin, new PluginOrigin(VapixCommanderPlugin.PluginId, "0.1.0", null)));
        await _host.StartAllAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        await _engine.DisposeAsync();
    }

    private FakeDevice AddDevice(string address, Func<SeenRequest, HttpResponseMessage>? handler = null, TimeSpan delay = default)
    {
        var device = new FakeDevice { Address = address };
        _devices.All.Add(device);
        var vapix = _vapix.For(device.Id);
        vapix.Handler = handler ?? DeviceAnswers;
        vapix.Delay = delay;
        return device;
    }

    private static HttpResponseMessage DeviceAnswers(SeenRequest request) => request.Uri switch
    {
        _ when request.Uri.StartsWith("axis-cgi/param.cgi?action=list", StringComparison.Ordinal) => FakeVapix.Text("root.Brand.Brand=AXIS\nroot.Brand.ProdNbr=P3265-V\n"),
        _ when request.Uri.StartsWith("axis-cgi/param.cgi?action=update", StringComparison.Ordinal) => FakeVapix.Text("OK"),
        "axis-cgi/basicdeviceinfo.cgi" => FakeVapix.Json("""{"apiVersion":"1.3","data":{"propertyList":{"ProdNbr":"P3265-V","Version":"12.11.77"}}}"""),
        _ => FakeVapix.Text("not found", HttpStatusCode.NotFound),
    };

    private async Task<RolloutReply> RolloutAsync(IEnumerable<FakeDevice> devices, bool stopOnFirstError, params string[] commandIds)
    {
        var json = await _host.InvokeAsync(VapixCommanderPlugin.PluginId, CommanderMethods.Rollout, CommandJson.Write(new RolloutRequest
        {
            DeviceIds = [.. devices.Select(d => d.Id)],
            Commands = [.. commandIds.Select(id => new RolloutCommand { Command = new CommandRef { Source = CommandSources.Library, Id = id } })],
            StopOnFirstError = stopOnFirstError,
            Confirmed = true,
            Owner = "tech@pc",
        }), CancellationToken.None);
        var reply = CommandJson.Read<RolloutReply>(json);
        Assert.Null(reply.Error);
        foreach (var taskId in reply.TaskIds)
        {
            await _engine.WaitForCompletionAsync(taskId, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
        }

        return reply;
    }

    private async Task<TaskRecord> Task(Guid id) => (await _engine.GetAsync(id, CancellationToken.None))!;

    private static string[] Lines(TaskRecord task) => [.. task.Steps.Select(s => $"{s.Name}: {s.State}")];

    [Theory]
    [InlineData(new[] { "Read brand parameters" }, "Read brand parameters")]
    [InlineData(new[] { "Read brand parameters", "Set day/night shift level" }, "Read brand parameters +1 more")]
    [InlineData(new[] { "Set the very long overlay text parameter of the first view area", "B", "C" }, "Set the very long overlay text paramete… +2 more")]
    public void Task_name_is_the_command_or_the_first_command_plus_count(string[] commands, string expected)
    {
        var name = RolloutTaskPlugin.TaskName(commands);
        Assert.Equal(expected, name);
        Assert.True(name!.Length <= Oadm.Sdk.Plugins.TaskPluginNames.MaxTaskNameLength);
        Assert.Null(RolloutTaskPlugin.TaskName([]));
    }

    [Fact]
    public async Task Every_command_is_a_named_step_with_its_result_or_error()
    {
        var ok = AddDevice("10.0.0.48");
        var failing = AddDevice("10.0.0.49", r => r.Uri == "axis-cgi/basicdeviceinfo.cgi"
            ? FakeVapix.Json("""{"apiVersion":"1.0","error":{"code":4002,"message":"Method not supported"}}""")
            : DeviceAnswers(r));

        var reply = await RolloutAsync([ok, failing], stopOnFirstError: false, "common.brand.read", "common.basicdeviceinfo.read", "common.daynight.shiftlevel");

        var first = await Task(reply.TaskIds[0]);
        Assert.Equal(TaskState.Done, first.State);
        Assert.Equal(RolloutTaskPlugin.PluginId, first.PluginId);
        Assert.Equal("Read brand parameters +2 more", first.Name);
        Assert.Equal(
            ["Check compatibility: Done", "Read brand parameters: Done", "Read basic device information: Done", "Set day/night shift level: Done", "Completed: Done"],
            Lines(first));
        Assert.Equal("All 3 commands supported (fresh API list)", first.Steps[0].Detail);
        Assert.Equal("Product: P3265-V", first.Steps[1].Detail);
        Assert.Equal("Model: P3265-V · AXIS OS: 12.11.77", first.Steps[2].Detail);
        Assert.Equal("OK", first.Steps[3].Detail);

        var second = await Task(reply.TaskIds[1]);
        Assert.Equal(TaskState.Failed, second.State);
        Assert.Equal(
            ["Check compatibility: Done", "Read brand parameters: Done", "Read basic device information: Failed", "Set day/night shift level: Done"],
            Lines(second));
        Assert.Equal("Method not supported (code 4002)", second.Steps[2].Detail);
        Assert.Equal("Read basic device information: Method not supported (code 4002)", second.Devices[0].Message);
        var log = await _engine.GetLogAsync(second.Id, CancellationToken.None);
        Assert.Contains(log!, e => e.Level == TaskLogLevel.Error && e.Message.Contains("POST /axis-cgi/basicdeviceinfo.cgi", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Incompatible_commands_fail_their_step_and_send_nothing()
    {
        var device = AddDevice("10.0.0.48");
        _vapix.For(device.Id).ApiList = [new Oadm.Sdk.Vapix.DeviceApi("param-cgi", "1.0")];

        var reply = await RolloutAsync([device], stopOnFirstError: false, "common.basicdeviceinfo.read", "common.daynight.shiftlevel");

        var task = await Task(reply.TaskIds[0]);
        Assert.Equal(TaskState.Failed, task.State);
        Assert.Equal("1 of 2 commands not supported (fresh API list)", task.Steps[0].Detail);
        Assert.Equal("Missing API basic-device-info. Nothing was sent.", task.Steps[1].Detail);
        Assert.DoesNotContain(_vapix.For(device.Id).Requests, r => r.Uri.Contains("basicdeviceinfo", StringComparison.Ordinal));
        Assert.Equal(TaskStepState.Done, task.Steps[2].State);
    }

    [Fact]
    public async Task Read_only_rollouts_use_the_cached_api_list()
    {
        var device = AddDevice("10.0.0.48");

        var reply = await RolloutAsync([device], stopOnFirstError: false, "common.brand.read");

        Assert.Equal(TaskState.Done, (await Task(reply.TaskIds[0])).State);
        Assert.Equal(0, _vapix.For(device.Id).ApiListCalls);
    }

    [Fact]
    public async Task Stop_on_first_error_stops_the_whole_rollout()
    {
        // Two run at a time: the failing device and a slow one; the other three wait in Queued.
        // The failure comes after 200 ms so the slow device has surely started its first command
        // (an instant failure could cancel it while still queued, which is also correct but not this case).
        var failing = AddDevice("10.0.0.1", r => FakeVapix.Text("# Error: Error -1 getting param in group 'Brand'"), delay: TimeSpan.FromMilliseconds(200));
        var slow = AddDevice("10.0.0.2", delay: TimeSpan.FromMilliseconds(400));
        var queued = Enumerable.Range(3, 3).Select(i => AddDevice("10.0.0." + i)).ToList();

        var reply = await RolloutAsync([failing, slow, .. queued], stopOnFirstError: true, "common.brand.read", "common.basicdeviceinfo.read");

        var failed = await Task(reply.TaskIds[0]);
        Assert.Equal(TaskState.Failed, failed.State);
        Assert.Equal(["Check compatibility: Done", "Read brand parameters: Failed", "Read basic device information: Skipped"], Lines(failed));
        Assert.Equal(RolloutTaskPlugin.StoppedHereReason, failed.Steps[2].Detail);
        Assert.Equal("Read brand parameters: Error: Error -1 getting param in group 'Brand'", failed.Devices[0].Message);

        // The running device finished its current command, then stopped.
        var stopped = await Task(reply.TaskIds[1]);
        Assert.Equal(TaskState.Cancelled, stopped.State);
        Assert.Equal(["Check compatibility: Done", "Read brand parameters: Done", "Read basic device information: Skipped"], Lines(stopped));
        Assert.Equal(RolloutTaskPlugin.StoppedElsewhereReason, stopped.Steps[2].Detail);
        Assert.Single(_vapix.For(slow.Id).Requests);

        foreach (var id in reply.TaskIds.Skip(2))
        {
            var task = await Task(id);
            Assert.Equal(TaskState.Cancelled, task.State);
            Assert.DoesNotContain(task.Steps, s => s.State is TaskStepState.Done or TaskStepState.Running);
        }

        Assert.All(queued, d => Assert.Empty(_vapix.For(d.Id).Requests));
        Assert.Equal(0, _plugin.Rollouts.Count);
    }

    [Fact]
    public async Task Without_stop_on_first_error_other_devices_continue()
    {
        var failing = AddDevice("10.0.0.1", r => FakeVapix.Text("# Error: nope"));
        var others = Enumerable.Range(2, 3).Select(i => AddDevice("10.0.0." + i)).ToList();

        var reply = await RolloutAsync([failing, .. others], stopOnFirstError: false, "common.brand.read");

        Assert.Equal(TaskState.Failed, (await Task(reply.TaskIds[0])).State);
        foreach (var id in reply.TaskIds.Skip(1))
        {
            Assert.Equal(TaskState.Done, (await Task(id)).State);
        }
    }
}
