using System.Collections.Concurrent;

using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Plugins;
using Oadm.Core.Tasks;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Tests.Tasks;

#pragma warning disable CA1001 // Disposed by xUnit through IAsyncLifetime.DisposeAsync.
public sealed class TaskEngineStepsTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly FakeDeviceRepository _devices = new();
    private readonly FakeVapixClientFactory _vapix = new();
    private readonly PluginRegistry _registry = new();
    private readonly RecordingStore _store = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
    private TaskEngine? _engine;

    private TaskEngine Engine => _engine ??= new TaskEngine(_store, _registry, _devices, _vapix, timeProvider: _time);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_engine is not null)
        {
            await _engine.DisposeAsync();
        }
    }

    private static string Shape(TaskRecord record) => string.Join(", ", record.Steps.Select(s => $"{s.Name}:{s.State}"));

    [Fact]
    public async Task StepsArePartOfTheSnapshotAndTheFinalRecord()
    {
        var device = _devices.Add();
        Register("t.steps", async (ctx, _, _) =>
        {
            ctx.PlanSteps("Check device", "Send restart", "Verify device");
            using (ctx.BeginStep("Check device"))
            {
                _time.Advance(TimeSpan.FromSeconds(1));
            }

            await ctx.StepAsync("Send restart", step =>
            {
                step.Complete("Accepted");
                return Task.CompletedTask;
            });
        });

        var record = await RunAsync("t.steps", device);

        Assert.Equal(TaskState.Done, record.State);
        Assert.Equal("Check device:Done, Send restart:Done, Verify device:Skipped", Shape(record));
        Assert.Equal("Accepted", record.Steps[1].Detail);
        Assert.Equal("Not run.", record.Steps[2].Detail);
        Assert.Equal(TimeSpan.FromSeconds(1), record.Steps[0].FinishedUtc - record.Steps[0].StartedUtc);
        Assert.Equal(1, record.CurrentStepIndex);
        Assert.Equal(100, record.Devices[0].Progress);
    }

    [Fact]
    public async Task AnExceptionFailsTheStepAndSkipsThePendingOnes()
    {
        var device = _devices.Add();
        Register("t.fail", (ctx, _, _) =>
        {
            ctx.PlanSteps("Read", "Write", "Verify");
            using (ctx.BeginStep("Read"))
            {
            }

            using (ctx.BeginStep("Write"))
            {
                throw new InvalidOperationException("Access denied");
            }
        });

        var record = await RunAsync("t.fail", device);

        Assert.Equal(TaskState.Failed, record.State);
        Assert.Equal("Read:Done, Write:Failed, Verify:Skipped", Shape(record));
        Assert.Equal("Access denied", record.Steps[1].Detail);
        Assert.Equal("Not run: an earlier step failed.", record.Steps[2].Detail);
        Assert.Equal(1, record.CurrentStepIndex);
        Assert.Equal("Access denied", record.Devices[0].Message);
    }

    [Fact]
    public async Task CancellationFailsTheRunningStepAndSkipsTheRest()
    {
        var device = _devices.Add();
        var started = new TaskCompletionSource();
        Register("t.cancel", async (ctx, _, ct) =>
        {
            ctx.PlanSteps("Wait for device", "Verify device");
            using var step = ctx.BeginStep("Wait for device");
            started.SetResult();
            await Task.Delay(System.Threading.Timeout.Infinite, ct);
        });

        var id = Assert.Single(await Engine.RunAsync("t.cancel", [device], null, "tester", CancellationToken.None));
        await started.Task.WaitAsync(Timeout);
        Assert.True(Engine.Cancel(id));
        await Engine.WaitForCompletionAsync(id, CancellationToken.None).WaitAsync(Timeout);
        var record = (await _store.GetAsync(id, CancellationToken.None))!;

        Assert.Equal(TaskState.Cancelled, record.State);
        Assert.Equal("Wait for device:Failed, Verify device:Skipped", Shape(record));
        Assert.Equal("Cancelled.", record.Steps[0].Detail);
        Assert.Equal("Not run: the task was cancelled.", record.Steps[1].Detail);
    }

    [Fact]
    public async Task ProgressFollowsTheStepsUnlessThePluginReportsItsOwn()
    {
        var derived = _devices.Add();
        var gate = new SemaphoreSlim(0);
        var observed = new ConcurrentQueue<int>();
        Register("t.progress", async (ctx, _, ct) =>
        {
            ctx.PlanSteps("A", "B", "C", "D");
            ctx.BeginStep("A").Complete();
            using var b = ctx.BeginStep("B");
            b.ReportProgress(50, "half");
            await gate.WaitAsync(Timeout, ct);
        });

        var id = Assert.Single(await Engine.RunAsync("t.progress", [derived], null, "tester", CancellationToken.None));
        await WaitUntilAsync(async () => (await Engine.GetAsync(id, CancellationToken.None))!.Steps.Any(s => s.Detail == "half"));
        var live = (await Engine.GetAsync(id, CancellationToken.None))!;
        Assert.Equal(37, live.Devices[0].Progress);
        Assert.Equal(37, live.Progress);
        Assert.Equal("B - half", live.Devices[0].Message); // the running step is the device message
        Assert.Equal(1, live.CurrentStepIndex);
        gate.Release();
        await Engine.WaitForCompletionAsync(id, CancellationToken.None).WaitAsync(Timeout);

        var explicitDevice = _devices.Add();
        Register("t.explicit", async (ctx, _, ct) =>
        {
            ctx.PlanSteps("A", "B");
            using var a = ctx.BeginStep("A");
            ctx.ReportProgress(80, "custom");
            await gate.WaitAsync(Timeout, ct);
        });
        var id2 = Assert.Single(await Engine.RunAsync("t.explicit", [explicitDevice], null, "tester", CancellationToken.None));
        await WaitUntilAsync(async () => (await Engine.GetAsync(id2, CancellationToken.None))!.Devices[0].Message == "custom");
        Assert.Equal(80, (await Engine.GetAsync(id2, CancellationToken.None))!.Devices[0].Progress);
        gate.Release();
        await Engine.WaitForCompletionAsync(id2, CancellationToken.None).WaitAsync(Timeout);
    }

    [Fact]
    public async Task AWarningStepEndsTheTaskDoneWithWarnings()
    {
        var device = _devices.Add();
        Register("t.warnstep", (ctx, _, _) =>
        {
            ctx.BeginStep("Verify version").Warn("Device reports 12.10, expected 12.11");
            ctx.BeginStep("Commit").Complete();
            return Task.CompletedTask;
        });

        var record = await RunAsync("t.warnstep", device);

        Assert.Equal(TaskState.DoneWithWarnings, record.State);
        Assert.Equal("Verify version:Warning, Commit:Done", Shape(record));
        Assert.Equal("Device reports 12.10, expected 12.11", record.Devices[0].Message);
        var entry = Assert.Single(await _store.GetLogAsync(record.Id, CancellationToken.None));
        Assert.Equal(TaskLogLevel.Warning, entry.Level);
    }

    [Fact]
    public async Task StepChangesArePublishedOnTheFeedAndPersistedThrottled()
    {
        var device = _devices.Add();
        var changes = new ConcurrentQueue<TaskChange>();
        Engine.TaskChanged += (_, change) => changes.Enqueue(change);
        Register("t.feed", (ctx, _, _) =>
        {
            for (var i = 0; i < 20; i++)
            {
                using var step = ctx.BeginStep("Step " + i);
                step.ReportProgress(50);
            }

            return Task.CompletedTask;
        });

        var record = await RunAsync("t.feed", device);

        Assert.Equal(20, record.Steps.Count);
        Assert.All(record.Steps, s => Assert.Equal(TaskStepState.Done, s.State));
        // Every step change reaches the feed ...
        Assert.True(changes.Count(c => c.Task.Id == record.Id) > 40);
        // ... but the store is written at most once per second (the clock does not move here) plus transitions.
        Assert.True(_store.Updates.Count(u => u.Id == record.Id) <= 5, $"{_store.Updates.Count(u => u.Id == record.Id)} writes");
    }

    [Fact]
    public async Task RecoveryFailsTheRunningStepAndSkipsPendingOnes()
    {
        var t = _time.GetUtcNow();
        var device = Guid.NewGuid();
        var orphan = new TaskRecord(Guid.NewGuid(), "x", "X", TaskState.Running, "o", t, t, null, 30, null,
            [new TaskDeviceRecord(device, TaskState.Running, "Upload firmware", 30)])
        {
            Steps =
            [
                new(0, "Check", TaskStepState.Done, null, 100, t, t),
                new(1, "Upload firmware", TaskStepState.Running, "10 of 80 MB", 12, t, null),
                new(2, "Verify", TaskStepState.Pending, null, 0, null, null),
            ],
        };
        await _store.AddAsync(orphan, CancellationToken.None);

        await Engine.RecoverInterruptedAsync(CancellationToken.None);

        var recovered = (await _store.GetAsync(orphan.Id, CancellationToken.None))!;
        Assert.Equal("Check:Done, Upload firmware:Failed, Verify:Skipped", Shape(recovered));
        Assert.Equal("Server stopped while the task was running.", recovered.Steps[1].Detail);
        Assert.Equal(_time.GetUtcNow(), recovered.Steps[1].FinishedUtc);
        Assert.Equal(1, recovered.CurrentStepIndex);
    }

    [Fact]
    public async Task APreconditionFailureHasNoSteps()
    {
        Register("t.none", (ctx, _, _) => Task.CompletedTask, canRun: _ => false);
        var record = await RunAsync("t.none", _devices.Add());
        Assert.Equal(TaskState.Failed, record.State);
        Assert.Empty(record.Steps);
        Assert.Equal(-1, record.CurrentStepIndex);
    }

    private async Task<TaskRecord> RunAsync(string pluginId, Guid device)
    {
        var id = Assert.Single(await Engine.RunAsync(pluginId, [device], null, "tester", CancellationToken.None));
        await Engine.WaitForCompletionAsync(id, CancellationToken.None).WaitAsync(Timeout);
        return (await _store.GetAsync(id, CancellationToken.None))!;
    }

    private void Register(string id, Func<ITaskExecutionContext, IDeviceInfo, CancellationToken, Task> execute, Func<IDeviceInfo, bool>? canRun = null)
    {
        Assert.True(_registry.RegisterTaskPlugin(new DelegateTaskPlugin(id, execute, canRun), new PluginOrigin("test", "1.0.0", null)));
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException();
            }

            await Task.Delay(5);
        }
    }

    private sealed class RecordingStore : ITaskStore
    {
        private readonly InMemoryTaskStore _inner = new();

        public ConcurrentQueue<TaskRecord> Updates { get; } = new();

        public Task AddAsync(TaskRecord task, CancellationToken ct) => _inner.AddAsync(task, ct);

        public Task UpdateAsync(TaskRecord task, CancellationToken ct)
        {
            Updates.Enqueue(task);
            return _inner.UpdateAsync(task, ct);
        }

        public Task<TaskRecord?> GetAsync(Guid id, CancellationToken ct) => _inner.GetAsync(id, ct);

        public Task<IReadOnlyList<TaskRecord>> ListAsync(CancellationToken ct) => _inner.ListAsync(ct);

        public Task<bool> DeleteAsync(Guid id, CancellationToken ct) => _inner.DeleteAsync(id, ct);

        public Task AppendLogAsync(Guid taskId, IReadOnlyList<TaskLogEntry> entries, CancellationToken ct) => _inner.AppendLogAsync(taskId, entries, ct);

        public Task<IReadOnlyList<TaskLogEntry>> GetLogAsync(Guid taskId, CancellationToken ct) => _inner.GetLogAsync(taskId, ct);
    }
}
