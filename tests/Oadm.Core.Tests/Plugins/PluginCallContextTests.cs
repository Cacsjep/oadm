using System.Collections.Concurrent;
using System.Text.Json;

using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Core.Devices;
using Oadm.Core.Persistence;
using Oadm.Core.Plugins;
using Oadm.Core.Tasks;
using Oadm.Core.Tests.DeviceGroups;
using Oadm.Core.Tests.Tasks;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

using Eventually = Oadm.Sdk.Testing.Eventually;

namespace Oadm.Core.Tests.Plugins;

/// <summary>
/// The core plugin host and the user's call (docs/spec/device-scope.md, Plugins): during the call of a user with a device
/// group the context's full accesses refuse (also in work the call starts), the call reaches only the user's view, methods
/// for users with all devices and packages before SDK 0.4.0 are refused before the plugin runs, and users with all devices
/// keep the old behaviour.
/// </summary>
public sealed class PluginCallContextTests : DeviceGroupTestBase
{
    private const string PluginId = "test.probe";

    [Fact]
    public async Task AGroupUsersCallRefusesTheContextsFullAccessesAndAnAllDevicesUserKeepsThem()
    {
        await using var rig = await RigAsync();
        foreach (var method in new[] { "ctx.devices", "ctx.vapix", "ctx.tasks", "ctx.streams", "ctx.system" })
        {
            var refused = await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => rig.InvokeAsync(rig.Anna, method, rig.Outside.Id));
            Assert.Equal("Only users with all devices can do this.", refused.Message);
        }

        // Nothing reached a device or started a task.
        Assert.Empty(rig.Vapix.Created);
        Assert.Empty(rig.Tasks.Runs);
        Assert.Empty(rig.Streams.Opened);

        // The same methods for a user with all devices work as before (plugins built on SDK 0.3.0 keep working for them).
        Assert.Equal("2", await rig.InvokeAsync(rig.Admin, "ctx.devices", null));
        await rig.InvokeAsync(rig.Admin, "ctx.vapix", rig.Outside.Id);
        await rig.InvokeAsync(rig.Admin, "ctx.tasks", rig.Outside.Id);
        await rig.InvokeAsync(rig.Admin, "ctx.streams", rig.Outside.Id);
        Assert.Equal("2", await rig.InvokeAsync(rig.Admin, "ctx.system", null));
        Assert.Equal([rig.Outside.Id], rig.Vapix.Created);

        // Outside any call (background work) the context is the full, system view.
        Assert.Equal(2, (await rig.Plugin.Context!.Devices.ListAsync(Ct)).Count);
    }

    [Fact]
    public async Task WorkAPluginStartsInsideAGroupUsersCallKeepsTheCallerAndIsRefused()
    {
        await using var rig = await RigAsync();
        await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => rig.InvokeAsync(rig.Anna, "taskrun", null));

        // A fire-and-forget Task.Run started in the call is refused too, also after the call ended.
        Assert.Equal("started", await rig.InvokeAsync(rig.Anna, "fireandforget", null));
        var outcome = await rig.Plugin.FireAndForget.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.IsType<DeviceAccessRefusedException>(outcome);

        // For a user with all devices the same work runs.
        Assert.Equal("2", await rig.InvokeAsync(rig.Admin, "taskrun", null));
    }

    [Fact]
    public async Task TheCallReachesOnlyTheUsersViewAndAnOutsideDeviceAnswersLikeAnUnknownOne()
    {
        await using var rig = await RigAsync();
        Assert.Equal("1", await rig.InvokeAsync(rig.Anna, "call.list", null));
        Assert.Equal("2", await rig.InvokeAsync(rig.Admin, "call.list", null));
        Assert.Equal("found", await rig.InvokeAsync(rig.Anna, "call.find", rig.Inside.Id));
        Assert.Equal("none", await rig.InvokeAsync(rig.Anna, "call.find", rig.Outside.Id));

        foreach (var method in new[] { "call.vapix", "call.tasks", "call.streams" })
        {
            var outside = await Assert.ThrowsAsync<DevicesNotAvailableException>(() => rig.InvokeAsync(rig.Anna, method, rig.Outside.Id));
            var unknown = await Assert.ThrowsAsync<DevicesNotAvailableException>(() => rig.InvokeAsync(rig.Anna, method, Guid.NewGuid()));
            Assert.Equal(DeviceMessages.NoLongerAvailable(1), outside.Message);
            Assert.Equal(outside.Message, unknown.Message);
        }

        // A batch with one outside device is refused whole: nothing starts.
        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => rig.InvokeAsync(rig.Anna, "call.tasks.both", $"{rig.Inside.Id},{rig.Outside.Id}"));
        Assert.Empty(rig.Vapix.Created);
        Assert.Empty(rig.Tasks.Runs);
        Assert.Empty(rig.Streams.Opened);

        await rig.InvokeAsync(rig.Anna, "call.vapix", rig.Inside.Id);
        await rig.InvokeAsync(rig.Anna, "call.tasks", rig.Inside.Id);
        await rig.InvokeAsync(rig.Anna, "call.streams", rig.Inside.Id);
        Assert.Equal([rig.Inside.Id], rig.Vapix.Created);
        Assert.Equal([rig.Inside.Id], Assert.Single(rig.Tasks.Runs));
        Assert.Equal([rig.Inside.Id], rig.Streams.Opened);

        // The user's tasks: cancel only those the user started through a call.
        var mine = rig.Tasks.Ids.Single();
        Assert.Equal("True", await rig.InvokeAsync(rig.Anna, "call.cancel", mine));
        Assert.Equal("False", await rig.InvokeAsync(rig.Anna, "call.cancel", Guid.NewGuid()));
        Assert.Equal("True", await rig.InvokeAsync(rig.Admin, "call.cancel", Guid.NewGuid()));

        // The call tells who calls.
        var who = JsonDocument.Parse((await rig.InvokeAsync(rig.Anna, "call.who", null))!).RootElement;
        Assert.Equal("anna", who.GetProperty("user").GetString());
        Assert.False(who.GetProperty("all").GetBoolean());
        Assert.Equal(rig.GroupId.ToString(), who.GetProperty("scope").GetString());
        Assert.Equal(SessionIds.Of(rig.Anna.Caller.TokenHash), who.GetProperty("session").GetString());
        Assert.Equal(ICallContext.AllDevicesScopeKey, JsonDocument.Parse((await rig.InvokeAsync(rig.Admin, "call.who", null))!).RootElement.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task AMethodForUsersWithAllDevicesIsRefusedForAGroupUserBeforeThePluginRuns()
    {
        await using var rig = await RigAsync();
        var refused = await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => rig.InvokeAsync(rig.Anna, "save", null));
        Assert.Equal(DeviceGroupTexts.AllDevicesOnly, refused.Message);
        Assert.DoesNotContain("save", rig.Plugin.Ran);

        // A plugin that throws when asked is refused too (fail closed).
        await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => rig.InvokeAsync(rig.Anna, "broken-declaration", null));
        Assert.DoesNotContain("broken-declaration", rig.Plugin.Ran);

        Assert.Equal("saved", await rig.InvokeAsync(rig.Admin, "save", null));
    }

    [Fact]
    public async Task APackageBeforeSdk040IsRefusedForGroupUsersOnly()
    {
        await using var rig = await RigAsync(minSdk: "0.3.0");
        var refused = await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => rig.InvokeAsync(rig.Anna, "call.list", null));
        Assert.Equal("Only users with all devices can use this plugin.", refused.Message);
        Assert.Empty(rig.Plugin.Ran);
        Assert.False(rig.Host.IsCallable(rig.Anna, PluginId));

        Assert.Equal("2", await rig.InvokeAsync(rig.Admin, "ctx.devices", null));
        Assert.True(rig.Host.IsCallable(rig.Admin, PluginId));
    }

    [Fact]
    public async Task OnlyAMethodWithAReasonMayUseTheSystemAccessInAGroupUsersCall()
    {
        await using var rig = await RigAsync();
        Assert.Equal("2", await rig.InvokeAsync(rig.Anna, "system.count", null));

        // ctx.Devices stays refused there; ctx.System is refused in every other method.
        await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => rig.InvokeAsync(rig.Anna, "system.count.ctx", null));
        await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => rig.InvokeAsync(rig.Anna, "ctx.system", null));
    }

    [Fact]
    public async Task AJobBelongsToItsUserRunsWithoutTheCallerAndKeepsTheOwnersDevices()
    {
        await using var rig = await RigAsync();
        var annasJob = (await rig.InvokeAsync(rig.Anna, "job.start", null))!;
        var job = await rig.Plugin.JobDone.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(job.Caller); // the background boundary: no caller in the job
        Assert.IsType<DeviceAccessRefusedException>(job.ContextAccess); // ctx.* refuses in a group user's job too
        Assert.Equal(1, job.OwnerDevices); // the owner's view

        Assert.Equal("found", await rig.InvokeAsync(rig.Anna, "job.find", annasJob));
        Assert.Equal(1, await rig.JobCountAsync(rig.Anna));

        // Another user's id answers like an unknown id.
        var ben = await rig.CallerAsync("ben", DeviceScope.Group(rig.GroupId));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.InvokeAsync(ben, "job.find", annasJob));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.InvokeAsync(ben, "job.find", "0123456789abcdef0123456789abcdef"));
        Assert.Equal(0, await rig.JobCountAsync(ben));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.InvokeAsync(rig.Admin, "job.find", annasJob));

        // An all-devices user's job uses the full context (background work).
        rig.Plugin.JobDone = new TaskCompletionSource<JobOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        await rig.InvokeAsync(rig.Admin, "job.start", null);
        var adminJob = await rig.Plugin.JobDone.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Null(adminJob.ContextAccess);
        Assert.Equal(2, adminJob.OwnerDevices);
    }

    [Fact]
    public async Task JobsEndWhenTheirOwnerChangesOrThePluginStops()
    {
        await using var rig = await RigAsync();
        rig.Plugin.HoldJobs = true;
        await rig.InvokeAsync(rig.Anna, "job.start", null);
        await rig.InvokeAsync(rig.Admin, "job.start", null);
        Assert.Equal(2, rig.Host.Jobs.RunningCount(PluginId));

        rig.Host.Jobs.CancelOwner(rig.Anna.Caller.UserId);
        await Eventually.TrueAsync(() => rig.Host.Jobs.RunningCount(PluginId) == 1, "anna's job ended");

        rig.Host.Jobs.CancelSession(rig.Admin.Caller.SessionId);
        await Eventually.TrueAsync(() => rig.Host.Jobs.RunningCount(PluginId) == 0, "the admin session's job ended");

        // The ended session starts nothing more; a new login does.
        await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => rig.InvokeAsync(rig.Admin, "job.start", null));
        await rig.InvokeAsync(rig.Fresh(rig.Admin, "hash-new-login"), "job.start", null);
        Assert.Equal(1, rig.Host.Jobs.RunningCount(PluginId));
        await rig.Host.StopAllAsync(Ct);
        Assert.Equal(0, rig.Host.Jobs.RunningCount(PluginId));
    }

    [Fact]
    public async Task AJobStartedInACallWhoseAccessChangedMeanwhileIsRefused()
    {
        await using var rig = await RigAsync();
        var berta = await rig.CallerAsync("berta", DeviceScope.Group(rig.GroupId));

        // The user got other devices (or another role) while the call ran: the call's job would keep the old access.
        rig.Host.Jobs.CancelOwner(rig.Anna.Caller.UserId);
        var refused = await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => rig.InvokeAsync(rig.Anna, "job.start", null));
        Assert.Equal(DeviceGroupTexts.DevicesChanged, refused.Message);
        Assert.Equal(0, rig.Host.Jobs.RunningCount(PluginId));
        Assert.Equal(0, await rig.JobCountAsync(rig.Anna));

        // Another user's change refuses nothing; new rules of the group refuse its users' calls that began before.
        Assert.NotNull(await rig.InvokeAsync(berta, "job.start", null));
        rig.Host.Jobs.CancelGroup(rig.GroupId);
        await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => rig.InvokeAsync(berta, "job.start", null));
        Assert.NotNull(await rig.InvokeAsync(rig.Admin, "job.start", null));

        // The next call (after the change) starts jobs again.
        Assert.NotNull(await rig.InvokeAsync(rig.Fresh(rig.Anna), "job.start", null));
    }

    [Fact]
    public async Task AJobBelongsToItsUsersSessionAndAnEndedSessionForgetsItsFinishedJobs()
    {
        await using var rig = await RigAsync();
        rig.Plugin.JobDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = (await rig.InvokeAsync(rig.Anna, "job.start", null))!;
        await rig.Plugin.JobDone.Task.WaitAsync(Eventually.DefaultTimeout);
        await Eventually.TrueAsync(() => rig.Host.Jobs.RunningCount(PluginId) == 0, "the job ended");
        var otherLogin = rig.Fresh(rig.Anna, "hash-second-login");

        Assert.Equal("found", await rig.InvokeAsync(rig.Anna, "job.find", id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.InvokeAsync(otherLogin, "job.find", id));
        Assert.Equal(0, await rig.JobCountAsync(otherLogin));

        rig.Host.Jobs.CancelSession(rig.Anna.Caller.SessionId);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.InvokeAsync(rig.Anna, "job.find", id));
        Assert.Equal(0, await rig.JobCountAsync(rig.Anna));
    }

    [Fact]
    public async Task ASharedJobIsFoundAndStoppedByEveryUserOfItsScopeOnly()
    {
        await using var rig = await RigAsync();
        var berta = await rig.CallerAsync("berta", DeviceScope.Group(rig.GroupId));
        var shared = (await rig.InvokeAsync(rig.Anna, "job.shared", null))!;
        rig.Plugin.HoldJobs = true;
        var own = (await rig.InvokeAsync(rig.Anna, "job.start", null))!;

        Assert.Equal("found", await rig.InvokeAsync(berta, "job.find", shared));
        Assert.Equal(1, await rig.JobCountAsync(berta));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.InvokeAsync(berta, "job.find", own)); // not shared: the owner only
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.InvokeAsync(rig.Admin, "job.find", shared)); // another scope
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.InvokeAsync(rig.Admin, "job.cancel", shared));

        Assert.Equal("cancelled", await rig.InvokeAsync(berta, "job.cancel", shared));
        await Eventually.TrueAsync(() => rig.Host.Jobs.RunningCount(PluginId) == 1, "the shared job ended");

        // A shared job still ends with its owner's access.
        var second = (await rig.InvokeAsync(rig.Anna, "job.shared", null))!;
        rig.Host.Jobs.CancelOwner(rig.Anna.Caller.UserId);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.InvokeAsync(berta, "job.find", second));
        await Eventually.TrueAsync(() => rig.Host.Jobs.RunningCount(PluginId) == 0, "every job ended");
    }

    [Fact]
    public async Task AJobStartedWithoutTheHostsCallIsRefused()
    {
        await using var rig = await RigAsync();
        var fake = Oadm.Sdk.Testing.FakeCallContext.AllDevices(new Oadm.Sdk.Testing.FakeCoreContext());
        Assert.Throws<ArgumentException>(() => rig.Plugin.Context!.Jobs!.Start(fake, "x", (_, _) => Task.CompletedTask));
        Assert.Null(rig.Plugin.Context!.Jobs!.Find(fake, "x"));
    }

    [Fact]
    public async Task TheBackgroundBoundaryClearsThePluginCall()
    {
        await using var rig = await RigAsync();
        Assert.Equal("2", await rig.InvokeAsync(rig.Anna, "suppressed", null));
    }

    [Fact]
    public async Task DeviceScopesFollowTheGroupsAndADeletedGroupCoversNothing()
    {
        await using var rig = await RigAsync();
        var scopes = rig.Plugin.Context!.DeviceScopes!;
        var key = rig.GroupId.ToString();
        var changed = new ConcurrentQueue<string>();
        scopes.Changed += (_, k) => changed.Enqueue(k);

        Assert.True(scopes.Exists(ICallContext.AllDevicesScopeKey));
        Assert.True(scopes.Exists(key));
        Assert.False(scopes.Exists(Guid.NewGuid().ToString()));
        Assert.False(scopes.Exists("none"));
        Assert.Equal("All devices", scopes.NameOf(ICallContext.AllDevicesScopeKey));
        Assert.Equal("B", scopes.NameOf(key));
        Assert.True(scopes.Contains(key, rig.Inside));
        Assert.False(scopes.Contains(key, rig.Outside));
        Assert.True(scopes.Contains(ICallContext.AllDevicesScopeKey, rig.Outside));
        Assert.Equal([rig.Inside.Id], (await scopes.Devices(key).ListAsync(Ct)).Select(d => d.Id));
        Assert.Null(await scopes.Devices(key).FindAsync(rig.Outside.Id, Ct));
        Assert.Equal(2, (await scopes.Devices(ICallContext.AllDevicesScopeKey).ListAsync(Ct)).Count);
        Assert.True(scopes.InAnyGroup(rig.Inside));
        Assert.False(scopes.InAnyGroup(rig.Outside));

        await Groups.DeleteAsync(rig.GroupId, Ct);

        Assert.Contains(key, changed);
        Assert.False(scopes.Exists(key));
        Assert.Null(scopes.NameOf(key));
        Assert.False(scopes.Contains(key, rig.Inside));
        Assert.Empty(await scopes.Devices(key).ListAsync(Ct));
        Assert.False(scopes.InAnyGroup(rig.Inside));
    }

    [Fact]
    public async Task DeviceScopesInAGroupUsersCallAnswerOnlyTheCallersOwnScope()
    {
        await using var rig = await RigAsync();
        var own = rig.GroupId.ToString();
        var other = (await SaveGroupAsync("Other", thisServer: true)).Group.Id.ToString();

        Assert.Equal("1", await rig.InvokeAsync(rig.Anna, "scopes.devices", own));
        Assert.Equal("True", await rig.InvokeAsync(rig.Anna, "scopes.exists", own));
        Assert.Equal("B", await rig.InvokeAsync(rig.Anna, "scopes.name", own));
        foreach (var key in new[] { ICallContext.AllDevicesScopeKey, other, Guid.NewGuid().ToString() })
        {
            foreach (var method in new[] { "scopes.devices", "scopes.exists", "scopes.name" })
            {
                var refused = await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => rig.InvokeAsync(rig.Anna, method, key));
                Assert.Equal("Only users with all devices can do this.", refused.Message);
            }
        }

        // A method that declares its system access asks whether devices it has from ctx.System are in any scope; another
        // scope's device list and name stay refused.
        Assert.Equal("True,2,2", await rig.InvokeAsync(rig.Anna, "system.membership", ICallContext.AllDevicesScopeKey));
        Assert.Equal("True,1,1", await rig.InvokeAsync(rig.Anna, "system.membership", other)); // "This server": the device without a site
        await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => rig.InvokeAsync(rig.Anna, "system.scopes", ICallContext.AllDevicesScopeKey));
        await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => rig.InvokeAsync(rig.Anna, "system.name", other));

        // The user's job: the same rule.
        await rig.InvokeAsync(rig.Anna, "job.scopes", ICallContext.AllDevicesScopeKey);
        Assert.IsType<DeviceAccessRefusedException>(await rig.Plugin.JobResult.Task.WaitAsync(Eventually.DefaultTimeout));
        rig.Plugin.JobResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await rig.InvokeAsync(rig.Anna, "job.scopes", own);
        Assert.Equal(1, await rig.Plugin.JobResult.Task.WaitAsync(Eventually.DefaultTimeout));

        // A user with all devices and background work reach every scope.
        Assert.Equal("2", await rig.InvokeAsync(rig.Admin, "scopes.devices", ICallContext.AllDevicesScopeKey));
        Assert.Equal("1", await rig.InvokeAsync(rig.Admin, "scopes.devices", own));
        Assert.Equal(2, (await rig.Plugin.Context!.DeviceScopes!.Devices(ICallContext.AllDevicesScopeKey).ListAsync(Ct)).Count);

        // A repository made in background work is refused when read in another group's call.
        rig.Plugin.Repository = rig.Plugin.Context!.DeviceScopes!.Devices(ICallContext.AllDevicesScopeKey);
        await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => rig.InvokeAsync(rig.Anna, "scopes.kept", null));
    }

    [Fact]
    public async Task TasksOfACallAndOfAJobBelongToTheCallerWhateverOwnerThePluginNames()
    {
        await using var rig = await RigAsync(engine: true);
        var engine = rig.Engine!;

        var fromCall = Guid.Parse((await rig.InvokeAsync(rig.Anna, "call.tasks.spoof", rig.Inside.Id))!);
        await engine.WaitForCompletionAsync(fromCall, Ct);
        var callTask = (await engine.GetAsync(fromCall, Ct))!;
        Assert.Equal(rig.Anna.Caller.Owner, callTask.Owner);
        Assert.True(callTask.State == TaskState.Done, callTask.State + " " + callTask.Devices[0].Message);

        await rig.InvokeAsync(rig.Anna, "job.tasks", rig.Inside.Id);
        var fromJob = Assert.IsType<Guid>(await rig.Plugin.JobResult.Task.WaitAsync(Eventually.DefaultTimeout));
        await engine.WaitForCompletionAsync(fromJob, Ct);
        Assert.Equal(rig.Anna.Caller.Owner, (await engine.GetAsync(fromJob, Ct))!.Owner);

        // The task got the owner's call (and so the queue check of the owner's view) both times.
        Assert.Equal(2, rig.Plugin.TaskCallers.Count);
        Assert.All(rig.Plugin.TaskCallers, c =>
        {
            Assert.NotNull(c);
            Assert.Equal("anna", c!.UserName);
            Assert.Equal(rig.GroupId.ToString(), c.DeviceScopeKey);
        });

        // Outside devices are refused in the job too.
        rig.Plugin.JobResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await rig.InvokeAsync(rig.Anna, "job.tasks", rig.Outside.Id);
        Assert.IsType<DevicesNotAvailableException>(await rig.Plugin.JobResult.Task.WaitAsync(Eventually.DefaultTimeout));

        // A user with all devices: the caller is the owner too.
        var admins = Guid.Parse((await rig.InvokeAsync(rig.Admin, "call.tasks.spoof", rig.Outside.Id))!);
        await engine.WaitForCompletionAsync(admins, Ct);
        Assert.Equal(rig.Admin.Caller.Owner, (await engine.GetAsync(admins, Ct))!.Owner);
    }

    [Fact]
    public async Task AJobIsForgottenWhenItsOwnersDevicesChange()
    {
        await using var rig = await RigAsync();

        async Task<string> FinishedJobAsync()
        {
            rig.Anna = rig.Fresh(rig.Anna); // the user's next call, after the change
            rig.Plugin.JobDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var id = (await rig.InvokeAsync(rig.Anna, "job.start", null))!;
            await rig.Plugin.JobDone.Task.WaitAsync(Eventually.DefaultTimeout);
            Assert.Equal("found", await rig.InvokeAsync(rig.Anna, "job.find", id));
            return id;
        }

        // Another group for the user (or other devices, another role): even a finished job's result is gone.
        var first = await FinishedJobAsync();
        rig.Host.Jobs.CancelOwner(rig.Anna.Caller.UserId);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.InvokeAsync(rig.Anna, "job.find", first));
        Assert.Equal(0, await rig.JobCountAsync(rig.Anna));

        // New rules of the user's group.
        var second = await FinishedJobAsync();
        rig.Host.Jobs.CancelGroup(rig.GroupId);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.InvokeAsync(rig.Anna, "job.find", second));

        // The same user with the devices of another group finds nothing of the old group's jobs.
        var third = await FinishedJobAsync();
        var other = (await SaveGroupAsync("Other", thisServer: true)).Group;
        var moved = new PluginCaller(rig.Anna.Caller, await Views.ForScopeAsync(DeviceScope.Group(other.Id), "anna", Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.InvokeAsync(moved, "job.find", third));
        Assert.Equal(0, await rig.JobCountAsync(moved));
        Assert.Equal("found", await rig.InvokeAsync(rig.Anna, "job.find", third));
    }

    [Fact]
    public async Task WhenDevicesMoveInOrOutOfAGroupItsUsersFinishedJobsAreForgottenAndRunningOnesWhenTheyEnd()
    {
        await using var rig = await RigAsync();
        rig.Plugin.JobDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = (await rig.InvokeAsync(rig.Anna, "job.start", null))!;
        await rig.Plugin.JobDone.Task.WaitAsync(Eventually.DefaultTimeout);
        var admins = (await rig.InvokeAsync(rig.Admin, "job.start", null))!;
        await Eventually.TrueAsync(() => rig.Host.Jobs.RunningCount(PluginId) == 0, "the jobs ended");

        rig.Plugin.HoldJobs = true;
        rig.Plugin.JobGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Plugin.JobDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = (await rig.InvokeAsync(rig.Anna, "job.start", null))!;

        // A tag or site change moved devices into or out of the group (the coalesced signal of the device scopes).
        rig.Host.Jobs.ScopeDevicesChanged(rig.GroupId.ToString());

        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.InvokeAsync(rig.Anna, "job.find", finished));
        Assert.Equal("found", await rig.InvokeAsync(rig.Anna, "job.find", running)); // still running: its page goes on
        Assert.Equal("found", await rig.InvokeAsync(rig.Admin, "job.find", admins)); // all devices: untouched

        rig.Plugin.JobGate.SetResult();
        await rig.Plugin.JobDone.Task.WaitAsync(Eventually.DefaultTimeout);
        await Eventually.TrueAsync(() => rig.Host.Jobs.RunningCount(PluginId) == 0, "the held job ended");
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.InvokeAsync(rig.Anna, "job.find", running));
        Assert.Equal(0, await rig.JobCountAsync(rig.Anna));
    }

    private async Task<Rig> RigAsync(string minSdk = "0.4.0", bool engine = false)
    {
        var b = Sites.Add("Building B");
        var inside = await AddDeviceAsync("ACCC8E000001", siteId: b);
        var outside = await AddDeviceAsync("ACCC8E000002");
        var group = (await SaveGroupAsync("B", sites: [b])).Group;
        var registry = new PluginRegistry();
        var plugin = new ProbePlugin();
        Assert.True(registry.RegisterCorePlugin(plugin, new PluginOrigin(PluginId, "1.0.0", null) { MinSdkVersion = minSdk, DeclaredSiteSupport = Sdk.Sites.SiteSupport.Full }));
        var vapix = new RecordingVapix();
        var tasks = new RecordingTasks();
        var streams = new RecordingStreams();
        var scopes = new HostDeviceScopes(Groups, Views);
        TaskEngine? taskEngine = null;
        if (engine)
        {
            Assert.True(registry.RegisterTaskPlugin(new DelegateTaskPlugin("t.call", (ctx, _, _) =>
            {
                plugin.TaskCallers.Enqueue(ctx.Caller);
                return Task.CompletedTask;
            }), new PluginOrigin("test.tasks", "1.0.0", null) { MinSdkVersion = SdkInfo.Version, DeclaredSiteSupport = Sdk.Sites.SiteSupport.Full }));
            taskEngine = new TaskEngine(Db.Get<EfTaskStore>(), registry, Devices, new FakeVapixClientFactory()) { Views = Views };
        }

        var host = new CorePluginHost(registry, Devices, vapix, (ITaskRunner?)taskEngine ?? tasks, new InMemoryPluginSettingsProvider(), eventStreams: streams, deviceScopes: scopes, viewWatcher: Db.Get<DeviceViewWatcher>());
        await host.StartAllAsync(Ct);
        var rig = new Rig(this, host, scopes, plugin, vapix, tasks, streams, inside, outside, group.Id) { Engine = taskEngine };
        rig.Anna = await rig.CallerAsync("anna", DeviceScope.Group(group.Id));
        rig.Admin = await rig.CallerAsync("root", DeviceScope.All);
        return rig;
    }

    private sealed class Rig(PluginCallContextTests test, CorePluginHost host, HostDeviceScopes scopes, ProbePlugin plugin, RecordingVapix vapix, RecordingTasks tasks, RecordingStreams streams, Device inside, Device outside, Guid groupId) : IAsyncDisposable
    {
        public CorePluginHost Host { get; } = host;

        public ProbePlugin Plugin { get; } = plugin;

        public RecordingVapix Vapix { get; } = vapix;

        public RecordingTasks Tasks { get; } = tasks;

        public RecordingStreams Streams { get; } = streams;

        public Device Inside { get; } = inside;

        public Device Outside { get; } = outside;

        public Guid GroupId { get; } = groupId;

        public PluginCaller Anna { get; set; } = null!;

        public PluginCaller Admin { get; set; } = null!;

        public TaskEngine? Engine { get; init; }

        public async Task<PluginCaller> CallerAsync(string name, DeviceScope scope)
        {
            var user = await test.Users.AddAsync(name, "pw", BuiltInRoles.OperatorId, scope, Ct);
            var caller = test.CallerOf(user) with { AccessEpoch = Host.Jobs.Epochs.Capture() };
            return new PluginCaller(caller, await test.Views.ForAsync(caller, Ct));
        }

        /// <summary>The user's next call (the interceptor captures the access epoch again), optionally in another session.</summary>
        public PluginCaller Fresh(PluginCaller caller, string? tokenHash = null) =>
            new(caller.Caller with { AccessEpoch = Host.Jobs.Epochs.Capture(), TokenHash = tokenHash ?? caller.Caller.TokenHash }, caller.View);

        /// <summary>Like a gRPC call: the interceptor's caller backstop is set too.</summary>
        public async Task<string?> InvokeAsync(PluginCaller caller, string method, object? argument)
        {
            using (CallerContext.Use(caller.Caller))
            {
                return await Host.InvokeAsync(caller, PluginId, method, argument?.ToString(), Ct);
            }
        }

        public async Task<int> JobCountAsync(PluginCaller caller) => int.Parse((await InvokeAsync(caller, "job.count", null))!, System.Globalization.CultureInfo.InvariantCulture);

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            scopes.Dispose();
            if (Engine is not null)
            {
                await Engine.DisposeAsync();
            }
        }
    }

    internal sealed record JobOutcome(AuthenticatedCaller? Caller, Exception? ContextAccess, int OwnerDevices);

    /// <summary>A plugin whose methods each touch one access (the payload is a device id or a job id).</summary>
    private sealed class ProbePlugin : ICorePlugin
    {
        public string Id => PluginId;

        public string DisplayName => "Probe";

        public string IconKey => "plugin";

        public IReadOnlyList<ITaskPlugin> TaskPlugins => [];

        public ICorePluginContext? Context { get; private set; }

        public ConcurrentQueue<string> Ran { get; } = new();

        public TaskCompletionSource<Exception?> FireAndForget { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<JobOutcome> JobDone { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HoldJobs { get; set; }

        /// <summary>With <see cref="HoldJobs"/>: held jobs go on when it is set (none: until cancelled).</summary>
        public TaskCompletionSource? JobGate { get; set; }

        public ConcurrentQueue<ICallContext?> TaskCallers { get; } = new();

        public IDeviceRepository? Repository { get; set; }

        public TaskCompletionSource<object?> JobResult { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool RequiresAllDevices(string method) => method switch
        {
            "save" => true,
            "broken-declaration" => throw new InvalidOperationException("broken"),
            _ => false,
        };

        public string? SystemAccessReason(string method) => method is "system.count" or "system.count.ctx" or "system.scopes" or "system.membership" or "system.name" ? "counts every device, shows no device data" : null;

        public Task StartAsync(ICorePluginContext context, CancellationToken ct)
        {
            Context = context;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct) =>
            throw new InvalidOperationException("The host calls the overload with the call.");

        public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
        {
            Ran.Enqueue(method);
            var ctx = Context!;
            Guid Id() => Guid.Parse(payloadJson!);
            switch (method)
            {
                case "ctx.devices":
                    return (await ctx.Devices.ListAsync(ct)).Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case "ctx.vapix":
                    await ctx.Vapix.CreateAsync(Id(), ct);
                    return null;
                case "ctx.tasks":
                    await ctx.Tasks.RunAsync("t", [Id()], null, "x", ct);
                    return null;
                case "ctx.streams":
                    await using (await ctx.EventStreams!.OpenAsync(Id(), ct))
                    {
                        return null;
                    }

                case "ctx.system":
                case "system.count":
                    return (await ctx.System.Devices.ListAsync(ct)).Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case "system.count.ctx":
                    return (await ctx.Devices.ListAsync(ct)).Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case "taskrun":
                    return await Task.Run(async () => (await ctx.Devices.ListAsync(CancellationToken.None)).Count.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
                case "fireandforget":
                    _ = Task.Run(async () =>
                    {
                        await Task.Yield();
                        try
                        {
                            await ctx.Devices.ListAsync(CancellationToken.None);
                            FireAndForget.TrySetResult(null);
                        }
                        catch (Exception ex)
                        {
                            FireAndForget.TrySetResult(ex);
                        }
                    }, CancellationToken.None);
                    return "started";
                case "suppressed":
                    using (CallerContext.Suppress())
                    {
                        return (await ctx.Devices.ListAsync(ct)).Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    }

                case "call.list":
                    return (await caller.Devices.ListAsync(ct)).Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case "call.find":
                    return await caller.Devices.FindAsync(Id(), ct) is null ? "none" : "found";
                case "call.vapix":
                    await caller.Vapix.CreateAsync(Id(), ct);
                    return null;
                case "call.tasks":
                    await caller.Tasks.RunAsync("t", [Id()], null, caller.UserName, ct);
                    return null;
                case "call.tasks.both":
                    await caller.Tasks.RunAsync("t", [.. payloadJson!.Split(',').Select(Guid.Parse)], null, caller.UserName, ct);
                    return null;
                case "call.cancel":
                    return caller.Tasks.Cancel(Id()).ToString();
                case "call.streams":
                    await using (await caller.EventStreams!.OpenAsync(Id(), ct))
                    {
                        return null;
                    }

                case "call.who":
                    return JsonSerializer.Serialize(new { user = caller.UserName, all = caller.HasAllDevices, scope = caller.DeviceScopeKey, session = caller.SessionId });
                case "save":
                case "broken-declaration":
                    return "saved";
                case "job.start":
                    return ctx.Jobs!.Start(caller, "report", async (job, token) =>
                    {
                        if (HoldJobs)
                        {
                            await (JobGate?.Task ?? Task.Delay(Timeout.Infinite, token)).WaitAsync(token);
                        }

                        Exception? access = null;
                        try
                        {
                            await ctx.Devices.ListAsync(token);
                        }
                        catch (DeviceAccessRefusedException ex)
                        {
                            access = ex;
                        }

                        JobDone.TrySetResult(new JobOutcome(CallerContext.Current, access, (await job.Owner.Devices.ListAsync(token)).Count));
                    }).Id;
                case "scopes.devices":
                    return (await ctx.DeviceScopes!.Devices(payloadJson!).ListAsync(ct)).Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case "scopes.kept":
                    return (await Repository!.ListAsync(ct)).Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case "scopes.exists":
                    return ctx.DeviceScopes!.Exists(payloadJson!).ToString();
                case "scopes.name":
                    return ctx.DeviceScopes!.NameOf(payloadJson!);
                case "system.membership":
                    var everyDevice = await ctx.System.Devices.ListAsync(ct);
                    return string.Join(',', ctx.DeviceScopes!.Exists(payloadJson!), everyDevice.Count(d => ctx.DeviceScopes.Contains(payloadJson!, d)),
                        everyDevice.Count(d => ctx.DeviceScopes.Contains(payloadJson!, d.Id)));
                case "system.name":
                    return ctx.DeviceScopes!.NameOf(payloadJson!);
                case "system.scopes":
                    return (await ctx.DeviceScopes!.Devices(payloadJson!).ListAsync(ct)).Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case "call.tasks.spoof":
                    return (await caller.Tasks.RunAsync("t.call", [Id()], null, "spoofed", ct))[0].ToString();
                case "job.scopes":
                case "job.tasks":
                    var argument = payloadJson!;
                    return ctx.Jobs!.Start(caller, method, async (job, token) =>
                    {
                        try
                        {
                            JobResult.TrySetResult(method == "job.scopes"
                                ? (await ctx.DeviceScopes!.Devices(argument).ListAsync(token)).Count
                                : (await job.Owner.Tasks.RunAsync("t.call", [Guid.Parse(argument)], null, "spoofed", token))[0]);
                        }
                        catch (Exception ex)
                        {
                            JobResult.TrySetResult(ex);
                        }
                    }).Id;
                case "job.shared":
                    return ctx.Jobs!.StartShared(caller, "scan", (_, token) => Task.Delay(Timeout.Infinite, token)).Id;
                case "job.cancel":
                    (ctx.Jobs!.Find(caller, payloadJson!) ?? throw new KeyNotFoundException("The scan is no longer available.")).Cancel();
                    return "cancelled";
                case "job.find":
                    return ctx.Jobs!.Find(caller, payloadJson!) is null ? throw new KeyNotFoundException("The report is no longer available.") : "found";
                case "job.count":
                    return ctx.Jobs!.List(caller).Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                default:
                    throw new ArgumentException("Unknown method " + method);
            }
        }
    }

    private sealed class RecordingVapix : IVapixClientFactory
    {
        public ConcurrentQueue<Guid> Created { get; } = new();

        public Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct)
        {
            Created.Enqueue(deviceId);
            return Task.FromResult<IVapixClient>(new Tasks.FakeVapixClient());
        }
    }

    private sealed class RecordingTasks : ITaskRunner
    {
        public ConcurrentQueue<IReadOnlyList<Guid>> Runs { get; } = new();

        public ConcurrentQueue<Guid> Ids { get; } = new();

        public Task<IReadOnlyList<Guid>> RunAsync(string pluginId, IReadOnlyList<Guid> deviceIds, string? payloadJson, string owner, CancellationToken ct)
        {
            Runs.Enqueue(deviceIds);
            IReadOnlyList<Guid> ids = [.. deviceIds.Select(_ => Guid.NewGuid())];
            foreach (var id in ids)
            {
                Ids.Enqueue(id);
            }

            return Task.FromResult(ids);
        }

        public bool Cancel(Guid taskId) => true;
    }

    private sealed class RecordingStreams : IDeviceEventStreams
    {
        public ConcurrentQueue<Guid> Opened { get; } = new();

        public Task<IDeviceEventSource> OpenAsync(Guid deviceId, CancellationToken ct)
        {
            Opened.Enqueue(deviceId);
            return Task.FromResult<IDeviceEventSource>(new NoEvents());
        }
    }

    private sealed class NoEvents : IDeviceEventSource
    {
        public int LostDocuments => 0;

        public async IAsyncEnumerable<DeviceMetadataDocument> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
