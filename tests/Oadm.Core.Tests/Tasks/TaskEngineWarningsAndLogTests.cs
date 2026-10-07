using System.Collections.Concurrent;

using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Plugins;
using Oadm.Core.Tasks;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Tests.Tasks;

#pragma warning disable CA1001 // Disposed by xUnit through IAsyncLifetime.DisposeAsync.
public sealed class TaskEngineWarningsAndLogTests : IAsyncLifetime
#pragma warning restore CA1001
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly FakeDeviceRepository _devices = new();
    private readonly FakeVapixClientFactory _vapix = new();
    private readonly PluginRegistry _registry = new();
    private readonly CountingStore _store = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
    private readonly TaskEngineOptions _options = new();
    private TaskEngine? _engine;

    private TaskEngine Engine => _engine ??= new TaskEngine(_store, _registry, _devices, _vapix, options: _options, timeProvider: _time);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_engine is not null)
        {
            await _engine.DisposeAsync();
        }
    }

    [Fact]
    public async Task AWarningMakesTheDeviceAndTheTaskDoneWithWarnings()
    {
        var warned = _devices.Add();
        var clean = _devices.Add();
        Register("t.warn", (ctx, device, _) =>
        {
            if (device.Id == warned)
            {
                ctx.ReportWarning("LED not available");
            }

            return Task.CompletedTask;
        });

        var records = await RunAllAsync("t.warn", [warned, clean]);

        // One task per device: the warning only affects the warned device's task.
        var record = records.Single(r => r.DeviceId == warned);
        Assert.Equal(TaskState.DoneWithWarnings, record.State);
        var w = Assert.Single(record.Devices);
        Assert.Equal(TaskState.DoneWithWarnings, w.State);
        Assert.Equal("LED not available", w.Message);
        Assert.Equal(100, w.Progress);
        Assert.Equal(TaskState.Done, records.Single(r => r.DeviceId == clean).State);
        Assert.Empty(await _store.GetLogAsync(records.Single(r => r.DeviceId == clean).Id, CancellationToken.None));

        var entry = Assert.Single(await _store.GetLogAsync(record.Id, CancellationToken.None));
        Assert.Equal(TaskLogLevel.Warning, entry.Level);
        Assert.Equal(warned, entry.DeviceId);
        Assert.Equal("LED not available", entry.Message);
        Assert.True(TaskState.DoneWithWarnings.IsTerminal());
    }

    [Fact]
    public async Task AFailureWinsOverAWarning()
    {
        var device = _devices.Add();
        Register("t.warnfail", (ctx, _, _) =>
        {
            ctx.ReportWarning("first a warning");
            throw new InvalidOperationException("then it broke");
        });

        var record = await RunAsync("t.warnfail", [device]);

        Assert.Equal(TaskState.Failed, record.State);
        Assert.Equal(TaskState.Failed, record.Devices[0].State);
        Assert.Equal("then it broke", record.Devices[0].Message);
        var log = await _store.GetLogAsync(record.Id, CancellationToken.None);
        Assert.Equal([TaskLogLevel.Warning, TaskLogLevel.Error], log.Select(e => e.Level));
        Assert.Equal("then it broke", log[1].Message);
    }

    [Fact]
    public async Task LogEntriesArePersistedInOrderWithTheirDevice()
    {
        var a = _devices.Add();
        Register("t.log", (ctx, device, _) =>
        {
            ctx.Log(TaskLogLevel.Info, "checking " + device.Id);
            ctx.Log(TaskLogLevel.Warning, "odd but fine");
            return Task.CompletedTask;
        });

        var record = await RunAsync("t.log", [a]);

        Assert.Equal(TaskState.Done, record.State); // Log(Warning) alone is not ReportWarning
        var log = await Engine.GetLogAsync(record.Id, CancellationToken.None);
        Assert.NotNull(log);
        Assert.Equal(["checking " + a, "odd but fine"], log.Select(e => e.Message));
        Assert.All(log, e => Assert.Equal(a, e.DeviceId));
        Assert.All(log, e => Assert.Equal(_time.GetUtcNow(), e.TimeUtc));
        Assert.Null(await Engine.GetLogAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task ThePreconditionFailureIsLogged()
    {
        var gone = Guid.NewGuid();
        Register("t.any", (_, _, _) => Task.CompletedTask);

        var record = await RunAsync("t.any", [gone]);

        var entry = Assert.Single(await _store.GetLogAsync(record.Id, CancellationToken.None));
        Assert.Equal(TaskLogLevel.Error, entry.Level);
        Assert.Equal("Device not found.", entry.Message);
    }

    [Fact]
    public async Task TheLogIsCappedPerTask()
    {
        _options.MaxLogEntriesPerTask = 5;
        var device = _devices.Add();
        Register("t.chatty", (ctx, _, _) =>
        {
            for (var i = 0; i < 20; i++)
            {
                ctx.Log(TaskLogLevel.Info, "line " + i);
            }

            return Task.CompletedTask;
        });

        var record = await RunAsync("t.chatty", [device]);

        var log = await _store.GetLogAsync(record.Id, CancellationToken.None);
        Assert.Equal(5, log.Count);
        Assert.Equal(["line 0", "line 1", "line 2", "line 3"], log.Take(4).Select(e => e.Message));
        Assert.Equal(TaskLogLevel.Warning, log[4].Level);
        Assert.StartsWith("Log limit of 5 entries reached", log[4].Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLiveLogIsAvailableWhileTheTaskRuns()
    {
        var device = _devices.Add();
        var logged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Register("t.live", async (ctx, _, ct) =>
        {
            ctx.Log(TaskLogLevel.Info, "uploading");
            logged.SetResult();
            await release.Task.WaitAsync(ct);
        });

        var taskId = await Engine.RunOneAsync("t.live", [device], null, "o", CancellationToken.None);
        await logged.Task.WaitAsync(Timeout);

        var live = await Engine.GetLogAsync(taskId, CancellationToken.None);
        Assert.Equal("uploading", Assert.Single(live!).Message);

        release.SetResult();
        await Engine.WaitForCompletionAsync(taskId, CancellationToken.None).WaitAsync(Timeout);
        Assert.Equal("uploading", Assert.Single(await _store.GetLogAsync(taskId, CancellationToken.None)).Message);
    }

    [Fact]
    public async Task ProgressMessagesArePersistedAtMostOncePerSecondPerDevice()
    {
        var device = _devices.Add();
        var reported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var later = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Register("t.progress", async (ctx, _, ct) =>
        {
            for (var i = 1; i <= 10; i++)
            {
                ctx.ReportProgress(i * 5, "step " + i);
            }

            reported.SetResult();
            await later.Task.WaitAsync(ct);
            ctx.ReportProgress(80, "late step");
            ctx.ReportProgress(85); // no message: never persisted on its own
            await release.Task.WaitAsync(ct);
        });

        var taskId = await Engine.RunOneAsync("t.progress", [device], null, "o", CancellationToken.None);
        await reported.Task.WaitAsync(Timeout);
        await WaitUntilAsync(() => _store.RunningMessages(taskId).Count == 1);
        await Task.Delay(100);
        Assert.Single(_store.RunningMessages(taskId)); // ten reports within the same second: one write

        _time.Advance(TimeSpan.FromSeconds(1));
        later.SetResult();
        await WaitUntilAsync(() => _store.RunningMessages(taskId).Contains("late step"));
        Assert.Equal(2, _store.RunningMessages(taskId).Count);

        release.SetResult();
        await Engine.WaitForCompletionAsync(taskId, CancellationToken.None).WaitAsync(Timeout);
        var stored = await _store.GetAsync(taskId, CancellationToken.None);
        Assert.Equal("late step", stored!.Devices[0].Message); // last message survives as the device result
    }

    [Fact]
    public async Task ThePluginGetsThePayloadButSnapshotsNeverCarryIt()
    {
        var device = _devices.Add();
        string? seen = null;
        Assert.True(_registry.RegisterTaskPlugin(new PayloadPlugin(p => seen = p), new PluginOrigin("test", "1.0.0", null)));
        using var subscription = Engine.Changes.Subscribe();

        var taskId = await Engine.RunOneAsync(PayloadPlugin.PluginId, [device], """{"password":"S3cret!"}""", "o", CancellationToken.None);
        await Engine.WaitForCompletionAsync(taskId, CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal("""{"password":"S3cret!"}""", seen);
        Assert.Null((await Engine.GetAsync(taskId, CancellationToken.None))!.PayloadJson);
        Assert.All(_store.Updates, r => Assert.Null(r.PayloadJson));
        while (subscription.Reader.TryRead(out var change))
        {
            Assert.Null(change.Task.PayloadJson);
        }
    }

    private async Task<TaskRecord> RunAsync(string pluginId, Guid[] devices) => Assert.Single(await RunAllAsync(pluginId, devices));

    private async Task<List<TaskRecord>> RunAllAsync(string pluginId, Guid[] devices)
    {
        var records = new List<TaskRecord>();
        foreach (var taskId in await Engine.RunAsync(pluginId, devices, null, "tester", CancellationToken.None))
        {
            await Engine.WaitForCompletionAsync(taskId, CancellationToken.None).WaitAsync(Timeout);
            records.Add((await _store.GetAsync(taskId, CancellationToken.None))!);
        }

        return records;
    }

    private void Register(string id, Func<ITaskExecutionContext, IDeviceInfo, CancellationToken, Task> execute)
    {
        Assert.True(_registry.RegisterTaskPlugin(new DelegateTaskPlugin(id, execute), new PluginOrigin("test", "1.0.0", null)));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException();
            }

            await Task.Delay(5);
        }
    }

    private sealed class PayloadPlugin(Action<string?> onPayload) : ITaskPlugin
    {
        public const string PluginId = "t.payload";

        public string Id => PluginId;

        public string DisplayName => "Payload";

        public string? IconKey => null;

        public bool ShowInToolbar => false;

        public bool RequiresDialog => true;

        public bool CanRun(IDeviceInfo device) => true;

        public Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
        {
            onPayload(payloadJson);
            return Task.CompletedTask;
        }
    }

    /// <summary>In-memory store that records every update.</summary>
    private sealed class CountingStore : ITaskStore
    {
        private readonly InMemoryTaskStore _inner = new();

        public ConcurrentQueue<TaskRecord> Updates { get; } = new();

        /// <summary>Messages of persisted snapshots taken while the (only) device was running.</summary>
        public List<string> RunningMessages(Guid taskId) => [.. Updates
            .Where(u => u.Id == taskId && u.Devices[0].State == TaskState.Running && u.Devices[0].Message is not null)
            .Select(u => u.Devices[0].Message!)];

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
