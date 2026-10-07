using Oadm.Core.Plugins;
using Oadm.Core.Tasks;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Tests.Tasks;

#pragma warning disable CA1001 // Disposed by xUnit through IAsyncLifetime.DisposeAsync.
public sealed class TaskEngineTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly FakeDeviceRepository _devices = new();
    private readonly FakeVapixClientFactory _vapix = new();
    private readonly PluginRegistry _registry = new();
    private ITaskStore _store = new InMemoryTaskStore();
    private TaskEngine? _engine;

    private TaskEngine Engine => _engine ??= new TaskEngine(_store, _registry, _devices, _vapix);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_engine is not null)
        {
            await _engine.DisposeAsync();
        }
    }

    [Fact]
    public async Task RunCompletesEveryDeviceAndPersistsTheFinalState()
    {
        Register("t.ok", async (ctx, _, ct) =>
        {
            ctx.ReportProgress(50, "half");
            await Task.Yield();
        });
        var ids = _devices.AddMany(3);

        var taskId = await Engine.RunAsync("t.ok", ids, "{\"a\":1}", "alice@pc", CancellationToken.None);
        await Engine.WaitForCompletionAsync(taskId, CancellationToken.None).WaitAsync(Timeout);

        var stored = await _store.GetAsync(taskId, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(TaskState.Done, stored.State);
        Assert.Equal(100, stored.Progress);
        Assert.Equal("alice@pc", stored.Owner);
        Assert.Equal("Test t.ok", stored.Name);
        Assert.Null(stored.PayloadJson); // payloads are never persisted
        Assert.NotNull(stored.StartedUtc);
        Assert.NotNull(stored.FinishedUtc);
        Assert.Equal(ids, stored.Devices.Select(d => d.DeviceId));
        Assert.All(stored.Devices, d =>
        {
            Assert.Equal(TaskState.Done, d.State);
            Assert.Equal(100, d.Progress);
            Assert.Equal("half", d.Message);
        });
    }

    [Fact]
    public async Task TaskGoesQueuedRunningDoneOnTheChangeFeed()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Register("t.gate", (_, _, _) => gate.Task);
        var device = _devices.Add();
        using var subscription = Engine.Changes.Subscribe();

        var taskId = await Engine.RunAsync("t.gate", [device], null, "o", CancellationToken.None);
        var running = await ReadUntilAsync(subscription, c => c.Task.Devices[0].State == TaskState.Running);
        Assert.Equal(TaskState.Running, running.Task.State);
        Assert.Equal(TaskState.Running, (await Engine.GetAsync(taskId, CancellationToken.None))!.State);

        gate.SetResult();
        await Engine.WaitForCompletionAsync(taskId, CancellationToken.None).WaitAsync(Timeout);

        var all = Drain(subscription);
        Assert.Equal(TaskState.Done, all[^1].Task.State);

        // A new watcher starts with a snapshot of existing tasks.
        var watched = new List<TaskChange>();
        using var cts = new CancellationTokenSource(Timeout);
        await foreach (var change in Engine.WatchAsync(includeSnapshot: true, cts.Token))
        {
            watched.Add(change);
            break;
        }

        Assert.Equal(TaskChangeKind.Added, watched[0].Kind);
        Assert.Equal(TaskState.Done, watched[0].Task.State);
    }

    [Fact]
    public async Task FirstChangeIsAddedInQueuedState()
    {
        Register("t.ok", (_, _, _) => Task.CompletedTask);
        using var subscription = Engine.Changes.Subscribe();

        var taskId = await Engine.RunAsync("t.ok", [_devices.Add()], null, "o", CancellationToken.None);
        await Engine.WaitForCompletionAsync(taskId, CancellationToken.None).WaitAsync(Timeout);

        var changes = Drain(subscription);
        Assert.Equal(TaskChangeKind.Added, changes[0].Kind);
        Assert.Equal(TaskState.Queued, changes[0].Task.State);
        Assert.Equal(TaskState.Queued, changes[0].Task.Devices[0].State);
        Assert.Contains(changes, c => c.Task.State == TaskState.Running);
        Assert.Equal(TaskState.Done, changes[^1].Task.State);
        Assert.All(changes.Skip(1), c => Assert.Equal(TaskChangeKind.Updated, c.Kind));
    }

    [Theory]
    [InlineData(null, 8)]
    [InlineData(3, 3)]
    public async Task ParallelismPerTaskIsBounded(int? configured, int expected)
    {
        if (configured is { } max)
        {
            _engine = new TaskEngine(_store, _registry, _devices, _vapix, options: new TaskEngineOptions { MaxParallelDevicesPerTask = max });
        }

        var current = 0;
        var peak = 0;
        Register("t.slow", async (_, _, ct) =>
        {
            var now = Interlocked.Increment(ref current);
            InterlockedMax(ref peak, now);
            await Task.Delay(40, ct);
            Interlocked.Decrement(ref current);
        });

        var taskId = await Engine.RunAsync("t.slow", _devices.AddMany(20), null, "o", CancellationToken.None);
        await Engine.WaitForCompletionAsync(taskId, CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(expected, peak);
        Assert.Equal(TaskState.Done, (await Engine.GetAsync(taskId, CancellationToken.None))!.State);
    }

    [Fact]
    public async Task CancelStopsRunningDevicesAndSkipsQueuedOnes()
    {
        _engine = new TaskEngine(_store, _registry, _devices, _vapix, options: new TaskEngineOptions { MaxParallelDevicesPerTask = 2 });
        var started = 0;
        var twoStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Register("t.forever", async (_, _, ct) =>
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                twoStarted.SetResult();
            }

            await Task.Delay(System.Threading.Timeout.Infinite, ct);
        });

        var taskId = await Engine.RunAsync("t.forever", _devices.AddMany(10), null, "o", CancellationToken.None);
        await twoStarted.Task.WaitAsync(Timeout);

        Assert.True(Engine.Cancel(taskId));
        await Engine.WaitForCompletionAsync(taskId, CancellationToken.None).WaitAsync(Timeout);

        var record = (await _store.GetAsync(taskId, CancellationToken.None))!;
        Assert.Equal(TaskState.Cancelled, record.State);
        Assert.All(record.Devices, d => Assert.Equal(TaskState.Cancelled, d.State));
        Assert.Equal(2, started);
        Assert.False(Engine.Cancel(taskId));
    }

    [Fact]
    public async Task FailuresAreIsolatedPerDevice()
    {
        var ids = _devices.AddMany(4);
        var throwsAsync = ids[1];
        var throwsSync = ids[2];
        Register("t.mixed", (_, device, _) =>
        {
            if (device.Id == throwsSync)
            {
                throw new InvalidOperationException("sync boom");
            }

            return device.Id == throwsAsync ? FailAsync() : Task.CompletedTask;

            static async Task FailAsync()
            {
                await Task.Yield();
                throw new HttpRequestException("async boom");
            }
        });

        var taskId = await Engine.RunAsync("t.mixed", ids, null, "o", CancellationToken.None);
        await Engine.WaitForCompletionAsync(taskId, CancellationToken.None).WaitAsync(Timeout);

        var record = (await _store.GetAsync(taskId, CancellationToken.None))!;
        Assert.Equal(TaskState.Failed, record.State);
        Assert.Equal(TaskState.Done, Device(record, ids[0]).State);
        Assert.Equal(TaskState.Done, Device(record, ids[3]).State);
        Assert.Equal((TaskState.Failed, "async boom"), (Device(record, throwsAsync).State, Device(record, throwsAsync).Message));
        Assert.Equal((TaskState.Failed, "sync boom"), (Device(record, throwsSync).State, Device(record, throwsSync).Message));
    }

    [Fact]
    public async Task PreconditionsFailTheDeviceWithoutRunningThePlugin()
    {
        var runs = 0;
        Register("t.pre", (_, _, _) =>
        {
            Interlocked.Increment(ref runs);
            return Task.CompletedTask;
        }, canRun: d => d.Status != DeviceStatus.Unreachable);

        var missing = Guid.NewGuid();
        var certChanged = _devices.Add(DeviceStatus.CertificateChanged);
        var unreachable = _devices.Add(DeviceStatus.Unreachable);
        var noCredentials = _devices.Add();
        _vapix.Failing.Add(noCredentials);
        var ok = _devices.Add();

        var taskId = await Engine.RunAsync("t.pre", [missing, certChanged, unreachable, noCredentials, ok], null, "o", CancellationToken.None);
        await Engine.WaitForCompletionAsync(taskId, CancellationToken.None).WaitAsync(Timeout);

        var record = (await _store.GetAsync(taskId, CancellationToken.None))!;
        Assert.Equal(TaskState.Failed, record.State);
        Assert.Equal("Device not found.", Device(record, missing).Message);
        Assert.Contains("certificate", Device(record, certChanged).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot run", Device(record, unreachable).Message, StringComparison.Ordinal);
        Assert.Equal("No credentials stored for device.", Device(record, noCredentials).Message);
        Assert.Equal(TaskState.Done, Device(record, ok).State);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task OverallProgressIsTheAverageOfDeviceProgress()
    {
        var ids = _devices.AddMany(2);
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Register("t.progress", async (ctx, device, _) =>
        {
            if (device.Id == ids[0])
            {
                ctx.ReportProgress(60, "sixty");
                reported.SetResult();
            }
            else
            {
                ctx.ReportProgress(150); // clamped to 100
            }

            await release.Task;
        });

        var taskId = await Engine.RunAsync("t.progress", ids, null, "o", CancellationToken.None);
        await reported.Task.WaitAsync(Timeout);
        await WaitUntilAsync(async () => (await Engine.GetAsync(taskId, CancellationToken.None))!.Devices[1].Progress == 100);

        var live = (await Engine.GetAsync(taskId, CancellationToken.None))!;
        Assert.Equal(80, live.Progress);
        Assert.Equal("sixty", live.Devices[0].Message);
        Assert.Equal(80, (await Engine.ListAsync(CancellationToken.None)).Single().Progress);

        release.SetResult();
        await Engine.WaitForCompletionAsync(taskId, CancellationToken.None).WaitAsync(Timeout);
        Assert.Equal(100, (await Engine.GetAsync(taskId, CancellationToken.None))!.Progress);
    }

    [Fact]
    public async Task ContributedTaskReceivesItsOwner()
    {
        ICorePlugin? seenOwner = null;
        var core = new OwnerCorePlugin(new DelegateTaskPlugin("t.contributed", (ctx, _, _) =>
        {
            seenOwner = ctx.Owner;
            return Task.CompletedTask;
        }));
        Assert.True(_registry.RegisterCorePlugin(core, new PluginOrigin("test", "1.0.0", null)));

        var taskId = await Engine.RunAsync("t.contributed", [_devices.Add()], null, "o", CancellationToken.None);
        await Engine.WaitForCompletionAsync(taskId, CancellationToken.None).WaitAsync(Timeout);

        Assert.Same(core, seenOwner);
    }

    [Fact]
    public async Task InvalidRequestsThrow()
    {
        Register("t.ok", (_, _, _) => Task.CompletedTask);
        await Assert.ThrowsAsync<ArgumentException>(() => Engine.RunAsync("nope", [_devices.Add()], null, "o", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => Engine.RunAsync("t.ok", [], null, "o", CancellationToken.None));
        Assert.Empty(await Engine.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task StoreFailuresDoNotStopTheTask()
    {
        _store = new FlakyStore();
        Register("t.ok", (_, _, _) => Task.CompletedTask);
        using var subscription = Engine.Changes.Subscribe();

        var taskId = await Engine.RunAsync("t.ok", [_devices.Add()], null, "o", CancellationToken.None);
        await Engine.WaitForCompletionAsync(taskId, CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(TaskState.Done, Drain(subscription)[^1].Task.State);
    }

    [Fact]
    public async Task DeleteCancelsAnActiveTaskAndRemovesIt()
    {
        Register("t.forever", (_, _, ct) => Task.Delay(System.Threading.Timeout.Infinite, ct));
        using var subscription = Engine.Changes.Subscribe();
        var taskId = await Engine.RunAsync("t.forever", [_devices.Add()], null, "o", CancellationToken.None);
        await ReadUntilAsync(subscription, c => c.Task.State == TaskState.Running);

        Assert.True(await Engine.DeleteAsync(taskId, CancellationToken.None).WaitAsync(Timeout));

        Assert.Null(await Engine.GetAsync(taskId, CancellationToken.None));
        var changes = Drain(subscription);
        Assert.Equal(TaskChangeKind.Removed, changes[^1].Kind);
        Assert.Equal(TaskState.Cancelled, changes[^1].Task.State);
        Assert.False(await Engine.DeleteAsync(taskId, CancellationToken.None));
    }

    [Fact]
    public async Task RecoverMarksInterruptedTasksFailed()
    {
        var device = Guid.NewGuid();
        var interrupted = new TaskRecord(
            Guid.NewGuid(), "t.x", "X", TaskState.Running, "o", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, 40, null,
            [new TaskDeviceRecord(device, TaskState.Running, null, 40)]);
        var finished = interrupted with { Id = Guid.NewGuid(), State = TaskState.Done, Progress = 100 };
        await _store.AddAsync(interrupted, CancellationToken.None);
        await _store.AddAsync(finished, CancellationToken.None);

        await Engine.RecoverInterruptedAsync(CancellationToken.None);

        var recovered = (await _store.GetAsync(interrupted.Id, CancellationToken.None))!;
        Assert.Equal(TaskState.Failed, recovered.State);
        Assert.Equal(TaskState.Failed, recovered.Devices[0].State);
        Assert.NotNull(recovered.FinishedUtc);
        Assert.Equal(finished, await _store.GetAsync(finished.Id, CancellationToken.None));
    }

    [Fact]
    public async Task DisposeCancelsRunningTasks()
    {
        Register("t.forever", (_, _, ct) => Task.Delay(System.Threading.Timeout.Infinite, ct));
        var engine = Engine;
        var taskId = await engine.RunAsync("t.forever", [_devices.Add()], null, "o", CancellationToken.None);

        await engine.DisposeAsync().AsTask().WaitAsync(Timeout);

        Assert.Equal(TaskState.Cancelled, (await _store.GetAsync(taskId, CancellationToken.None))!.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => engine.RunAsync("t.forever", [_devices.Add()], null, "o", CancellationToken.None));
    }

    private void Register(string id, Func<ITaskExecutionContext, IDeviceInfo, CancellationToken, Task> execute, Func<IDeviceInfo, bool>? canRun = null)
    {
        Assert.True(_registry.RegisterTaskPlugin(new DelegateTaskPlugin(id, execute, canRun), new PluginOrigin("test", "1.0.0", null)));
    }

    private static TaskDeviceRecord Device(TaskRecord record, Guid id) => record.Devices.Single(d => d.DeviceId == id);

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    private static async Task<TaskChange> ReadUntilAsync(TaskChangeSubscription subscription, Func<TaskChange, bool> predicate)
    {
        using var cts = new CancellationTokenSource(Timeout);
        await foreach (var change in subscription.Reader.ReadAllAsync(cts.Token))
        {
            if (predicate(change))
            {
                return change;
            }
        }

        throw new InvalidOperationException("Feed completed.");
    }

    private static List<TaskChange> Drain(TaskChangeSubscription subscription)
    {
        var list = new List<TaskChange>();
        while (subscription.Reader.TryRead(out var change))
        {
            list.Add(change);
        }

        return list;
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

    private sealed class OwnerCorePlugin(ITaskPlugin contributed) : ICorePlugin
    {
        public string Id => "core.owner";

        public string DisplayName => "Owner";

        public string? IconKey => null;

        public IReadOnlyList<ITaskPlugin> TaskPlugins { get; } = [contributed];

        public Task StartAsync(ICorePluginContext ctx, CancellationToken ct) => Task.CompletedTask;

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private sealed class FlakyStore : ITaskStore
    {
        private readonly InMemoryTaskStore _inner = new();

        public Task AddAsync(TaskRecord task, CancellationToken ct) => _inner.AddAsync(task, ct);

        public Task UpdateAsync(TaskRecord task, CancellationToken ct) => throw new IOException("disk full");

        public Task<TaskRecord?> GetAsync(Guid id, CancellationToken ct) => _inner.GetAsync(id, ct);

        public Task<IReadOnlyList<TaskRecord>> ListAsync(CancellationToken ct) => _inner.ListAsync(ct);

        public Task<bool> DeleteAsync(Guid id, CancellationToken ct) => _inner.DeleteAsync(id, ct);

        public Task AppendLogAsync(Guid taskId, IReadOnlyList<TaskLogEntry> entries, CancellationToken ct) => _inner.AppendLogAsync(taskId, entries, ct);

        public Task<IReadOnlyList<TaskLogEntry>> GetLogAsync(Guid taskId, CancellationToken ct) => _inner.GetLogAsync(taskId, ct);
    }
}
