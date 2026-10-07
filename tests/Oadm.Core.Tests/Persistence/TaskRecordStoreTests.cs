using Oadm.Core.Persistence;

namespace Oadm.Core.Tests.Persistence;

public sealed class TaskRecordStoreTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private ITaskRecordStore _store = null!;

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _store = _db.Get<ITaskRecordStore>();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private static TaskRecord NewTask(params Guid[] devices) => new()
    {
        PluginId = "oadm.restart",
        Name = "Restart",
        Owner = "WS01/alice",
        PayloadJson = "{}",
        Results = devices.Select(d => new TaskDeviceResult { DeviceId = d }).ToList(),
    };

    [Fact]
    public async Task CreateAndGetRoundTrip()
    {
        var device = Guid.NewGuid();
        var created = await _store.CreateAsync(NewTask(device), CancellationToken.None);

        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal(DateTimeKind.Utc, created.CreatedUtc.Kind);
        Assert.NotEqual(default, created.CreatedUtc);

        var loaded = await _store.GetAsync(created.Id, CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(TaskRecordStatus.Queued, loaded.Status);
        Assert.Equal("WS01/alice", loaded.Owner);
        var result = Assert.Single(loaded.Results);
        Assert.Equal(device, result.DeviceId);
        Assert.Equal(created.Id, result.TaskId);
    }

    [Fact]
    public async Task StatusTransitionsStampTimes()
    {
        var created = await _store.CreateAsync(NewTask(Guid.NewGuid()), CancellationToken.None);

        var running = await _store.UpdateStatusAsync(created.Id, TaskRecordStatus.Running, CancellationToken.None);
        Assert.NotNull(running!.StartedUtc);
        Assert.Null(running.FinishedUtc);

        Assert.True(await _store.UpdateProgressAsync(created.Id, 140, CancellationToken.None));
        Assert.Equal(100, (await _store.GetAsync(created.Id, CancellationToken.None))!.Progress);
        Assert.True(await _store.UpdateProgressAsync(created.Id, 40, CancellationToken.None));

        var done = await _store.UpdateStatusAsync(created.Id, TaskRecordStatus.Done, CancellationToken.None);
        Assert.Equal(running.StartedUtc, done!.StartedUtc);
        Assert.NotNull(done.FinishedUtc);
        Assert.Equal(100, done.Progress);

        Assert.Null(await _store.UpdateStatusAsync(Guid.NewGuid(), TaskRecordStatus.Failed, CancellationToken.None));
        Assert.False(await _store.UpdateProgressAsync(Guid.NewGuid(), 1, CancellationToken.None));
    }

    [Fact]
    public async Task DeviceResultsAreUpserted()
    {
        var known = Guid.NewGuid();
        var created = await _store.CreateAsync(NewTask(known), CancellationToken.None);
        var extra = Guid.NewGuid();

        Assert.True(await _store.UpdateDeviceResultAsync(created.Id, known, TaskRecordStatus.Running, 50, "Restarting", CancellationToken.None));
        Assert.True(await _store.UpdateDeviceResultAsync(created.Id, known, TaskRecordStatus.Failed, 50, "Timeout", CancellationToken.None));
        Assert.True(await _store.UpdateDeviceResultAsync(created.Id, extra, TaskRecordStatus.Done, 100, null, CancellationToken.None));
        Assert.False(await _store.UpdateDeviceResultAsync(Guid.NewGuid(), known, TaskRecordStatus.Done, 100, null, CancellationToken.None));

        var loaded = await _store.GetAsync(created.Id, CancellationToken.None);
        Assert.Equal(2, loaded!.Results.Count);
        var first = loaded.Results.Single(r => r.DeviceId == known);
        Assert.Equal(TaskRecordStatus.Failed, first.Status);
        Assert.Equal("Timeout", first.Message);
    }

    [Fact]
    public async Task ListIsNewestFirstAndDeleteRemovesResults()
    {
        var older = NewTask(Guid.NewGuid());
        older.CreatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = NewTask(Guid.NewGuid());
        newer.CreatedUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        await _store.CreateAsync(older, CancellationToken.None);
        await _store.CreateAsync(newer, CancellationToken.None);

        var list = await _store.ListAsync(CancellationToken.None);
        Assert.Equal([newer.Id, older.Id], list.Select(t => t.Id));
        Assert.All(list, t => Assert.Single(t.Results));

        Assert.True(await _store.DeleteAsync(newer.Id, CancellationToken.None));
        Assert.False(await _store.DeleteAsync(newer.Id, CancellationToken.None));
        Assert.Equal(older.Id, Assert.Single(await _store.ListAsync(CancellationToken.None)).Id);
    }
}
