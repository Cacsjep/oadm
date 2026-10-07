using Oadm.Core.Persistence;
using Oadm.Core.Plugins;
using Oadm.Core.Tasks;
using Oadm.Core.Tests.Persistence;
using Oadm.Core.Tests.Tasks;

using Xunit.Abstractions;

namespace Oadm.Core.Tests.Perf;

/// <summary>A SQLite database with 50,000 finished tasks, seeded once for the history tests.</summary>
public sealed class TaskHistoryFixture : IAsyncLifetime
{
    public TestDatabase Db { get; private set; } = null!;

    public List<TaskRecord> History { get; } = PerfScale.History();

    public TimeSpan SeedTime { get; private set; }

    public EfTaskStore Store => Db.Get<EfTaskStore>();

    public async Task InitializeAsync()
    {
        Db = await TestDatabase.CreateAsync();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Store.AddRangeAsync(History, CancellationToken.None);
        SeedTime = watch.Elapsed;
    }

    public async Task DisposeAsync() => await Db.DisposeAsync();
}

/// <summary>Task history with 50,000 tasks: paging, active tasks, Watch snapshot, retention (EF Core over SQLite).</summary>
[Trait(PerfScale.Category, PerfScale.Perf)]
public sealed class TaskHistoryScaleTests(TaskHistoryFixture fixture, ITestOutputHelper output) : IClassFixture<TaskHistoryFixture>
{
    [Fact]
    public void Seeding50000TasksInOneTransactionIsFast()
    {
        output.WriteLine($"AddRangeAsync 50,000 tasks (1 result + 3 steps each): {fixture.SeedTime.TotalMilliseconds:F0} ms");
        Assert.True(fixture.SeedTime < TimeSpan.FromSeconds(90), $"seeding took {fixture.SeedTime}");
    }

    [Fact]
    public async Task HistoryPagesAreReadWithPagingNotByLoadingEverything()
    {
        ITaskStore store = fixture.Store;
        TaskPage first = null!;
        await PerfScale.MeasureAsync(output, "ListPageAsync first page of 100", TimeSpan.FromSeconds(2), async () =>
            first = await store.ListPageAsync(0, 100, CancellationToken.None));
        Assert.True(first.TotalCount >= PerfScale.Tasks);
        Assert.Equal(100, first.Tasks.Count);
        Assert.True(first.Tasks[0].CreatedUtc >= first.Tasks[^1].CreatedUtc, "newest first");
        Assert.All(first.Tasks, t => Assert.Equal(3, t.Steps.Count));

        TaskPage last = null!;
        await PerfScale.MeasureAsync(output, "ListPageAsync page at offset 49,900", TimeSpan.FromSeconds(3), async () =>
            last = await store.ListPageAsync(PerfScale.Tasks - 100, 100, CancellationToken.None));
        Assert.Equal(100, last.Tasks.Count);

        await PerfScale.MeasureAsync(output, "ListActiveAsync (Status index) over 50,000 tasks", TimeSpan.FromSeconds(2), async () =>
            Assert.Empty(await store.ListActiveAsync(CancellationToken.None)));
    }

    [Fact]
    public async Task WatchSnapshotWithALimitReadsOnlyTheNewestTasks()
    {
        await using var engine = new TaskEngine(fixture.Store, new PluginRegistry(), new FakeDeviceRepository(), new FakeVapixClientFactory());
        IReadOnlyList<TaskRecord> snapshot = [];
        var limited = await PerfScale.MeasureAsync(output, "Watch snapshot, newest 10,000 of 50,000", TimeSpan.FromSeconds(15), async () =>
            snapshot = await engine.SnapshotAsync(10_000, CancellationToken.None));
        Assert.Equal(10_000, snapshot.Count);
        var newest = fixture.History.Max(t => t.CreatedUtc);
        Assert.Equal(newest, snapshot[0].CreatedUtc);

        var all = await PerfScale.MeasureAsync(output, "Watch snapshot, all 50,000 (legacy, limit 0)", TimeSpan.FromSeconds(60), async () =>
            snapshot = await engine.SnapshotAsync(null, CancellationToken.None));
        Assert.True(snapshot.Count >= PerfScale.Tasks);
        output.WriteLine($"limited snapshot is {all.TotalMilliseconds / Math.Max(1, limited.TotalMilliseconds):F1}x faster");
    }
}

/// <summary>Retention and Delete all on 50,000 tasks (own database each, they delete).</summary>
[Trait(PerfScale.Category, PerfScale.Perf)]
public sealed class TaskRetentionScaleTests(ITestOutputHelper output) : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private List<TaskRecord> _history = null!;

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _history = PerfScale.History();
        await _db.Get<EfTaskStore>().AddRangeAsync(_history, CancellationToken.None);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task RetentionKeepsTheNewestTasksAndPublishesOneRemovedPerTask()
    {
        await using var engine = new TaskEngine(_db.Get<EfTaskStore>(), new PluginRegistry(), new FakeDeviceRepository(), new FakeVapixClientFactory());
        using var subscription = engine.Changes.Subscribe();
        var deleted = 0;
        await PerfScale.MeasureAsync(output, "PruneHistoryAsync to the newest 10,000 of 50,000", TimeSpan.FromSeconds(60), async () =>
            deleted = await engine.PruneHistoryAsync(null, 10_000, CancellationToken.None));
        Assert.Equal(PerfScale.Tasks - 10_000, deleted);

        ITaskStore store = _db.Get<EfTaskStore>();
        var page = await store.ListPageAsync(0, 1, CancellationToken.None);
        Assert.Equal(10_000, page.TotalCount);
        Assert.Equal(_history.Max(t => t.CreatedUtc), page.Tasks[0].CreatedUtc);

        var removed = 0;
        while (subscription.Reader.TryRead(out var change))
        {
            Assert.Equal(TaskChangeKind.Removed, change.Kind);
            removed++;
        }

        Assert.Equal(deleted, removed);

        // Age rule: the history spans 50,000 s (about 14 h) starting 30 days ago, so all of it is older than 7 days.
        Assert.Equal(10_000, await engine.PruneHistoryAsync(TimeSpan.FromDays(7), null, CancellationToken.None));
        Assert.Equal(0, (await store.ListPageAsync(0, 1, CancellationToken.None)).TotalCount);
    }

    [Fact]
    public async Task DeleteAllOf50000TasksIsOneBulkDelete()
    {
        await using var engine = new TaskEngine(_db.Get<EfTaskStore>(), new PluginRegistry(), new FakeDeviceRepository(), new FakeVapixClientFactory());
        var deleted = 0;
        await PerfScale.MeasureAsync(output, "DeleteAllAsync 50,000 tasks", TimeSpan.FromSeconds(60), async () =>
            deleted = await engine.DeleteAllAsync(CancellationToken.None));
        Assert.Equal(PerfScale.Tasks, deleted);
        Assert.Equal(0, (await ((ITaskStore)_db.Get<EfTaskStore>()).ListPageAsync(0, 1, CancellationToken.None)).TotalCount);
    }
}
