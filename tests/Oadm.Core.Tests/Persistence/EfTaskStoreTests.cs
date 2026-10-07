using Oadm.Core.Persistence;
using Oadm.Core.Plugins;
using Oadm.Core.Tasks;
using Oadm.Core.Tests.Tasks;

namespace Oadm.Core.Tests.Persistence;

public sealed class EfTaskStoreTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private ITaskStore _store = null!;

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _store = _db.Get<ITaskStore>();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private static TaskRecord NewTask(DateTimeOffset created, params Guid[] devices) => new(
        Guid.NewGuid(),
        "oadm.restart",
        "Restart",
        TaskState.Queued,
        "WS01/alice",
        created,
        null,
        null,
        0,
        "{}",
        [.. devices.Select(d => new TaskDeviceRecord(d, TaskState.Queued, null, 0))]);

    [Fact]
    public void RegisteredAsTheEngineStore()
    {
        Assert.IsType<EfTaskStore>(_store);
    }

    [Fact]
    public async Task AddAndGetRoundTrip()
    {
        var device = Guid.NewGuid();
        var created = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var task = NewTask(created, device);
        await _store.AddAsync(task, CancellationToken.None);

        var loaded = await _store.GetAsync(task.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(TaskState.Queued, loaded.State);
        Assert.Equal("WS01/alice", loaded.Owner);
        Assert.Equal(created, loaded.CreatedUtc);
        Assert.Equal("{}", loaded.PayloadJson);
        var result = Assert.Single(loaded.Devices);
        Assert.Equal(device, result.DeviceId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.AddAsync(task, CancellationToken.None));
    }

    [Fact]
    public async Task UpdateReplacesSnapshotIncludingDeviceResults()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var task = NewTask(DateTimeOffset.UtcNow, a, b);
        await _store.AddAsync(task, CancellationToken.None);

        var started = DateTimeOffset.UtcNow;
        var finished = started.AddSeconds(5);
        var final = task with
        {
            State = TaskState.Failed,
            StartedUtc = started,
            FinishedUtc = finished,
            Progress = 140,
            Devices = [new TaskDeviceRecord(a, TaskState.Done, null, 100), new TaskDeviceRecord(Guid.NewGuid(), TaskState.Failed, "Timeout", 50)],
        };
        await _store.UpdateAsync(final, CancellationToken.None);

        var loaded = (await _store.GetAsync(task.Id, CancellationToken.None))!;
        Assert.Equal(TaskState.Failed, loaded.State);
        Assert.Equal(100, loaded.Progress);
        Assert.Equal(started.UtcDateTime, loaded.StartedUtc!.Value.UtcDateTime, TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, loaded.Devices.Count);
        Assert.DoesNotContain(loaded.Devices, d => d.DeviceId == b);
        Assert.Contains(loaded.Devices, d => d.State == TaskState.Failed && d.Message == "Timeout");
    }

    [Fact]
    public async Task ListIsNewestFirstAndDeleteRemovesResults()
    {
        var older = NewTask(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), Guid.NewGuid());
        var newer = NewTask(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), Guid.NewGuid());
        await _store.AddAsync(older, CancellationToken.None);
        await _store.AddAsync(newer, CancellationToken.None);

        var list = await _store.ListAsync(CancellationToken.None);
        Assert.Equal([newer.Id, older.Id], list.Select(t => t.Id));
        Assert.All(list, t => Assert.Single(t.Devices));

        Assert.True(await _store.DeleteAsync(newer.Id, CancellationToken.None));
        Assert.False(await _store.DeleteAsync(newer.Id, CancellationToken.None));
        Assert.Equal(older.Id, Assert.Single(await _store.ListAsync(CancellationToken.None)).Id);
    }

    [Fact]
    public async Task EngineRunsAndRecoversAgainstTheDatabase()
    {
        var registry = new PluginRegistry();
        registry.RegisterTaskPlugin(
            new DelegateTaskPlugin("test.ok", (ctx, _, _) => Task.CompletedTask),
            new PluginOrigin("tests", "1.0.0", null));
        var devices = new FakeDeviceRepository();
        var deviceId = devices.Add();

        await using (var engine = new TaskEngine(_store, registry, devices, new FakeVapixClientFactory()))
        {
            var id = await engine.RunAsync("test.ok", [deviceId], null, "tester", CancellationToken.None);
            await engine.WaitForCompletionAsync(id, CancellationToken.None);
            var stored = await _store.GetAsync(id, CancellationToken.None);
            Assert.Equal(TaskState.Done, stored!.State);
            Assert.Equal(TaskState.Done, Assert.Single(stored.Devices).State);
        }

        // A task left Running by a crashed process is failed on the next start.
        var orphan = NewTask(DateTimeOffset.UtcNow, deviceId) with { State = TaskState.Running };
        await _store.AddAsync(orphan, CancellationToken.None);
        await using (var engine = new TaskEngine(_store, registry, devices, new FakeVapixClientFactory()))
        {
            await engine.RecoverInterruptedAsync(CancellationToken.None);
        }

        Assert.Equal(TaskState.Failed, (await _store.GetAsync(orphan.Id, CancellationToken.None))!.State);
    }
}
