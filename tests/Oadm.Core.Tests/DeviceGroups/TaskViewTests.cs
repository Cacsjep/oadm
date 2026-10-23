using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Core.Devices;
using Oadm.Core.Persistence;
using Oadm.Core.Plugins;
using Oadm.Core.Tasks;
using Oadm.Core.Tests.Tasks;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Core.Tests.DeviceGroups;

/// <summary>
/// Tasks through the caller's view (docs/spec/device-scope.md, Tasks): runs, the queue check of the owner's current view,
/// the plugin's call, the filtered history, the Watch feed per view and the backstop of the system runner.
/// </summary>
public sealed class TaskViewTests : DeviceGroupTestBase
{
    private const string Tag = "Building B";

    private readonly PluginRegistry _registry = new();
    private readonly FakeVapixClientFactory _vapix = new();

    private TaskEngine Engine(bool withViews = true) =>
        new(Db.Get<EfTaskStore>(), _registry, Devices, _vapix) { Views = withViews ? Views : null };

    private async Task<(UserRecord User, AuthenticatedCaller Caller, DeviceView View)> GroupUserAsync(string name = "anna")
    {
        var tag = await TagIdAsync(Tag);
        var group = (await SaveGroupAsync("B", tags: [tag])).Group;
        var user = await Users.AddAsync(name, "pw", BuiltInRoles.OperatorId, DeviceScope.Group(group.Id), Ct);
        var caller = CallerOf(user);
        return (user, caller, await Views.ForAsync(caller, Ct));
    }

    private void Register(ITaskPlugin plugin) => Assert.True(_registry.RegisterTaskPlugin(plugin, new PluginOrigin("test", "1.0.0", null)));

    private static TaskRecord Finished(Guid deviceId, DateTimeOffset created) =>
        new(Guid.NewGuid(), "t.any", "Any", TaskState.Done, "tester@pc", created, created, created, 100, null,
            [new TaskDeviceRecord(deviceId, TaskState.Done, null, 100)]);

    [Fact]
    public async Task ARunOnTheViewGivesThePluginTheOwnersCallLimitedToTheView()
    {
        var inside = await AddDeviceAsync("ACCC8E000001", tags: [Tag]);
        var outside = await AddDeviceAsync("ACCC8E000002");
        var (_, caller, view) = await GroupUserAsync();
        ICallContext? seen = null;
        IReadOnlyList<IDeviceInfo>? listed = null;
        Exception? vapixRefused = null;
        Exception? runRefused = null;
        Register(new DelegateTaskPlugin("t.call", async (ctx, _, ct) =>
        {
            seen = ctx.Caller;
            listed = await ctx.Caller!.Devices.ListAsync(ct);
            vapixRefused = await Record.ExceptionAsync(() => ctx.Caller.Vapix.CreateAsync(outside.Id, ct));
            runRefused = await Record.ExceptionAsync(() => ctx.Caller.Tasks.RunAsync("t.call", [outside.Id], null, "x", ct));
        }));
        await using var engine = Engine();

        var ids = await engine.RunAsync("t.call", await view.GetAsync([inside.Id], Ct), null, TaskCaller.Of(caller), Ct);
        await engine.WaitForCompletionAsync(ids[0], Ct);

        var task = await engine.GetAsync(ids[0], Ct);
        Assert.Equal(TaskState.Done, task!.State);
        Assert.Equal(caller.Owner, task.Owner);
        Assert.NotNull(seen);
        Assert.Equal("anna", seen!.UserName);
        Assert.False(seen.HasAllDevices);
        Assert.Equal(view.ScopeKey, seen.DeviceScopeKey);
        Assert.Equal([inside.Id], listed!.Select(d => d.Id));
        Assert.True(seen.Contains(inside));
        Assert.False(seen.Contains(outside));
        Assert.IsType<DevicesNotAvailableException>(vapixRefused);
        Assert.IsType<DevicesNotAvailableException>(runRefused);
    }

    [Fact]
    public async Task TasksTheSystemStartsHaveNoCaller()
    {
        var device = await AddDeviceAsync("ACCC8E000001");
        var caller = new TaskCompletionSource<ICallContext?>();
        Register(new DelegateTaskPlugin("t.system", (ctx, _, _) =>
        {
            caller.TrySetResult(ctx.Caller);
            return Task.CompletedTask;
        }));
        await using var engine = Engine();

        await engine.RunAsync("t.system", [device.Id], null, "plugin", Ct);

        Assert.Null(await caller.Task.WaitAsync(Eventually.DefaultTimeout));
    }

    [Fact]
    public async Task AQueuedTaskWhoseDeviceLeftTheOwnersViewFailsWithoutReachingTheDevice()
    {
        var first = await AddDeviceAsync("ACCC8E000001", tags: [Tag]);
        var leaving = await AddDeviceAsync("ACCC8E000002", tags: [Tag]);
        var staying = await AddDeviceAsync("ACCC8E000003", tags: [Tag]);
        var (_, caller, view) = await GroupUserAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        Register(new DelegateTaskPlugin("t.gated", async (_, device, ct) =>
        {
            reached.Add(device.Id);
            if (device.Id == first.Id)
            {
                started.TrySetResult();
                await gate.Task.WaitAsync(ct);
            }
        })
        { Limit = 1 });
        await using var engine = Engine();

        var ids = await engine.RunAsync("t.gated", await view.GetAsync([first.Id, leaving.Id, staying.Id], Ct), null, TaskCaller.Of(caller), Ct);
        await started.Task.WaitAsync(Eventually.DefaultTimeout);

        // While two tasks wait in the queue, one device leaves the group (its tag is removed).
        await Tags.SetDeviceTagsAsync(await Views.System.GetAsync([leaving.Id], Ct), [], [Tag], Ct);
        gate.SetResult();
        foreach (var id in ids)
        {
            await engine.WaitForCompletionAsync(id, Ct);
        }

        var left = await engine.GetAsync(ids[1], Ct);
        Assert.Equal(TaskState.Failed, left!.State);
        Assert.Equal(DeviceMessages.NoLongerAvailable(1), left.Devices[0].Message);
        Assert.Equal(TaskState.Done, (await engine.GetAsync(ids[2], Ct))!.State);
        Assert.DoesNotContain(leaving.Id, reached);
        Assert.Contains(DeviceMessages.NoLongerAvailable(1), (await engine.GetLogAsync(ids[1], Ct))!.Select(e => e.Message));
    }

    [Fact]
    public async Task QueuedTasksOfAUserWhoLostTheGroupFail()
    {
        var first = await AddDeviceAsync("ACCC8E000001", tags: [Tag]);
        var second = await AddDeviceAsync("ACCC8E000002", tags: [Tag]);
        var (user, caller, view) = await GroupUserAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        Register(new DelegateTaskPlugin("t.gated", async (_, device, ct) =>
        {
            reached.Add(device.Id);
            started.TrySetResult();
            await gate.Task.WaitAsync(ct);
        })
        { Limit = 1 });
        await using var engine = Engine();

        var ids = await engine.RunAsync("t.gated", await view.GetAsync([first.Id, second.Id], Ct), null, TaskCaller.Of(caller), Ct);
        await started.Task.WaitAsync(Eventually.DefaultTimeout);

        // The group is deleted: the user has no devices (fail closed).
        var groupId = view.GroupId!.Value;
        await Groups.DeleteAsync(groupId, Ct);
        gate.SetResult();
        await engine.WaitForCompletionAsync(ids[1], Ct);

        var second2 = await engine.GetAsync(ids[1], Ct);
        Assert.Equal(TaskState.Failed, second2!.State);
        Assert.Equal(DeviceMessages.NoLongerAvailable(1), second2.Devices[0].Message);
        Assert.Equal([first.Id], reached);
        Assert.False((await Users.ListAsync(Ct)).Single(u => u.Id == user.Id).HasAllDevices);
    }

    [Fact]
    public async Task AUserWithAllDevicesRunsAsBeforeAndTheQueueCheckPasses()
    {
        var device = await AddDeviceAsync("ACCC8E000001");
        var admin = await Users.AddAsync("boss", "pw", BuiltInRoles.OperatorId, DeviceScope.All, Ct);
        var caller = CallerOf(admin);
        var view = await Views.ForAsync(caller, Ct);
        ICallContext? seen = null;
        Register(new DelegateTaskPlugin("t.all", (ctx, _, _) =>
        {
            seen = ctx.Caller;
            return Task.CompletedTask;
        }));
        await using var engine = Engine();

        var ids = await engine.RunAsync("t.all", await view.GetAsync([device.Id], Ct), null, TaskCaller.Of(caller), Ct);
        await engine.WaitForCompletionAsync(ids[0], Ct);

        Assert.Equal(TaskState.Done, (await engine.GetAsync(ids[0], Ct))!.State);
        Assert.True(seen!.HasAllDevices);
        Assert.Equal(ICallContext.AllDevicesScopeKey, seen.DeviceScopeKey);
    }

    [Fact]
    public async Task TheSystemRunnerRefusesDevicesOutsideTheViewOfAGroupCaller()
    {
        var inside = await AddDeviceAsync("ACCC8E000001", tags: [Tag]);
        var outside = await AddDeviceAsync("ACCC8E000002");
        var (_, caller, _) = await GroupUserAsync();
        Register(new DelegateTaskPlugin("t.backstop", (_, _, _) => Task.CompletedTask));
        await using var engine = Engine();
        await using var withoutViews = Engine(withViews: false);

        using (CallerContext.Use(caller))
        {
            await Assert.ThrowsAsync<DevicesNotAvailableException>(() => engine.RunAsync("t.backstop", [inside.Id, outside.Id], null, "x", Ct));
            await Assert.ThrowsAsync<DevicesNotAvailableException>(() => withoutViews.RunAsync("t.backstop", [inside.Id], null, "x", Ct));
            var ok = await engine.RunAsync("t.backstop", [inside.Id], null, "x", Ct);
            await engine.WaitForCompletionAsync(ok[0], Ct);
            Assert.Equal(caller.Owner, (await engine.GetAsync(ok[0], Ct))!.Owner);
        }

        Assert.Single(await Db.Get<EfTaskStore>().ListAsync(Ct));
    }

    [Fact]
    public async Task TheHistoryOfAGroupIsFilteredInTheQueryAndTheLimitCountsAfterTheFilter()
    {
        var inside = await AddDeviceAsync("ACCC8E000001", tags: [Tag]);
        var outside = await AddDeviceAsync("ACCC8E000002");
        var removed = Guid.NewGuid();
        var (_, _, view) = await GroupUserAsync();
        var store = Db.Get<EfTaskStore>();
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        var insideTasks = new List<TaskRecord>();
        for (var i = 0; i < 5; i++)
        {
            insideTasks.Add(Finished(inside.Id, start.AddSeconds(i)));
        }

        // Newer tasks of other devices than the group's: a limit applied before the filter would return none of the group.
        var others = Enumerable.Range(0, 20).Select(i => Finished(i % 2 == 0 ? outside.Id : removed, start.AddMinutes(10).AddSeconds(i))).ToList();
        await store.AddRangeAsync([.. insideTasks, .. others], Ct);
        await using var engine = Engine();

        var page = await engine.ListPageAsync(view, 0, 3, Ct);
        Assert.Equal(5, page.TotalCount);
        Assert.Equal(insideTasks.OrderByDescending(t => t.CreatedUtc).Take(3).Select(t => t.Id), page.Tasks.Select(t => t.Id));
        var snapshot = await engine.SnapshotAsync(view, 3, Ct);
        Assert.Equal(3, snapshot.Count);
        Assert.All(snapshot, t => Assert.Equal(inside.Id, t.DeviceId));
        Assert.Equal(5, (await engine.ListAsync(view, Ct)).Count);

        // Users with all devices: everything, also the history of the removed device.
        var all = await Views.ForScopeAsync(DeviceScope.All, "boss", Ct);
        Assert.Equal(25, (await engine.ListPageAsync(all, 0, 100, Ct)).TotalCount);
        Assert.Equal(25, (await engine.SnapshotAsync(all, null, Ct)).Count);

        // A view without devices sees no task.
        var none = await Views.ForScopeAsync(DeviceScope.None, "nobody", Ct);
        Assert.Equal(0, (await engine.ListPageAsync(none, 0, 100, Ct)).TotalCount);
        Assert.Empty(await engine.SnapshotAsync(none, 10, Ct));
    }

    [Fact]
    public async Task TaskIdMethodsAnswerATaskOutsideTheViewLikeAnUnknownOne()
    {
        var inside = await AddDeviceAsync("ACCC8E000001", tags: [Tag]);
        var outside = await AddDeviceAsync("ACCC8E000002");
        var (_, _, view) = await GroupUserAsync();
        var store = Db.Get<EfTaskStore>();
        var now = DateTimeOffset.UtcNow;
        var mine = Finished(inside.Id, now);
        var theirs = Finished(outside.Id, now);
        var ofRemoved = Finished(Guid.NewGuid(), now);
        await store.AddRangeAsync([mine, theirs, ofRemoved], Ct);
        await using var engine = Engine();

        foreach (var hidden in new[] { theirs.Id, ofRemoved.Id, Guid.NewGuid() })
        {
            Assert.Null(await engine.GetAsync(view, hidden, Ct));
            Assert.Null(await engine.GetLogAsync(view, hidden, Ct));
            Assert.False(await engine.CancelAsync(view, hidden, Ct));
            Assert.False(await engine.DeleteAsync(view, hidden, Ct));
        }

        Assert.Equal(3, (await store.ListAsync(Ct)).Count);
        Assert.NotNull(await engine.GetLogAsync(view, mine.Id, Ct));
        Assert.True(await engine.CancelAsync(view, mine.Id, Ct));
        Assert.True(await engine.DeleteAsync(view, mine.Id, Ct));
        Assert.Equal(2, (await store.ListAsync(Ct)).Count);
    }

    [Fact]
    public async Task TheWatchOfAGroupGetsOnlyItsTasksAndATaskWhoseDeviceLeftIsRemovedOnce()
    {
        var inside = await AddDeviceAsync("ACCC8E000001", tags: [Tag]);
        var leaving = await AddDeviceAsync("ACCC8E000002", tags: [Tag]);
        var outside = await AddDeviceAsync("ACCC8E000003");
        var (_, _, view) = await GroupUserAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Register(new DelegateTaskPlugin("t.watch", async (ctx, device, ct) =>
        {
            if (device.Id == leaving.Id)
            {
                started.TrySetResult();
                await gate.Task.WaitAsync(ct);
                ctx.ReportProgress(50, "Half");
            }
        }));
        await using var engine = Engine();
        var running = (await engine.RunAsync("t.watch", [leaving.Id], null, "system", Ct))[0];
        await started.Task.WaitAsync(Eventually.DefaultTimeout);

        var (snapshot, subscription) = await engine.SubscribeWithSnapshotAsync(view, 100, Ct);
        using (subscription)
        {
            Assert.Equal([running], snapshot.Select(t => t.Id));

            var outsideTask = (await engine.RunAsync("t.watch", [outside.Id], null, "system", Ct))[0];
            await engine.WaitForCompletionAsync(outsideTask, Ct);
            await Tags.SetDeviceTagsAsync(await Views.System.GetAsync([leaving.Id], Ct), [], [Tag], Ct);
            gate.SetResult();
            await engine.WaitForCompletionAsync(running, Ct);
            var insideTask = (await engine.RunAsync("t.watch", [inside.Id], null, "system", Ct))[0];
            await engine.WaitForCompletionAsync(insideTask, Ct);

            var received = new List<TaskChange>();
            using var cts = new CancellationTokenSource(Eventually.DefaultTimeout);
            await foreach (var change in subscription.ReadAllAsync(cts.Token))
            {
                received.Add(change);
                if (change.Task.Id == insideTask && change.Task.State == TaskState.Done)
                {
                    break;
                }
            }

            Assert.DoesNotContain(received, c => c.Task.Id == outsideTask);
            var left = received.Where(c => c.Task.Id == running).ToList();
            Assert.Equal([TaskChangeKind.Removed], left.Select(c => c.Kind));
            Assert.Equal(TaskChangeKind.Added, received.First(c => c.Task.Id == insideTask).Kind);
        }
    }

    [Fact]
    public async Task TheWatchOfAUserWithAllDevicesPassesEveryChange()
    {
        var device = await AddDeviceAsync("ACCC8E000001");
        Register(new DelegateTaskPlugin("t.watch", (_, _, _) => Task.CompletedTask));
        await using var engine = Engine();
        var all = await Views.ForScopeAsync(DeviceScope.All, "boss", Ct);

        var (snapshot, subscription) = await engine.SubscribeWithSnapshotAsync(all, null, Ct);
        using (subscription)
        {
            Assert.True(subscription.PassesEverything);
            Assert.Empty(snapshot);
            var removedDevice = (await engine.RunAsync("t.watch", [Guid.NewGuid()], null, "system", Ct))[0];
            await engine.WaitForCompletionAsync(removedDevice, Ct);
            using var cts = new CancellationTokenSource(Eventually.DefaultTimeout);
            await foreach (var change in subscription.ReadAllAsync(cts.Token))
            {
                if (change.Task.Id == removedDevice && change.Task.State.IsTerminal())
                {
                    break;
                }
            }
        }

        Assert.NotNull(device);
    }
}
