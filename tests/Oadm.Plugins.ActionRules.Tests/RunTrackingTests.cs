using System.Diagnostics;

using Oadm.Plugins.ActionRules.Rules;

using Xunit.Abstractions;

namespace Oadm.Plugins.ActionRules.Tests;

/// <summary>
/// The open runs of the editor: a task end costs O(1) whatever the size of the runs (30,000 devices), and only Undo asks
/// the task engine whether a run's tasks ended.
/// </summary>
public sealed class RunTrackingTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "oadm-action-rules-runs-" + Guid.NewGuid().ToString("N"));
    private readonly FakeDevices _devices = new();
    private readonly RecordingRunner _runner = new();
    private readonly ActionRulesPlugin _plugin = new();

    public async Task InitializeAsync() =>
        await _plugin.StartAsync(new FakeContext(_devices, new FakeCameras(), _runner, new RecordingEvents(), _data), CancellationToken.None);

    public async Task DisposeAsync()
    {
        await _plugin.DisposeAsync();
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    private async Task<RunStarted> DeleteOnAsync(int count)
    {
        var devices = Enumerable.Range(0, count).Select(i => new FakeDevice { Address = "10.1." + (i / 250) + "." + (i % 250) }).ToList();
        _devices.Items.AddRange(devices);
        var request = new RunRequest([.. devices.Select(d => new DeviceTargets(d.Id, [new RuleRef("1", "a")]))], null);
        return ActionRulesJson.Deserialize<RunStarted>(await _plugin.InvokeAsync(ActionRulesMethods.Delete, ActionRulesJson.Serialize(request), CancellationToken.None));
    }

    [Fact]
    public async Task A_refused_task_naming_another_run_does_not_count_as_one_of_its_tasks()
    {
        var run = await DeleteOnAsync(2);
        Assert.Equal(2, _plugin.OpenTasksOf(run.RunId));

        // Started by hand with the run's id: an undo of a run that Undo never marked is refused before anything is read.
        var forged = new RuleTaskPayload(RuleTaskActions.Undo, run.RunId, "Undo", [new DeviceTargets(_devices.Items[0].Id, [new RuleRef("1", "a")])], null, UndoOf: run.RunId);
        var task = (RuleTaskPlugin)_plugin.TaskPlugins[0];
        await Assert.ThrowsAsync<ArgumentException>(() => task.ExecuteAsync(new RecordingContext(new RuleCamera()), _devices.Items[0], ActionRulesJson.Serialize(forged), CancellationToken.None));

        Assert.Equal(2, _plugin.OpenTasksOf(run.RunId)); // the run still waits for both of its tasks
    }

    [Fact]
    public async Task A_task_end_never_asks_the_task_engine_and_the_last_one_ends_the_run()
    {
        var run = await DeleteOnAsync(3);
        var other = await DeleteOnAsync(2);

        foreach (var id in run.TaskIds)
        {
            _runner.Active.TryRemove(id, out _);
            _plugin.TaskEnded(run.RunId);
        }

        Assert.Equal(0, _runner.IsActiveCalls);
        await _plugin.Undo.SaveDeviceAsync(run.RunId, new DeviceUndo(_devices.Items[0].Id, [new UndoRule(null, null)]), CancellationToken.None);
        await _plugin.InvokeAsync(ActionRulesMethods.Undo, ActionRulesJson.Serialize(new UndoRequest(run.RunId)), CancellationToken.None);
        Assert.Equal(0, _runner.IsActiveCalls); // the run ended by its count, nothing to ask

        await _plugin.Undo.SaveDeviceAsync(other.RunId, new DeviceUndo(_devices.Items[3].Id, [new UndoRule(null, null)]), CancellationToken.None);
        var running = await Assert.ThrowsAsync<ArgumentException>(() => _plugin.InvokeAsync(ActionRulesMethods.Undo, ActionRulesJson.Serialize(new UndoRequest(other.RunId)), CancellationToken.None));
        Assert.StartsWith("This change is still running.", running.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task Thirty_thousand_task_ends_of_a_run_next_to_another_open_run_are_fast()
    {
        var run = await DeleteOnAsync(30_000);
        var open = await DeleteOnAsync(30_000);

        var watch = Stopwatch.StartNew();
        foreach (var id in run.TaskIds)
        {
            _runner.Active.TryRemove(id, out _);
            _plugin.TaskEnded(run.RunId);
        }

        watch.Stop();
        output.WriteLine($"30,000 task ends next to an open run of 30,000: {watch.ElapsedMilliseconds} ms");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"30,000 task ends took {watch.ElapsedMilliseconds} ms");
        Assert.Equal(0, _runner.IsActiveCalls);
        Assert.Equal(30_000, open.TaskIds.Count);
    }
}
