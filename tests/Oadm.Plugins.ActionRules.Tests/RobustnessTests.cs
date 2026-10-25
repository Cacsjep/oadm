using System.Security.Cryptography;
using System.Text;

using Oadm.Plugins.ActionRules.Device;
using Oadm.Plugins.ActionRules.Rules;
using StepRun = Oadm.Sdk.Testing.StepRun;

namespace Oadm.Plugins.ActionRules.Tests;

/// <summary>
/// What must hold when things go wrong: undo after a later change, a removal that fails after a recreate, a timeout in the
/// middle of a run, rules sharing an action, recipients matching the same rule, undo while a run still runs, the undo store.
/// </summary>
public sealed class RobustnessTests : IAsyncLifetime
{
    private const string Tcp = "com.axis.action.fixed.notification.tcp";

    private readonly string _data = Path.Combine(Path.GetTempPath(), "oadm-action-rules-robust-" + Guid.NewGuid().ToString("N"));
    private readonly FakeDevice _device = new();
    private readonly FakeDevices _devices = new();
    private readonly RecordingRunner _runner = new();
    private readonly ActionRulesPlugin _plugin = new();
    private FakeContext? _ctx;

    public async Task InitializeAsync()
    {
        _devices.Items.Add(_device);
        _ctx = new FakeContext(_devices, new FakeCameras(), _runner, new RecordingEvents(), _data);
        await _plugin.StartAsync(_ctx, CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _plugin.DisposeAsync();
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    private static string AddTcp(RuleCamera camera, string name) =>
        camera.Add(name, Tcp, [("host", "192.0.2.10"), ("port", "9999"), ("qos", "0"), ("message", name)], start: PageTests.VirtualInput);

    [Theory]
    [InlineData("os12")]
    [InlineData("os10")]
    public async Task Undo_after_a_later_change_leaves_the_later_change(string os)
    {
        var camera = new RuleCamera(os);
        AddTcp(camera, "Door");

        var first = await RunAsync(camera, RuleTaskActions.Edit, new RulePatch { Name = "First" });
        await RunAsync(camera, RuleTaskActions.Edit, new RulePatch { Name = "Second" });
        var undo = await UndoAsync(camera, first.RunId);

        Assert.Equal("Second", Assert.Single(camera.Rules).Value.Name); // no duplicate, the later change stays
        Assert.Contains(undo.Warnings, w => w.Contains("Changed after this change", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_removal_that_fails_after_a_recreate_takes_the_new_rule_out()
    {
        var camera = new RuleCamera("os10") { RefuseRequest = "SOAP RemoveActionRule", RefuseTimes = 1 };
        AddTcp(camera, "Door");

        await Assert.ThrowsAsync<ActionEngineException>(() => RunAsync(camera, RuleTaskActions.Edit, new RulePatch { Name = "Renamed" }));

        var rule = Assert.Single(camera.Rules); // never both: the new one was taken out again
        Assert.Equal("Door", rule.Value.Name);
        Assert.Single(camera.Configs);
    }

    [Fact]
    public async Task A_timeout_in_the_middle_is_saved_and_undone_without_duplicates()
    {
        var camera = new RuleCamera("os10") { TimeoutRequest = "SOAP AddActionConfiguration", TimeoutSkip = 1 };
        AddTcp(camera, "One");
        AddTcp(camera, "Two");

        var run = await RunAsync(camera, RuleTaskActions.Edit, new RulePatch { Parameters = new Dictionary<string, string> { ["host"] = "192.0.2.99" } }, expectFailure: true);
        Assert.Contains("The device did not answer in time.", StepRun.Lines(run.Context.Steps).Concat(run.Context.Logs.Select(l => l.Message)).Last(l => l.Contains("in time", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.Equal(1, camera.Rules.Values.Count(r => camera.Configs[r.ConfigId].Parameters.Any(p => p is { Name: "host", Value: "192.0.2.99" })));

        camera.TimeoutRequest = null;
        await UndoAsync(camera, run.RunId);

        Assert.Equal(2, camera.Rules.Count); // the first change was saved before the second failed: put back, nothing doubled
        Assert.All(camera.Rules.Values, r => Assert.Equal("192.0.2.10", camera.Param(camera.Rules.Single(x => x.Value == r).Key, "host")));
    }

    [Fact]
    public async Task A_lost_answer_to_the_removal_finishes_the_change_when_the_old_rule_is_gone()
    {
        var camera = new RuleCamera("os10") { TimeoutRequest = "SOAP RemoveActionRule", TimeoutAfterDoing = true };
        AddTcp(camera, "Door");

        var run = await RunAsync(camera, RuleTaskActions.Edit, new RulePatch { Name = "Renamed" });

        Assert.Equal("Renamed", Assert.Single(camera.Rules).Value.Name); // read again: the old one is gone, the new one stays
        await UndoAsync(camera, run.RunId);
        Assert.Equal("Door", Assert.Single(camera.Rules).Value.Name);
    }

    [Fact]
    public async Task A_removal_without_answer_that_did_nothing_takes_the_new_rule_out()
    {
        var camera = new RuleCamera("os10") { TimeoutRequest = "SOAP RemoveActionRule", TimeoutTimes = 1 };
        AddTcp(camera, "Door");

        var run = await RunAsync(camera, RuleTaskActions.Edit, new RulePatch { Name = "Renamed" }, expectFailure: true);

        Assert.Equal("Door", Assert.Single(camera.Rules).Value.Name);
        Assert.Contains(run.Context.Logs, l => l.Message.Contains(RuleWriter.NoAnswerNothing, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_device_that_stops_answering_keeps_both_and_undo_tidies_up()
    {
        var camera = new RuleCamera("os10") { TimeoutRequest = "SOAP RemoveActionRule", TimeoutTimes = 1 };
        AddTcp(camera, "Door");

        // The removal times out and the device cannot be read: the state is unknown, so nothing more is removed; both stay,
        // reported, and the new rule is saved for undo.
        camera.RefuseRequest = "SOAP GetActionConfigurations"; // the reading again after the timeout fails (the page and the task read first)
        camera.RefuseSkip = 2;
        camera.RefuseTimes = 1;
        var run = await RunAsync(camera, RuleTaskActions.Edit, new RulePatch { Name = "Renamed" }, expectFailure: true);
        Assert.Equal(2, camera.Rules.Count);
        Assert.Contains(run.Context.Logs, l => l.Message.Contains(RuleWriter.NoAnswerBoth, StringComparison.Ordinal));

        await UndoAsync(camera, run.RunId);
        Assert.Equal("Door", Assert.Single(camera.Rules).Value.Name);
    }

    [Fact]
    public async Task Undo_puts_back_the_deleted_one_of_two_identical_rules()
    {
        var camera = new RuleCamera("os12");
        AddTcp(camera, "Door");
        AddTcp(camera, "Door");
        var rules = await SoapActionEngine.ReadRulesAsync(camera, CancellationToken.None);
        var payload = new RuleTaskPayload(RuleTaskActions.Delete, Guid.NewGuid(), "Delete",
            [new DeviceTargets(_device.Id, [new RuleRef(rules[0].Id, RuleView.VersionOf(rules[0]))])], null);
        await StartAsync(camera, payload);
        Assert.Single(camera.Rules);

        await UndoAsync(camera, payload.RunId); // the twin is not the deleted rule

        Assert.Equal(2, camera.Rules.Count);
    }

    [Fact]
    public async Task Rules_sharing_one_action_leave_no_action_behind()
    {
        var camera = new RuleCamera("os10");
        var one = AddTcp(camera, "One");
        var shared = camera.Rules[one].ConfigId;
        camera.Rules["50"] = camera.Rules[one] with { Name = "Two" }; // both rules use one action (a configuration the web interface never shares, ADM files may)

        await RunAsync(camera, RuleTaskActions.Edit, new RulePatch { Enabled = false });

        Assert.Equal(2, camera.Rules.Count);
        Assert.All(camera.Rules.Values, r => Assert.False(r.Enabled));
        Assert.False(camera.Configs.ContainsKey(shared)); // removed with the last rule that used it
        Assert.Equal(2, camera.Configs.Count);
    }

    [Fact]
    public async Task Two_recipients_with_the_same_server_change_a_rule_once()
    {
        var camera = new RuleCamera("os10");
        DeviceRecipient Recipient(string id, string name) => new(id, name, "com.axis.recipient.tcp", [new("host", "192.0.2.10"), new("port", "9999"), new("qos", "0")]);
        camera.Recipients["60"] = Recipient("60", "VMS");
        camera.Recipients["61"] = Recipient("61", "VMS copy");
        camera.Add("Notify", "com.axis.action.unlimited.notification.tcp", [("host", "192.0.2.10"), ("port", "9999"), ("qos", "0"), ("message", "m"), ("period", "5")], conditions: [PageTests.VirtualInput]);
        var refs = camera.Recipients.Values.Select(r => new RuleRef(r.Id, RecipientView.VersionOf(r))).ToList();
        var payload = new RuleTaskPayload(RuleTaskActions.RecipientEdit, Guid.NewGuid(), "Change", [new DeviceTargets(_device.Id, refs)], null)
        {
            RecipientPatch = new RecipientPatch(null, new Dictionary<string, string> { ["host"] = "192.0.2.20" }),
        };

        await StartAsync(camera, payload);

        var rule = Assert.Single(camera.Rules); // changed once, not recreated twice
        Assert.Equal("192.0.2.20", camera.Param(rule.Key, "host"));
        Assert.All(camera.Recipients.Values, r => Assert.Equal("192.0.2.20", r.Parameters.Single(p => p.Name == "host").Value));
    }

    [Fact]
    public async Task Undo_waits_for_the_run_and_happens_once()
    {
        var camera = new RuleCamera();
        AddTcp(camera, "Door");
        var rules = await SoapActionEngine.ReadRulesAsync(camera, CancellationToken.None);
        var request = new RunRequest([new DeviceTargets(_device.Id, [new RuleRef(rules[0].Id, RuleView.VersionOf(rules[0]))])], new RulePatch { Name = "Renamed" });
        var started = ActionRulesJson.Deserialize<RunStarted>(await _plugin.InvokeAsync(ActionRulesMethods.Edit, ActionRulesJson.Serialize(request), CancellationToken.None));
        await _plugin.Undo.SaveDeviceAsync(started.RunId, new DeviceUndo(_device.Id, [new UndoRule(rules[0], rules[0])]), CancellationToken.None);
        var undo = ActionRulesJson.Serialize(new UndoRequest(started.RunId));

        var running = await Assert.ThrowsAsync<ArgumentException>(() => _plugin.InvokeAsync(ActionRulesMethods.Undo, undo, CancellationToken.None));
        Assert.StartsWith("This change is still running.", running.Message, StringComparison.Ordinal);

        _plugin.TaskEnded(started.RunId); // the run's one task ended
        var both = await Task.WhenAll(
            Try(() => _plugin.InvokeAsync(ActionRulesMethods.Undo, undo, CancellationToken.None)),
            Try(() => _plugin.InvokeAsync(ActionRulesMethods.Undo, undo, CancellationToken.None)));
        Assert.Equal(1, both.Count(ok => ok)); // two clicks at once: one undo
        Assert.Equal(2, _runner.Runs.Count);
    }

    [Fact]
    public async Task Undo_is_free_once_the_runs_tasks_were_cancelled_while_queued()
    {
        var camera = new RuleCamera();
        AddTcp(camera, "Door");
        var rules = await SoapActionEngine.ReadRulesAsync(camera, CancellationToken.None);
        var request = new RunRequest([new DeviceTargets(_device.Id, [new RuleRef(rules[0].Id, RuleView.VersionOf(rules[0]))])], new RulePatch { Name = "Renamed" });
        var started = ActionRulesJson.Deserialize<RunStarted>(await _plugin.InvokeAsync(ActionRulesMethods.Edit, ActionRulesJson.Serialize(request), CancellationToken.None));
        await _plugin.Undo.SaveDeviceAsync(started.RunId, new DeviceUndo(_device.Id, [new UndoRule(rules[0], rules[0])]), CancellationToken.None);
        Assert.True(await _plugin.IsRunningAsync(new Oadm.Plugins.Shared.ContextCall(_ctx!), started.RunId, CancellationToken.None));

        Assert.All(started.TaskIds, id => Assert.True(_runner.Cancel(id))); // cancelled before start: no task reports

        Assert.False(await _plugin.IsRunningAsync(new Oadm.Plugins.Shared.ContextCall(_ctx!), started.RunId, CancellationToken.None));
        await _plugin.InvokeAsync(ActionRulesMethods.Undo, ActionRulesJson.Serialize(new UndoRequest(started.RunId)), CancellationToken.None);
        Assert.Equal(2, _runner.Runs.Count);
    }

    [Fact]
    public async Task A_group_users_check_never_ends_a_run_whose_other_tasks_still_run()
    {
        var camera = new RuleCamera();
        AddTcp(camera, "Door");
        var rules = await SoapActionEngine.ReadRulesAsync(camera, CancellationToken.None);
        var request = new RunRequest([new DeviceTargets(_device.Id, [new RuleRef(rules[0].Id, RuleView.VersionOf(rules[0]))])], new RulePatch { Name = "Renamed" });
        var started = ActionRulesJson.Deserialize<RunStarted>(await _plugin.InvokeAsync(ActionRulesMethods.Edit, ActionRulesJson.Serialize(request), CancellationToken.None));
        var all = new Oadm.Plugins.Shared.ContextCall(_ctx!);

        // The group's devices have no active task of the run (its view of the task engine): not running for that user...
        var group = new GroupCall(all, new IdleTasks());
        Assert.False(await _plugin.IsRunningAsync(group, started.RunId, CancellationToken.None));

        // ...but the run's tasks on other devices still write: a user with all devices still sees it running.
        Assert.True(await _plugin.IsRunningAsync(all, started.RunId, CancellationToken.None));
    }

    /// <summary>A user limited to a device group over the test's context, with its own view of the task engine.</summary>
    private sealed class GroupCall(Oadm.Sdk.Plugins.ICallContext inner, Oadm.Sdk.Tasks.ITaskRunner tasks) : Oadm.Sdk.Plugins.ICallContext
    {
        public string UserName => "anna";

        public string SessionId => "anna-session";

        public string DeviceScopeKey => "group";

        public bool HasAllDevices => false;

        public Oadm.Sdk.Devices.IDeviceRepository Devices => inner.Devices;

        public Oadm.Sdk.Vapix.IVapixClientFactory Vapix => inner.Vapix;

        public Oadm.Sdk.Tasks.ITaskRunner Tasks => tasks;

        public Oadm.Sdk.Devices.IDeviceEventStreams? EventStreams => inner.EventStreams;

        public bool Has(string permission) => true;

        public bool Contains(Oadm.Sdk.Devices.IDeviceInfo device) => true;
    }

    /// <summary>A task engine view in which no task of the run is active (the run's tasks are on other devices).</summary>
    private sealed class IdleTasks : Oadm.Sdk.Tasks.ITaskRunner
    {
        public Task<IReadOnlyList<Guid>> RunAsync(string pluginId, IReadOnlyList<Guid> deviceIds, string? payloadJson, string owner, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<bool?> AnyActiveAsync(IReadOnlyCollection<Guid> taskIds, CancellationToken ct) => Task.FromResult<bool?>(false);
    }

    [Fact]
    public async Task The_undo_store_keeps_new_runs_and_refuses_unknown_ones()
    {
        var store = _plugin.Undo;
        var fresh = Path.Combine(_data, "undo", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fresh); // a run whose run.json is being written

        Assert.Empty(await store.ListAsync(CancellationToken.None));
        Assert.True(Directory.Exists(fresh));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.SaveDeviceAsync(Guid.NewGuid(), new DeviceUndo(_device.Id, []), CancellationToken.None));
    }

    [Fact]
    public void Versions_are_keyed_so_passwords_cannot_be_tested_against_them()
    {
        var rule = new DeviceRule("1", "Upload", true, PageTests.VirtualInput, [], null, new RuleAction("com.axis.action.fixed.send_images.ftp", [new("host", "h"), new("password", "secret")]));
        var other = rule with { Action = rule.Action with { Parameters = [new("host", "h"), new("password", "guess")] } };

        Assert.NotEqual(RuleView.VersionOf(rule), RuleView.VersionOf(other)); // a changed password still counts as a change
        var unkeyed = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RuleContent.Of(rule))), 0, 12);
        Assert.NotEqual(unkeyed, RuleView.VersionOf(rule));
        Assert.Equal(string.Empty, RuleView.From(rule).Get("password"));
    }

    private static async Task<bool> Try(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed record Ran(Guid RunId, RecordingContext Context);

    private async Task<Ran> RunAsync(RuleCamera camera, string action, RulePatch? patch, bool expectFailure = false)
    {
        var rules = await SoapActionEngine.ReadRulesAsync(camera, CancellationToken.None);
        var payload = new RuleTaskPayload(action, Guid.NewGuid(), "test",
            [new DeviceTargets(_device.Id, [.. rules.Select(r => new RuleRef(r.Id, RuleView.VersionOf(r)))])], patch);
        if (expectFailure)
        {
            var ctx = await StartAsync(camera, payload, expectFailure: true);
            return new Ran(payload.RunId, ctx);
        }

        return new Ran(payload.RunId, await StartAsync(camera, payload));
    }

    private async Task<RecordingContext> StartAsync(RuleCamera camera, RuleTaskPayload payload, bool expectFailure = false)
    {
        await _plugin.Undo.StartRunAsync(new UndoRun(payload.RunId, DateTimeOffset.UtcNow, "test", payload.Action, 1, false), CancellationToken.None);
        var ctx = new RecordingContext(camera);
        var task = (RuleTaskPlugin)_plugin.TaskPlugins[0];
        try
        {
            await StepRun.RunAsync(ctx.Steps, () => task.ExecuteAsync(ctx, _device, ActionRulesJson.Serialize(payload), CancellationToken.None));
        }
        catch (ActionEngineException) when (expectFailure)
        {
        }

        return ctx;
    }

    private async Task<RecordingContext> UndoAsync(RuleCamera camera, Guid runId)
    {
        await _plugin.Undo.TryMarkUndoneAsync(runId, true, CancellationToken.None); // like the page: marked before its tasks start
        var ctx = new RecordingContext(camera);
        var task = (RuleTaskPlugin)_plugin.TaskPlugins[0];
        try
        {
            await StepRun.RunAsync(ctx.Steps, () => task.ExecuteAsync(ctx, _device, ActionRulesJson.Serialize(new RuleTaskPayload(RuleTaskActions.Undo, Guid.NewGuid(), "Undo", [], null, runId)), CancellationToken.None));
        }
        catch (ActionEngineException ex)
        {
            throw new InvalidOperationException(ex.Message + " | " + string.Join(" | ", ctx.Steps.Snapshot().Select(s => $"{s.Name}: {s.State} {s.Detail}")), ex);
        }

        return ctx;
    }
}
