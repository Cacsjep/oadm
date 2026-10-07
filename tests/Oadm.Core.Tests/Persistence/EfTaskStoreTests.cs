using Oadm.Core.Persistence;
using Oadm.Core.Plugins;
using Oadm.Core.Tasks;
using Oadm.Core.Tests.Tasks;
using Oadm.Sdk.Plugins;

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
        Assert.Null(loaded.PayloadJson); // payloads may carry secrets and are never persisted
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
            var id = await engine.RunOneAsync("test.ok", [deviceId], null, "tester", CancellationToken.None);
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

    [Fact]
    public async Task StepsRoundTripAreRewrittenInPlaceAndDeletedWithTheTask()
    {
        var t0 = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var task = NewTask(t0, Guid.NewGuid()) with
        {
            Steps =
            [
                new(0, "Check compatibility", TaskStepState.Done, "network-settings 1.8", 100, t0, t0.AddSeconds(1)),
                new(1, "Set DNS", TaskStepState.Pending, null, 0, null, null),
            ],
        };
        await _store.AddAsync(task, CancellationToken.None);
        Assert.Equal(task.Steps, (await _store.GetAsync(task.Id, CancellationToken.None))!.Steps);

        // A dynamic step was inserted before the pending one: indices shift, rows are updated in place.
        var updated = task with
        {
            Steps =
            [
                task.Steps[0],
                new(1, "Set host name", TaskStepState.Skipped, "Keep unchanged", 0, t0.AddSeconds(2), t0.AddSeconds(2)),
                new(2, "Set DNS", TaskStepState.Running, null, 40, t0.AddSeconds(2), null),
            ],
        };
        await _store.UpdateAsync(updated, CancellationToken.None);
        var loaded = (await _store.GetAsync(task.Id, CancellationToken.None))!;
        Assert.Equal(updated.Steps, loaded.Steps);
        Assert.Equal(2, loaded.CurrentStepIndex);
        Assert.Equal(updated.Steps, Assert.Single(await _store.ListAsync(CancellationToken.None)).Steps);

        await _store.UpdateAsync(updated with { Steps = [task.Steps[0]] }, CancellationToken.None);
        Assert.Single((await _store.GetAsync(task.Id, CancellationToken.None))!.Steps);

        Assert.True(await _store.DeleteAsync(task.Id, CancellationToken.None));
        Assert.Null(await _store.GetAsync(task.Id, CancellationToken.None));
    }

    [Fact]
    public async Task LogEntriesRoundTripInOrderAndAreDeletedWithTheTask()
    {
        var device = Guid.NewGuid();
        var task = NewTask(DateTimeOffset.UtcNow, device);
        await _store.AddAsync(task, CancellationToken.None);
        var t0 = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        await _store.AppendLogAsync(task.Id, [new(t0, null, TaskLogLevel.Info, "started"), new(t0.AddSeconds(1), device, TaskLogLevel.Warning, "careful")], CancellationToken.None);
        await _store.AppendLogAsync(task.Id, [new(t0.AddSeconds(2), device, TaskLogLevel.Error, new string('x', 5000))], CancellationToken.None);

        var log = await _store.GetLogAsync(task.Id, CancellationToken.None);

        Assert.Equal(3, log.Count);
        Assert.Equal(new TaskLogEntry(t0, null, TaskLogLevel.Info, "started"), log[0]);
        Assert.Equal(new TaskLogEntry(t0.AddSeconds(1), device, TaskLogLevel.Warning, "careful"), log[1]);
        Assert.Equal(TaskLogLevel.Error, log[2].Level);
        Assert.Equal(OadmDbContext.TaskLogEntryMaxLength, log[2].Message.Length); // cut, not rejected

        Assert.True(await _store.DeleteAsync(task.Id, CancellationToken.None));
        Assert.Empty(await _store.GetLogAsync(task.Id, CancellationToken.None));
    }

    [Fact]
    public async Task APayloadWithAPasswordNeverReachesTheDatabaseFile()
    {
        const string Secret = "Sup3r-Secr3t-Pa55word";
        var registry = new PluginRegistry();
        string? seen = null;
        registry.RegisterTaskPlugin(
            new DelegateTaskPlugin("test.secret", (ctx, _, _) =>
            {
                ctx.ReportProgress(50, "setting the password");
                return Task.CompletedTask;
            }),
            new PluginOrigin("tests", "1.0.0", null));
        registry.RegisterTaskPlugin(new CapturingPlugin(p => seen = p), new PluginOrigin("tests", "1.0.0", null));
        var devices = new FakeDeviceRepository();
        var deviceId = devices.Add();
        var payload = $$"""{"user":"operator1","password":"{{Secret}}"}""";

        await using (var engine = new TaskEngine(_store, registry, devices, new FakeVapixClientFactory()))
        {
            foreach (var plugin in new[] { "test.secret", CapturingPlugin.PluginId })
            {
                var id = await engine.RunOneAsync(plugin, [deviceId], payload, "tester", CancellationToken.None);
                await engine.WaitForCompletionAsync(id, CancellationToken.None);
            }
        }

        Assert.Equal(payload, seen); // the plugin got it
        await _db.CloseAsync();       // flush and release the SQLite files

        var files = Directory.GetFiles(_db.Paths.DataDirectory, OadmPaths.DatabaseFileName + "*");
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var bytes = await File.ReadAllBytesAsync(file);
            Assert.Equal(-1, bytes.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(Secret)));
            Assert.Equal(-1, bytes.AsSpan().IndexOf(System.Text.Encoding.Unicode.GetBytes(Secret)));
        }
    }

    private sealed class CapturingPlugin(Action<string?> onPayload) : Oadm.Sdk.Plugins.ITaskPlugin
    {
        public const string PluginId = "test.capture";

        public string Id => PluginId;

        public string DisplayName => "Capture";

        public string? IconKey => null;

        public bool ShowInToolbar => false;

        public bool RequiresDialog => true;

        public bool CanRun(Oadm.Sdk.Devices.IDeviceInfo device) => true;

        public Task ExecuteAsync(Oadm.Sdk.Plugins.ITaskExecutionContext ctx, Oadm.Sdk.Devices.IDeviceInfo device, string? payloadJson, CancellationToken ct)
        {
            onPayload(payloadJson);
            ctx.Log(Oadm.Sdk.Plugins.TaskLogLevel.Info, "password changed");
            return Task.CompletedTask;
        }
    }
}
