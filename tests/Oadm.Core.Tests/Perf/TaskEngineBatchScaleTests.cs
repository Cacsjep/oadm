using Oadm.Core.Persistence;
using Oadm.Core.Plugins;
using Oadm.Core.Tasks;
using Oadm.Core.Tests.Persistence;
using Oadm.Core.Tests.Tasks;

using Xunit.Abstractions;

namespace Oadm.Core.Tests.Perf;

/// <summary>A Run on 5,000 devices: one store transaction for the submission, bounded parallelism, no lost changes.</summary>
[Trait(PerfScale.Category, PerfScale.Perf)]
public sealed class TaskEngineBatchScaleTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly FakeDeviceRepository _devices = new();
    private readonly PluginRegistry _registry = new();
    private TestDatabase _db = null!;

    public async Task InitializeAsync() => _db = await TestDatabase.CreateAsync();

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task RunOn5000DevicesWithTheDatabaseStore()
    {
        var running = 0;
        var maxRunning = 0;
        Assert.True(_registry.RegisterTaskPlugin(new DelegateTaskPlugin("perf.ok", async (ctx, _, ct) =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref maxRunning, now);
            ctx.ReportProgress(50, "half");
            await Task.Yield();
            Interlocked.Decrement(ref running);
        }), new PluginOrigin("test", "1.0.0", null)));
        var ids = _devices.AddMany(PerfScale.Devices);
        await using var engine = new TaskEngine(_db.Get<EfTaskStore>(), _registry, _devices, new FakeVapixClientFactory());

        // A subscriber that does not read during the whole Run (a slow client) must still see every final state.
        using var slowSubscriber = engine.Changes.Subscribe();

        IReadOnlyList<Guid> taskIds = [];
        await PerfScale.MeasureAsync(output, "RunAsync 5,000 devices (submission, one AddRange)", TimeSpan.FromSeconds(15), async () =>
            taskIds = await engine.RunAsync("perf.ok", ids, null, "tester", CancellationToken.None));
        Assert.Equal(PerfScale.Devices, taskIds.Count);

        await PerfScale.MeasureAsync(output, "5,000 tasks executed and persisted (8 in parallel)", TimeSpan.FromSeconds(240), async () =>
        {
            foreach (var id in taskIds)
            {
                await engine.WaitForCompletionAsync(id, CancellationToken.None);
            }
        });
        Assert.InRange(maxRunning, 1, new TaskEngineOptions().MaxParallelTasksPerPlugin);

        var final = new Dictionary<Guid, TaskChange>();
        while (slowSubscriber.Reader.TryRead(out var change))
        {
            final[change.Task.Id] = change;
        }

        Assert.Equal(PerfScale.Devices, final.Count);
        Assert.All(final.Values, c => Assert.Equal(TaskState.Done, c.Task.State));

        ITaskStore store = _db.Get<EfTaskStore>();
        var page = await store.ListPageAsync(0, PerfScale.Devices, CancellationToken.None);
        Assert.Equal(PerfScale.Devices, page.TotalCount);
        Assert.All(page.Tasks, t => Assert.Equal(TaskState.Done, t.State));
    }

    [Fact]
    public async Task RunOn5000DevicesEngineOverheadWithTheMemoryStore()
    {
        Assert.True(_registry.RegisterTaskPlugin(new DelegateTaskPlugin("perf.mem", (_, _, _) => Task.CompletedTask), new PluginOrigin("test", "1.0.0", null)));
        var ids = _devices.AddMany(PerfScale.Devices);
        await using var engine = new TaskEngine(new InMemoryTaskStore(), _registry, _devices, new FakeVapixClientFactory());
        await PerfScale.MeasureAsync(output, "Run + execute 5,000 tasks, memory store", TimeSpan.FromSeconds(30), async () =>
        {
            var taskIds = await engine.RunAsync("perf.mem", ids, null, "tester", CancellationToken.None);
            foreach (var id in taskIds)
            {
                await engine.WaitForCompletionAsync(id, CancellationToken.None);
            }
        });
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }
}
