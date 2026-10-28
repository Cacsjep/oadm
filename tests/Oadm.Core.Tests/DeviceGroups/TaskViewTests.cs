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

    private void Register(DelegateTaskPlugin plugin, string minSdk = SdkInfo.Version) =>
        Assert.True(_registry.RegisterTaskPlugin(plugin, new PluginOrigin("test." + plugin.Id, "1.0.0", null) { MinSdkVersion = minSdk }));

    private async Task<AuthenticatedCaller> AllDevicesCallerAsync() =>
        CallerOf(await Users.AddAsync("boss", "pw", BuiltInRoles.OperatorId, DeviceScope.All, Ct));

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
    public async Task AGroupUsersRunOfAPackageBeforeSdk040IsRefusedWholeAndAllDevicesRunIt()
    {
        var inside = await AddDeviceAsync("ACCC8E000001", tags: [Tag]);
        var (_, caller, view) = await GroupUserAsync();
        var ran = 0;
        Register(new DelegateTaskPlugin("t.old", (_, _, _) =>
        {
            Interlocked.Increment(ref ran);
            return Task.CompletedTask;
        }), minSdk: "0.3.0");
        await using var engine = Engine();

        var viewed = await view.GetAsync([inside.Id], Ct);
        var refused = await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => engine.RunAsync("t.old", viewed, null, TaskCaller.Of(caller), Ct));
        Assert.Equal(DeviceAccessRefusedException.PluginNeedsAllDevices, refused.Message);
        Assert.Empty(await engine.ListAsync(Ct));

        // call.Tasks of the group user's call is refused the same way.
        var call = engine.ForView(view, TaskCaller.Of(caller));
        await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => call.RunAsync("t.old", [inside.Id], null, "x", Ct));

        var boss = await AllDevicesCallerAsync();
        var ids = await engine.RunAsync("t.old", await (await Views.ForAsync(boss, Ct)).GetAsync([inside.Id], Ct), null, TaskCaller.Of(boss), Ct);
        await engine.WaitForCompletionAsync(ids[0], Ct);
        Assert.Equal(TaskState.Done, (await engine.GetAsync(ids[0], Ct))!.State);
        Assert.Equal(1, ran);
    }

    [Fact]
    public async Task AGroupOwnersTaskRunsUnderAPluginCallThatRefusesTheFullAccesses()
    {
        var inside = await AddDeviceAsync("ACCC8E000001", tags: [Tag]);
        var (_, caller, view) = await GroupUserAsync();
        var marks = new System.Collections.Concurrent.ConcurrentQueue<(string User, PluginCallMark? Mark)>();
        Register(new DelegateTaskPlugin("t.mark", (ctx, _, _) =>
        {
            marks.Enqueue((ctx.Caller?.UserName ?? "system", CallerContext.PluginCall));
            return Task.CompletedTask;
        }));
        await using var engine = Engine();

        var groupRun = await engine.RunAsync("t.mark", await view.GetAsync([inside.Id], Ct), null, TaskCaller.Of(caller), Ct);
        await engine.WaitForCompletionAsync(groupRun[0], Ct);
        var adminCaller = await AllDevicesCallerAsync();
        var adminRun = await engine.RunAsync("t.mark", await (await Views.ForAsync(adminCaller, Ct)).GetAsync([inside.Id], Ct), null, TaskCaller.Of(adminCaller), Ct);
        await engine.WaitForCompletionAsync(adminRun[0], Ct);

        var seen = marks.ToArray();
        Assert.Equal(2, seen.Length);
        Assert.Equal("anna", seen[0].User);
        Assert.False(seen[0].Mark!.HasAllDevices);
        Assert.False(seen[0].Mark!.SystemAllowed);
        Assert.Equal(view.ScopeKey, seen[0].Mark!.ScopeKey);
        Assert.Null(seen[1].Mark);
    }

    [Fact]
    public async Task TheTaskIdsOfARunPairWithTheRequestedDevicesInTheirOrder()
    {
        var a = await AddDeviceAsync("ACCC8E000001", tags: [Tag]);
        var b = await AddDeviceAsync("ACCC8E000002", tags: [Tag]);
        var c = await AddDeviceAsync("ACCC8E000003", tags: [Tag]);
        var (_, caller, view) = await GroupUserAsync();
        Register(new DelegateTaskPlugin("t.order", (_, _, _) => Task.CompletedTask));
        await using var engine = Engine();
        Guid[] request = [c.Id, a.Id, b.Id];

        var ids = await engine.RunAsync("t.order", await view.GetAsync(request, Ct), null, TaskCaller.Of(caller), Ct);
        var viaCall = await engine.ForView(view, TaskCaller.Of(caller)).RunAsync("t.order", request, null, "x", Ct);

        foreach (var run in new[] { ids, viaCall })
        {
            Assert.Equal(request.Length, run.Count);
            for (var i = 0; i < request.Length; i++)
            {
                await engine.WaitForCompletionAsync(run[i], Ct);
                Assert.Equal(request[i], (await engine.GetAsync(run[i], Ct))!.Devices[0].DeviceId);
            }
        }
    }

    [Fact]
    public async Task ATasksAddressCheckReadsTheRoutesAddressesAndAnswersForDevicesOutsideTheViewToo()
    {
        var self = await AddDeviceAsync("ACCC8E000011", tags: [Tag]);
        await AddDeviceAsync("ACCC8E000012"); // the same route, outside the group
        await AddDeviceAsync("ACCC8E000013", siteId: Sites.Add("Warehouse")); // another route
        var byName = await AddDeviceAsync("ACCC8E000014");
        await Devices.UpdateAsync(byName.Id, d => d.Address = "cam14.example", Ct);
        var (_, caller, view) = await GroupUserAsync();
        IReadOnlySet<string>? used = null;
        Register(new DelegateTaskPlugin("t.addresses", async (ctx, _, ct) =>
            used = await ctx.ManagedAddressesAsync(["10.0.0.11", "10.0.0.12", "10.0.0.13", "cam14.example"], ct)));
        await using var engine = Engine();

        var ids = await engine.RunAsync("t.addresses", await view.GetAsync([self.Id], Ct), null, TaskCaller.Of(caller), Ct);
        await engine.WaitForCompletionAsync(ids[0], Ct);

        // Only the other device on the route; never the task's own one, another site's, or a host name.
        Assert.Equal(["10.0.0.12"], used!);
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
    public async Task AQueuedTaskWhoseDeviceWasRemovedSaysTheSameAsOneOutsideTheGroup()
    {
        var first = await AddDeviceAsync("ACCC8E000001", tags: [Tag]);
        var removed = await AddDeviceAsync("ACCC8E000002", tags: [Tag]);
        var (_, caller, view) = await GroupUserAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Register(new DelegateTaskPlugin("t.gated", async (_, device, ct) =>
        {
            started.TrySetResult();
            await gate.Task.WaitAsync(ct);
        })
        { Limit = 1 });
        await using var engine = Engine();

        var ids = await engine.RunAsync("t.gated", await view.GetAsync([first.Id, removed.Id], Ct), null, TaskCaller.Of(caller), Ct);
        await started.Task.WaitAsync(Eventually.DefaultTimeout);
        await Devices.RemoveAsync(removed.Id, Ct);
        gate.SetResult();
        await engine.WaitForCompletionAsync(ids[1], Ct);

        var task = await engine.GetAsync(ids[1], Ct);
        Assert.Equal(TaskState.Failed, task!.State);
        Assert.Equal(DeviceMessages.NoLongerAvailable(1), task.Devices[0].Message);
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
    public async Task TheCallsRunnerTellsOnlyOwnTasksOneByOneAndTheViewsTasksTogether()
    {
        var inside = await AddDeviceAsync("ACCC8E000001", tags: [Tag]);
        var outside = await AddDeviceAsync("ACCC8E000002");
        var (_, anna, annaView) = await GroupUserAsync();
        var ben = await Users.AddAsync("ben", "pw", BuiltInRoles.OperatorId, DeviceScope.Group(annaView.GroupId!.Value), Ct);
        var benCaller = CallerOf(ben);
        var benView = await Views.ForAsync(benCaller, Ct);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Register(new DelegateTaskPlugin("t.hold", async (_, _, ct) => await gate.Task.WaitAsync(ct)));
        await using var engine = Engine();
        var annaTasks = engine.ForView(annaView, TaskCaller.Of(anna));
        var benTasks = engine.ForView(benView, TaskCaller.Of(benCaller));

        var annas = (await annaTasks.RunAsync("t.hold", [inside.Id], null, "x", Ct))[0];
        var system = (await engine.RunAsync("t.hold", [outside.Id], null, "plugin", Ct))[0];

        Assert.True(annaTasks.IsActive(annas));
        Assert.Null(benTasks.IsActive(annas)); // another user's task: cannot tell, never false
        Assert.Null(benTasks.IsActive(system));
        Assert.False(benTasks.IsActive(Guid.NewGuid()));
        Assert.True(await benTasks.AnyActiveAsync([annas], Ct)); // a task of the view, whoever started it
        Assert.Null(await benTasks.AnyActiveAsync([system], Ct)); // a device outside the view
        Assert.True(await engine.ForView(Views.System, TaskCaller.Of(anna)).AnyActiveAsync([system], Ct));

        gate.SetResult();
        await engine.WaitForCompletionAsync(annas, Ct);
        await engine.WaitForCompletionAsync(system, Ct);

        Assert.False(benTasks.IsActive(annas));
        Assert.False(await benTasks.AnyActiveAsync([annas, system], Ct));
    }

    [Fact]
    public async Task TheOutcomeOfAnEndedTaskIsToldForTheTasksOfTheView()
    {
        var inside = await AddDeviceAsync("ACCC8E000001", tags: [Tag]);
        var outside = await AddDeviceAsync("ACCC8E000002");
        var (_, anna, annaView) = await GroupUserAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Register(new DelegateTaskPlugin("t.fail", async (ctx, _, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            throw new InvalidOperationException("The application did not start.");
        }));
        Register(new DelegateTaskPlugin("t.warn", (ctx, _, _) =>
        {
            ctx.ReportWarning("No license key for this device.");
            return Task.CompletedTask;
        }));
        await using var engine = Engine();
        var annaTasks = engine.ForView(annaView, TaskCaller.Of(anna));
        var failed = (await engine.RunAsync("t.fail", [inside.Id], null, "plugin", Ct))[0];
        var hidden = (await engine.RunAsync("t.warn", [outside.Id], null, "plugin", Ct))[0];

        Assert.Null(await engine.GetOutcomeAsync(failed, Ct)); // still active
        gate.SetResult();
        await engine.WaitForCompletionAsync(failed, Ct);
        await engine.WaitForCompletionAsync(hidden, Ct);

        var outcome = await annaTasks.GetOutcomeAsync(failed, Ct); // a task of the view, whoever started it
        Assert.Equal(Oadm.Sdk.Tasks.TaskEndState.Failed, outcome?.State);
        Assert.Equal(inside.Id, outcome!.DeviceId);
        Assert.Contains("The application did not start.", outcome.Message, StringComparison.Ordinal);
        Assert.Null(await annaTasks.GetOutcomeAsync(hidden, Ct)); // a device outside the view
        Assert.Null(await annaTasks.GetOutcomeAsync(Guid.NewGuid(), Ct));
        var warned = await engine.ForView(Views.System, TaskCaller.Of(anna)).GetOutcomeAsync(hidden, Ct);
        Assert.Equal(Oadm.Sdk.Tasks.TaskEndState.DoneWithWarnings, warned?.State);
        Assert.Equal("No license key for this device.", warned!.Message);
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

    private static async Task<List<TaskChange>> ReadUntilAsync(TaskViewSubscription subscription, Func<List<TaskChange>, bool> done)
    {
        var received = new List<TaskChange>();
        using var cts = new CancellationTokenSource(Eventually.DefaultTimeout);
        await foreach (var change in subscription.ReadAllAsync(cts.Token))
        {
            received.Add(change);
            if (done(received))
            {
                break;
            }
        }

        return received;
    }

    private static bool Has(List<TaskChange> changes, TaskChangeKind kind, Guid taskId) =>
        changes.Exists(c => c.Kind == kind && c.Task.Id == taskId);

    [Fact]
    public async Task TheWatchOfAGroupRemovesTheTasksOfADeviceThatLeftAndAddsTheHistoryOfOneThatEnteredAtOnce()
    {
        var inside = await AddDeviceAsync("ACCC8E000001", tags: [Tag]);
        var leaving = await AddDeviceAsync("ACCC8E000002", tags: [Tag]);
        var entering = await AddDeviceAsync("ACCC8E000003");
        var (_, _, view) = await GroupUserAsync();
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        var ofInside = Finished(inside.Id, start);
        var ofLeaving = Finished(leaving.Id, start.AddSeconds(1));
        var ofEntering = Finished(entering.Id, start.AddSeconds(2));
        var olderOfEntering = Finished(entering.Id, start.AddSeconds(-5));
        await Db.Get<EfTaskStore>().AddRangeAsync([ofInside, ofLeaving, ofEntering, olderOfEntering], Ct);
        await using var engine = Engine();

        var (snapshot, subscription) = await engine.SubscribeWithSnapshotAsync(view, 100, Ct);
        using (subscription)
        {
            Assert.Equal([ofLeaving.Id, ofInside.Id], snapshot.Select(t => t.Id));

            // No task changes: the device moves alone bring the Removed and the Added.
            await Tags.SetDeviceTagsAsync(await Views.System.GetAsync([leaving.Id], Ct), [], [Tag], Ct);
            await Tags.SetDeviceTagsAsync(await Views.System.GetAsync([entering.Id], Ct), [Tag], [], Ct);
            var received = await ReadUntilAsync(subscription, r => Has(r, TaskChangeKind.Removed, ofLeaving.Id) && r.Count(c => c.Kind == TaskChangeKind.Added) == 2);

            Assert.Equal([TaskChangeKind.Removed], received.Where(c => c.Task.Id == ofLeaving.Id).Select(c => c.Kind));
            Assert.Equal([olderOfEntering.Id, ofEntering.Id], received.Where(c => c.Kind == TaskChangeKind.Added).Select(c => c.Task.Id));
            Assert.DoesNotContain(received, c => c.Task.Id == ofInside.Id);
            Assert.Equal(3, subscription.SentCount);
        }
    }

    [Fact]
    public async Task AnOlderDeviceChangeRaisedAfterANewerOneNeverPutsTheDeviceBackIntoTheWatch()
    {
        var device = await AddDeviceAsync("ACCC8E000001", tags: [Tag]);
        var (_, _, view) = await GroupUserAsync();
        var ofDevice = Finished(device.Id, DateTimeOffset.UtcNow.AddHours(-1));
        await Db.Get<EfTaskStore>().AddRangeAsync([ofDevice], Ct);
        using var outOfOrder = new OutOfOrderFeed(Views.Feed);
        await using var engine = Engine();

        var (snapshot, subscription) = await engine.SubscribeWithSnapshotAsync(view, 100, Ct);
        using (subscription)
        {
            Assert.Equal([ofDevice.Id], snapshot.Select(t => t.Id));

            // A copy with the tag is raised late, after the untagging reached the watch: the device stays out.
            var untagged = device.Clone();
            untagged.Tags = [];
            await outOfOrder.PublishAsync(
                new DeviceChange(DeviceChangeKind.Updated, device.Id, device.Clone()),
                new DeviceChange(DeviceChangeKind.Updated, device.Id, untagged));

            Assert.False(subscription.InView(device.Id));
            var received = await ReadUntilAsync(subscription, r => Has(r, TaskChangeKind.Removed, ofDevice.Id));
            var removed = Assert.Single(received);
            Assert.Equal(device.Id, removed.Task.DeviceId);
            Assert.Equal(0, subscription.SentCount);
        }
    }

    [Fact]
    public async Task TheWatchOfAGroupFollowsARemovedSiteAndANewRuleOfTheGroup()
    {
        var siteA = Sites.Add("Site A");
        var siteB = Sites.Add("Site B");
        var atA = await AddDeviceAsync("ACCC8E000001", siteId: siteA);
        var atB = await AddDeviceAsync("ACCC8E000002", siteId: siteB);
        var group = (await SaveGroupAsync("Sites", sites: [siteA])).Group;
        var user = await Users.AddAsync("anna", "pw", BuiltInRoles.OperatorId, DeviceScope.Group(group.Id), Ct);
        var view = await Views.ForAsync(CallerOf(user), Ct);
        var start = DateTimeOffset.UtcNow.AddHours(-1);
        var ofA = Finished(atA.Id, start);
        var ofB = Finished(atB.Id, start.AddSeconds(1));
        await Db.Get<EfTaskStore>().AddRangeAsync([ofA, ofB], Ct);
        await using var engine = Engine();

        var (snapshot, subscription) = await engine.SubscribeWithSnapshotAsync(view, 100, Ct);
        using (subscription)
        {
            Assert.Equal([ofA.Id], snapshot.Select(t => t.Id));

            // The group's rules take in site B: its history comes in.
            await SaveGroupAsync("Sites", sites: [siteA, siteB], id: group.Id, revision: group.Revision);
            Assert.Contains(await ReadUntilAsync(subscription, r => Has(r, TaskChangeKind.Added, ofB.Id)), c => c.Task.Id == ofB.Id);

            // Site A is removed: the catalog changed, its device's task leaves at once.
            Sites.Remove(siteA);
            var received = await ReadUntilAsync(subscription, r => Has(r, TaskChangeKind.Removed, ofA.Id));
            Assert.DoesNotContain(received, c => c.Task.Id == ofB.Id);
            Assert.Equal(1, subscription.SentCount);
        }
    }
}
