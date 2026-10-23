using Oadm.Plugins.Shared;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Vapix;

using PluginCanary = Oadm.Sdk.Testing.PluginCanary;
using Testing = Oadm.Sdk.Testing;

namespace Oadm.Plugins.ActionRules.Tests;

/// <summary>
/// Device groups (docs/spec/device-scope.md, Plugins): one table per group, every device id of a request (chosen devices,
/// targets, the source of a copy) in the caller's view or the whole request is refused like an unknown id, runs through the
/// caller's task runner, undo runs kept per group.
/// </summary>
public sealed class DeviceGroupTests
{
    private const string Targets = """{"targets":[{"deviceId":"{inside}","rules":[{"id":"1","version":"a"}]},{"deviceId":"{device}","rules":[{"id":"1","version":"a"}]}]""";

    [Fact]
    public async Task NoMethodReachesADeviceOutsideTheUsersGroup()
    {
        var ctx = new Testing.FakeCoreContext();
        var inside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.1" });
        var outside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.2" });
        await using var plugin = new ActionRulesPlugin(Options());
        await plugin.StartAsync(ctx, CancellationToken.None);

        await new PluginCanary(ctx, plugin, inside, outside)
            .Method(ActionRulesMethods.Read, """{"deviceIds":["{inside}","{device}"]}""")
            .Method(ActionRulesMethods.Events, """{"deviceIds":["{inside}","{device}"]}""")
            .Method(ActionRulesMethods.Edit, Targets + ""","patch":{"enabled":false}}""")
            .Method(ActionRulesMethods.Delete, Targets + "}")
            .Method(ActionRulesMethods.Deploy, """{"sourceDeviceId":"{device}","sourceId":"1","version":"a","deviceIds":["{inside}"]}""")
            .Method(ActionRulesMethods.Deploy, """{"sourceDeviceId":"{inside}","sourceId":"1","version":"a","deviceIds":["{device}"]}""")
            .Method(ActionRulesMethods.RecipientDeploy, """{"sourceDeviceId":"{device}","sourceId":"1","version":"a","deviceIds":["{inside}"]}""")
            .Method(ActionRulesMethods.RecipientDeploy, """{"sourceDeviceId":"{inside}","sourceId":"1","version":"a","deviceIds":["{device}"]}""")
            .Method(ActionRulesMethods.RecipientNew, """{"deviceIds":["{device}"],"template":"com.axis.recipient.tcp","name":"Alarm server","parameters":{}}""")
            .Method(ActionRulesMethods.RecipientEdit, Targets + ""","patch":{"name":"Alarm server"}}""")
            .Method(ActionRulesMethods.RecipientDelete, Targets + "}")
            .Method(ActionRulesMethods.ScheduleEdit, Targets + ""","patch":{"name":"Night"}}""")
            .Method(ActionRulesMethods.ScheduleDelete, Targets + "}")
            .Method(ActionRulesMethods.ScheduleDelete, """{"targets":[{"deviceId":"{inside}","rules":[{"id":"1","version":"a"}]}],"rules":[{"deviceId":"{device}","rules":[{"id":"2","version":"a"}]}]}""")
            .Method(ActionRulesMethods.ScheduleDeploy, """{"deviceIds":["{device}"],"new":{"name":"Night","definition":"DTSTART:19700101T220000\nDTEND:19700102T060000\nRRULE:FREQ=DAILY"}}""")
            .Method(ActionRulesMethods.ScheduleDeploy, """{"deviceIds":["{inside}"],"sourceDeviceId":"{device}","sourceId":"1","sourceVersion":"a"}""")
            .Method(ActionRulesMethods.Build, """{"deviceIds":["{inside}","{device}"],"rule":{"name":"Door","enabled":true,"event":{"topic":"tns1:Device/tnsaxis:IO/VirtualInput","values":[]},"whileActive":false,"conditions":[],"timeoutSeconds":0,"template":"com.axis.action.fixed.notification.tcp","parameters":{}}}""")
            .WithoutDevices(ActionRulesMethods.GetState, "the table of the caller's own group")
            .WithoutDevices(ActionRulesMethods.GetProgress, "the progress of the caller's own group")
            .WithoutDevices(ActionRulesMethods.Templates, "action and recipient templates by content id, no device data", """{"ids":["x"]}""")
            .WithoutDevices(ActionRulesMethods.UndoRuns, "only the runs of the caller's own group")
            .WithoutDevices(ActionRulesMethods.Undo, "a run of another group answers like an unknown run (UndoRunsAreKeptPerGroup)", """{"runId":"00000000-0000-0000-0000-000000000001"}""")
            .RunAsync();
    }

    [Fact]
    public async Task EachGroupReadsOnlyItsDevicesAndItsRowsReachOnlyItsUsers()
    {
        var ctx = new Testing.FakeCoreContext();
        var inside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.1" });
        var outside = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.2" });
        var key = ctx.FakeScopes.Add("Building B", [inside.Id]);
        await using var plugin = new ActionRulesPlugin(Options());
        await plugin.StartAsync(ctx, CancellationToken.None);
        var group = Testing.FakeCallContext.WithGroup(ctx, [inside.Id], "anna", key);
        var all = ctx.CallAsAllDevices();

        await ctx.InvokeAsync(plugin, group, ActionRulesMethods.Read, null);
        await plugin.Sweeps!.For(key)!.Current;
        Assert.Empty(ctx.RefusedAccesses);
        Assert.Equal(0, ctx.ClientsCreated.GetValueOrDefault(outside.Id));
        var state = ActionRulesJson.Deserialize<RulesState>(await ctx.InvokeAsync(plugin, group, ActionRulesMethods.GetState, null));
        Assert.Equal([inside.Id], state.Rows.Select(r => r.DeviceId));
        Assert.Empty(ActionRulesJson.Deserialize<RulesState>(await ctx.InvokeAsync(plugin, all, ActionRulesMethods.GetState, null)).Rows);

        plugin.Sweeps.For(key)!.Publish();
        Assert.Contains(ctx.EventsSeenBy(group), e => e.Topic == ActionRulesMethods.RowsTopic);
        Assert.DoesNotContain(ctx.EventsSeenBy(all), e => e.Topic == ActionRulesMethods.RowsTopic);

        // A run on the group's device goes through the caller's runner.
        await ctx.InvokeAsync(plugin, group, ActionRulesMethods.Delete, $$"""{"targets":[{"deviceId":"{{inside.Id}}","rules":[{"id":"1","version":"a"}]}]}""");
        Assert.Equal([inside.Id], ctx.TaskRuns.Single().DeviceIds);
    }

    [Fact]
    public async Task UndoRunsAreKeptPerGroup()
    {
        var data = Path.Combine(Path.GetTempPath(), "oadm-action-rules-groups-" + Guid.NewGuid().ToString("N"));
        try
        {
            var device = new FakeDevice { Address = "10.0.0.1" };
            var other = new FakeDevice { Address = "10.0.0.2" };
            var devices = new FakeDevices();
            devices.Items.AddRange([device, other]);
            var runner = new RecordingRunner();
            await using var plugin = new ActionRulesPlugin(Options());
            await plugin.StartAsync(new FakeContext(devices, new FakeCameras(), runner, new RecordingEvents(), data), CancellationToken.None);
            var groupA = new GroupCall("a", devices, [device.Id], runner);
            var groupB = new GroupCall("b", devices, [device.Id, other.Id], runner);
            var all = new ContextCall(new FakeContext(devices, new FakeCameras(), runner, new RecordingEvents(), data));

            var started = ActionRulesJson.Deserialize<RunStarted>(await plugin.InvokeAsync(groupA, ActionRulesMethods.Delete,
                ActionRulesJson.Serialize(new RunRequest([new DeviceTargets(device.Id, [new RuleRef("1", "a")])], null)), CancellationToken.None));
            await plugin.Undo.SaveDeviceAsync(started.RunId, new DeviceUndo(device.Id, [new UndoRule(null, null)]), CancellationToken.None);
            plugin.TaskEnded(started.RunId);

            async Task<List<UndoRun>> RunsAsync(ICallContext call) => ActionRulesJson.Deserialize<List<UndoRun>>(await plugin.InvokeAsync(call, ActionRulesMethods.UndoRuns, null, CancellationToken.None));
            Assert.Equal("a", Assert.Single(await RunsAsync(groupA)).Scope);
            Assert.Empty(await RunsAsync(groupB)); // another group: hidden
            Assert.Single(await RunsAsync(all)); // all devices: every run

            var undo = ActionRulesJson.Serialize(new UndoRequest(started.RunId));
            var foreign = await Assert.ThrowsAsync<ArgumentException>(() => plugin.InvokeAsync(groupB, ActionRulesMethods.Undo, undo, CancellationToken.None));
            var unknown = await Assert.ThrowsAsync<ArgumentException>(() => plugin.InvokeAsync(groupB, ActionRulesMethods.Undo, ActionRulesJson.Serialize(new UndoRequest(Guid.NewGuid())), CancellationToken.None));
            Assert.Equal(unknown.Message, foreign.Message);
            Assert.Single(runner.Runs); // only the delete

            await plugin.InvokeAsync(groupA, ActionRulesMethods.Undo, undo, CancellationToken.None);
            Assert.Equal(2, runner.Runs.Count);
        }
        finally
        {
            if (Directory.Exists(data))
            {
                Directory.Delete(data, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_group_user_starts_and_undoes_changes_while_another_run_is_open()
    {
        var data = Path.Combine(Path.GetTempPath(), "oadm-action-rules-open-" + Guid.NewGuid().ToString("N"));
        try
        {
            var device = new FakeDevice { Address = "10.0.0.1" };
            var devices = new FakeDevices();
            devices.Items.Add(device);
            var runner = new RecordingRunner();
            var ctx = new GuardedContext(new FakeContext(devices, new FakeCameras(), runner, new RecordingEvents(), data));
            await using var plugin = new ActionRulesPlugin(Options());
            await plugin.StartAsync(ctx, CancellationToken.None);
            var group = new GroupCall("a", devices, [device.Id], runner);
            var delete = ActionRulesJson.Serialize(new RunRequest([new DeviceTargets(device.Id, [new RuleRef("1", "a")])], null));

            var first = ActionRulesJson.Deserialize<RunStarted>(await ctx.CallAsync(plugin, group, ActionRulesMethods.Delete, delete));
            await plugin.Undo.SaveDeviceAsync(first.RunId, new DeviceUndo(device.Id, [new UndoRule(null, null)]), CancellationToken.None);

            // A second run while the first one's task is still queued: never asks the guarded task runner.
            var second = ActionRulesJson.Deserialize<RunStarted>(await ctx.CallAsync(plugin, group, ActionRulesMethods.Delete, delete));
            await plugin.Undo.SaveDeviceAsync(second.RunId, new DeviceUndo(device.Id, [new UndoRule(null, null)]), CancellationToken.None);

            // Still running: refused with the plain text, not PERMISSION_DENIED.
            var running = await Assert.ThrowsAsync<ArgumentException>(() => ctx.CallAsync(plugin, group, ActionRulesMethods.Undo, ActionRulesJson.Serialize(new UndoRequest(second.RunId))));
            Assert.StartsWith("This change is still running.", running.Message, StringComparison.Ordinal);

            // The first run's task was cancelled while queued (it never reports): undo sees that it ended.
            runner.Cancel(Assert.Single(first.TaskIds));
            await ctx.CallAsync(plugin, group, ActionRulesMethods.Undo, ActionRulesJson.Serialize(new UndoRequest(first.RunId)));
            Assert.Equal(3, runner.Runs.Count);
            Assert.Empty(ctx.Refused);
            Assert.Contains("System.Tasks", ctx.Used);
        }
        finally
        {
            if (Directory.Exists(data))
            {
                Directory.Delete(data, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_task_started_by_hand_is_bound_to_a_run_of_its_users_group()
    {
        var data = Path.Combine(Path.GetTempPath(), "oadm-action-rules-bound-" + Guid.NewGuid().ToString("N"));
        try
        {
            var device = new FakeDevice { Address = "10.0.0.1" };
            var devices = new FakeDevices();
            devices.Items.Add(device);
            var runner = new RecordingRunner();
            await using var plugin = new ActionRulesPlugin(Options());
            await plugin.StartAsync(new FakeContext(devices, new FakeCameras(), runner, new RecordingEvents(), data), CancellationToken.None);
            var groupA = new GroupCall("a", devices, [device.Id], runner);
            var groupB = new GroupCall("b", devices, [device.Id], runner);

            // Group A's run, undone through the page.
            var started = ActionRulesJson.Deserialize<RunStarted>(await plugin.InvokeAsync(groupA, ActionRulesMethods.Delete,
                ActionRulesJson.Serialize(new RunRequest([new DeviceTargets(device.Id, [new RuleRef("1", "a")])], null)), CancellationToken.None));
            await plugin.Undo.SaveDeviceAsync(started.RunId, new DeviceUndo(device.Id, [new UndoRule(null, null)]), CancellationToken.None);
            var notMarked = new RuleTaskPayload(RuleTaskActions.Undo, Guid.NewGuid(), "Undo", [], null, started.RunId);

            // Not marked by Undo: refused before the device is read, also for its own group.
            Assert.Equal(RuleTexts.CannotUndo, await RefusalAsync(plugin, groupA, device, notMarked));
            await plugin.Undo.TryMarkUndoneAsync(started.RunId, true, CancellationToken.None);

            // Another group's user names group A's run: refused, also for a change saved into it.
            Assert.Equal(RuleTexts.CannotUndo, await RefusalAsync(plugin, groupB, device, notMarked));
            var intoForeignRun = new RuleTaskPayload(RuleTaskActions.Delete, started.RunId, "Delete", [new DeviceTargets(device.Id, [new RuleRef("1", "a")])], null);
            Assert.Equal(RuleTexts.CannotSaveForUndo, await RefusalAsync(plugin, groupB, device, intoForeignRun));
            var unknownRun = intoForeignRun with { RunId = Guid.NewGuid() };
            Assert.Equal(RuleTexts.CannotSaveForUndo, await RefusalAsync(plugin, groupA, device, unknownRun));

            // Group A's own marked run and a task the system started (no user) go on.
            Assert.Null(await RefusalAsync(plugin, groupA, device, notMarked));
            Assert.Null(await RefusalAsync(plugin, null, device, notMarked));
        }
        finally
        {
            if (Directory.Exists(data))
            {
                Directory.Delete(data, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Rows_of_a_device_that_left_the_group_disappear_before_any_prune()
    {
        var ctx = new Testing.FakeCoreContext();
        var stays = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.1" });
        var leaves = ctx.Add(new Testing.FakeDevice { Address = "10.0.0.2" });
        var key = ctx.FakeScopes.Add("Building B", [stays.Id, leaves.Id]);
        await using var plugin = new ActionRulesPlugin(Options());
        await plugin.StartAsync(ctx, CancellationToken.None);
        await ctx.InvokeAsync(plugin, Testing.FakeCallContext.WithGroup(ctx, [stays.Id, leaves.Id], "anna", key), ActionRulesMethods.Read, null);
        var sweep = plugin.Sweeps!.For(key)!;
        await sweep.Current;
        Assert.Equal(2, sweep.Snapshot().Rows.Count);

        // A tag change moves the device out of the group; the group has not told the plugin yet (no Changed).
        ctx.FakeScopes.Add("Building B", [stays.Id], key);
        var now = Testing.FakeCallContext.WithGroup(ctx, [stays.Id], "anna", key);
        var state = ActionRulesJson.Deserialize<RulesState>(await ctx.InvokeAsync(plugin, now, ActionRulesMethods.GetState, null));
        Assert.Equal([stays.Id], state.Rows.Select(r => r.DeviceId));

        sweep.Publish();
        var removed = ctx.EventsSeenBy(now).Where(e => e.Topic == ActionRulesMethods.RowsTopic).Select(e => ActionRulesJson.Deserialize<RowsEvent>(e.PayloadJson)).ToList();
        Assert.Contains(removed, e => e.Removed.Contains(leaves.Id));

        // Reading it again after a task: the row stays gone, the device is not read.
        var reads = ctx.ClientsCreated.GetValueOrDefault(leaves.Id);
        await plugin.RefreshRowAsync(leaves.Id);
        await sweep.ReadOneAsync(leaves.Id, CancellationToken.None);
        Assert.Equal(reads, ctx.ClientsCreated.GetValueOrDefault(leaves.Id));
        Assert.DoesNotContain(sweep.Snapshot().Rows, r => r.DeviceId == leaves.Id);
    }

    private static async Task<string?> RefusalAsync(ActionRulesPlugin plugin, ICallContext? caller, IDeviceInfo device, RuleTaskPayload payload)
    {
        var task = (RuleTaskPlugin)plugin.TaskPlugins[0];
        var camera = new RuleCamera();
        var ctx = new RecordingContext(camera) { Caller = caller };
        try
        {
            await task.ExecuteAsync(ctx, device, ActionRulesJson.Serialize(payload), CancellationToken.None);
        }
        catch (ArgumentException ex) when (ex.Message is RuleTexts.CannotUndo or RuleTexts.CannotSaveForUndo)
        {
            Assert.Empty(camera.Requests); // refused before the device was read
            return ex.Message;
        }
#pragma warning disable CA1031 // Anything else got past the binding (the fake camera has no such rule).
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        return null;
    }

    private static DeviceSweepOptions Options() => new() { PublishInterval = TimeSpan.FromHours(1), DeviceTimeout = TimeSpan.FromSeconds(30) };

    /// <summary>
    /// The host's guard over the test's context: in a group user's call <c>ctx.Tasks</c> refuses (every member, also
    /// <c>IsActive</c>), <c>ctx.System</c> works only in methods with a <see cref="ICorePlugin.SystemAccessReason"/>.
    /// </summary>
    private sealed class GuardedContext(FakeContext inner) : ICorePluginContext, ISystemAccess
    {
        private readonly AsyncLocal<(bool Group, bool System)> _call = new();

        public System.Collections.Concurrent.ConcurrentQueue<string> Refused { get; } = new();

        public System.Collections.Concurrent.ConcurrentQueue<string> Used { get; } = new();

        public IDeviceRepository Devices => inner.Devices;

        public IVapixClientFactory Vapix => inner.Vapix;

        public ITaskRunner Tasks => new Guarded(this, inner.Tasks, "Tasks", false);

        public ISystemAccess System => this;

        ITaskRunner ISystemAccess.Tasks => new Guarded(this, inner.Tasks, "System.Tasks", true);

        IDeviceEventStreams? ISystemAccess.EventStreams => null;

        public IPluginSettings Settings => inner.Settings;

        public Microsoft.Extensions.Logging.ILogger Logger => inner.Logger;

        public IPluginEvents? Events => inner.Events;

        public ISecretProtector? Secrets => inner.Secrets;

        public string? DataDirectory => inner.DataDirectory;

        public async Task<string?> CallAsync(ICorePlugin plugin, ICallContext call, string method, string? payload)
        {
            _call.Value = (!call.HasAllDevices, !string.IsNullOrWhiteSpace(plugin.SystemAccessReason(method)));
            try
            {
                return await plugin.InvokeAsync(call, method, payload, CancellationToken.None);
            }
            finally
            {
                _call.Value = default;
            }
        }

        private void Check(string member, bool system)
        {
            var (group, allowed) = _call.Value;
            if (group && !(system && allowed))
            {
                Refused.Enqueue(member);
                throw new DeviceAccessRefusedException();
            }

            Used.Enqueue(member);
        }

        private sealed class Guarded(GuardedContext ctx, ITaskRunner tasks, string member, bool system) : ITaskRunner
        {
            public Task<IReadOnlyList<Guid>> RunAsync(string pluginId, IReadOnlyList<Guid> deviceIds, string? payloadJson, string owner, CancellationToken ct)
            {
                ctx.Check(member, system);
                return tasks.RunAsync(pluginId, deviceIds, payloadJson, owner, ct);
            }

            public bool Cancel(Guid taskId)
            {
                ctx.Check(member, system);
                return tasks.Cancel(taskId);
            }

            public bool? IsActive(Guid taskId)
            {
                ctx.Check(member, system);
                return tasks.IsActive(taskId);
            }
        }
    }

    /// <summary>A group user over the test's own fakes.</summary>
    private sealed class GroupCall(string key, FakeDevices all, IReadOnlyCollection<Guid> ids, RecordingRunner runner) : ICallContext, IDeviceRepository, ITaskRunner
    {
        public string UserName => "user-" + key;

        public string SessionId => "session-" + key;

        public string DeviceScopeKey => key;

        public bool HasAllDevices => false;

        public IDeviceRepository Devices => this;

        public IVapixClientFactory Vapix => throw new NotSupportedException();

        public ITaskRunner Tasks => this;

        public IDeviceEventStreams? EventStreams => null;

        public bool Has(string permission) => true;

        public bool Contains(IDeviceInfo device) => ids.Contains(device.Id);

        public async Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) => [.. (await all.ListAsync(ct)).Where(Contains)];

        public async Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) => await all.FindAsync(id, ct) is { } d && Contains(d) ? d : null;

        public Task<IReadOnlyList<Guid>> RunAsync(string pluginId, IReadOnlyList<Guid> deviceIds, string? payloadJson, string owner, CancellationToken ct) =>
            deviceIds.All(ids.Contains) ? runner.RunAsync(pluginId, deviceIds, payloadJson, owner, ct) : throw new DevicesNotAvailableException();

        public bool Cancel(Guid taskId) => false;

        public bool? IsActive(Guid taskId) => false;
    }
}
