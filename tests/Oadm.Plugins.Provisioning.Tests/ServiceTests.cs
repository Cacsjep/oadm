using System.Text.Json;

using Oadm.Plugins.Provisioning.Engine;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Provisioning.Nodes;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Network;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Provisioning.Tests;

/// <summary>What starts a provisioning task and how the task runs (docs/spec/provisioning.md "Rules that never break" and "Tests").</summary>
public sealed class ServiceTests
{
    private static RunPayload PayloadOf(FakeTaskRun run) => JsonSerializer.Deserialize<RunPayload>(run.PayloadJson!, ProvisioningJson.Options)!;

    // ---- what starts a task ----

    [Fact]
    public async Task Off_nothing_starts()
    {
        await using var h = new Harness();
        await h.LoadAsync();
        h.Active = h.Profile();
        var (_, device) = h.NewCamera(1);

        h.AutoAdd.Report(device!);
        h.Ack(2);

        Assert.Equal(0, h.Engine.PendingLooks);
        await Task.Yield();
        Assert.Empty(h.ProvisionRuns);
        Assert.False(h.Engine.IsOn);
    }

    [Fact]
    public async Task On_a_camera_the_server_stores_as_password_not_set_gets_one_task_and_never_a_second()
    {
        await using var h = new Harness();
        await h.StartAsync(h.Profile());
        var (_, device) = h.NewCamera(1);

        h.AutoAdd.Report(device!); // the add page, an import, the waiting list, the automatic add
        await Eventually.TrueAsync(() => h.ProvisionRuns.Count == 1, "the task started");
        h.AutoAdd.Report(device!, added: false); // the polling: Password not set again
        h.AutoAdd.Report(device!);
        await Eventually.TrueAsync(() => h.Engine.ReportsDone, "the reports were handled");
        Assert.Equal(0, await h.Engine.StartForAsync([device!.Id], CancellationToken.None)); // busy: none

        var run = Assert.Single(h.ProvisionRuns);
        Assert.Equal([device.Id], run.DeviceIds);
        Assert.Equal("Provisioning", run.Owner);
        Assert.Equal("Lobby", PayloadOf(run).Profile.Name);
        Assert.True(PayloadOf(run).Look);
        Assert.Null(PayloadOf(run).Nodes);
        Assert.Equal(new TaskAudit("Provision with Lobby") { UserName = "Provisioning (admin)", EachTask = true }, run.Audit); // one entry per task

        // Once the task ended, the camera can get a task again (reset to factory default later).
        await h.ExecuteAsync(run, device.Id);
        Assert.Equal(1, await h.Engine.StartForAsync([device.Id], CancellationToken.None));
    }

    [Fact]
    public async Task A_failed_run_is_kept_for_run_again_unless_the_camera_was_reset_since()
    {
        await using var h = new Harness();
        await h.StartAsync(h.Profile());
        var (_, device) = h.NewCamera(1, configure: c => c.RefusesPassword = true);
        h.AutoAdd.Report(device!);
        await Eventually.TrueAsync(() => h.ProvisionRuns.Count == 1, "the task started");
        Assert.NotNull((await h.ExecuteLastAsync()).Failure);
        var failed = (await h.Engine.ReadRunAsync(device!.Id, CancellationToken.None))!;

        // Still Password not set after a time offline, and a new lease: no second task, the failed run stays for Run again.
        h.AutoAdd.Report(device, added: false);
        h.Ack(1);
        await Eventually.TrueAsync(() => h.Engine.ReportsDone && h.Engine.PendingLooks == 0, "the report and the lease were handled");
        Assert.Single(h.ProvisionRuns);
        Assert.Equal(failed.TaskId, (await h.Engine.ReadRunAsync(device.Id, CancellationToken.None))!.TaskId);

        // It had a password in between (reset to factory default): a new task.
        h.AutoAdd.Report(device, added: false, hadPassword: true);
        await Eventually.TrueAsync(() => h.ProvisionRuns.Count == 2, "the task of the reset camera started");
    }

    [Fact]
    public async Task The_prune_of_old_runs_never_deletes_the_snapshot_of_a_task_that_starts_meanwhile()
    {
        await using var h = new Harness();
        await h.StartAsync(h.Profile());
        var (_, device) = h.NewCamera(1);
        h.Records.ListGateCollection = ProvisioningService.SnapshotsCollection;
        h.Records.ListGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var prune = h.Engine.PruneRunsAsync(CancellationToken.None);
        await h.Records.ListEntered.Task.WaitAsync(Eventually.DefaultTimeout); // the runs are read, the snapshots not yet
        var start = h.Engine.StartForAsync([device!.Id], CancellationToken.None);
        await Eventually.StaysTrueAsync(() => !start.IsCompleted, "the start waits for the prune", TimeSpan.FromMilliseconds(200));
        h.Records.ListGate.SetResult();
        await prune;

        Assert.Equal(1, await start);
        var run = await h.Engine.ReadRunAsync(device.Id, CancellationToken.None);
        Assert.NotNull(await h.Ctx.PluginRecords.GetAsync(ProvisioningService.SnapshotsCollection, run!.Snapshot, CancellationToken.None)); // its snapshot is there
    }

    [Fact]
    public async Task A_camera_at_a_site_gets_no_task()
    {
        await using var h = new Harness();
        await h.StartAsync(h.Profile());
        var cam = h.AutoAdd.Cam(Harness.Serial(1));
        var atSite = h.AutoAdd.Managed(cam, Harness.Address(1), siteId: Guid.NewGuid());
        var (_, local) = h.NewCamera(2);

        h.AutoAdd.Report(atSite);
        h.AutoAdd.Report(local!);

        await Eventually.TrueAsync(() => h.ProvisionRuns.Count == 1, "the local camera's task");
        Assert.Equal([local!.Id], h.ProvisionedDevices);
    }

    [Fact]
    public async Task No_task_starts_while_the_active_profile_has_errors()
    {
        await using var h = new Harness();
        await h.StartAsync(h.Profile());
        h.Active = null; // the plugin answers only an active profile without errors
        var (_, device) = h.NewCamera(1);

        Assert.Equal(0, await h.Engine.StartForAsync([device!.Id], CancellationToken.None));
        Assert.Empty(h.ProvisionRuns);
    }

    [Fact]
    public async Task A_lease_of_a_new_camera_adds_it_and_starts_its_task_and_a_configured_one_is_never_added_or_changed()
    {
        await using var h = new Harness();
        await h.StartAsync(h.Profile());
        h.NewCamera(1, managed: false);
        var (configured, _) = h.NewCamera(2, hasPassword: true, managed: false);

        h.Ack(1);
        h.Ack(2);
        h.Engine.OnAck("00:11:22:33:44:55", "192.168.15.150"); // no Axis MAC: nothing
        await h.RunUntilAsync(() => h.Engine.PendingLooks == 0 && h.ProvisionRuns.Count == 1, "the leases were checked");

        Assert.Equal([h.Device(1).Id], h.ProvisionedDevices);
        Assert.Equal(DeviceStatus.PasswordNotSet, h.Device(1).Status);
        Assert.Null(h.Devices.BySerial(Harness.Serial(2))); // not added
        Assert.DoesNotContain(h.AutoAdd.Calls, c => c.EndsWith(Harness.Serial(2), StringComparison.Ordinal) && !c.StartsWith("look", StringComparison.Ordinal));
        Assert.Equal(0, configured.PasswordWrites);
    }

    [Fact]
    public async Task A_lease_of_a_managed_camera_reset_to_factory_default_starts_its_task()
    {
        await using var h = new Harness();
        await h.StartAsync(h.Profile());
        var (cam, device) = h.NewCamera(1, hasPassword: true); // OADM has it as Ok
        cam.HasPassword = false; // reset since

        h.Ack(1);

        await h.RunUntilAsync(() => h.ProvisionRuns.Count == 1, "the task started");
        Assert.Equal([device!.Id], h.ProvisionedDevices);
        Assert.DoesNotContain(h.AutoAdd.Calls, c => c.StartsWith("add", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_lease_of_a_camera_that_does_not_answer_yet_is_asked_again_for_two_minutes()
    {
        await using var h = new Harness();
        await h.StartAsync(h.Profile());
        var (late, _) = h.NewCamera(1, managed: false, configure: c => c.Answers = false);
        h.NewCamera(2, managed: false, configure: c => c.Answers = false);

        h.Ack(1);
        h.Ack(2);
        await h.RunUntilAsync(() => h.AutoAdd.Calls.Count(c => c == "look " + Harness.Serial(1)) >= 2, "asked again");
        late.Answers = true;

        await h.RunUntilAsync(() => h.Engine.PendingLooks == 0, "both leases ended");
        Assert.Equal([h.Device(1).Id], h.ProvisionedDevices);
        Assert.Null(h.Devices.BySerial(Harness.Serial(2))); // never answered within 2 minutes: nothing
    }

    [Fact]
    public async Task On_the_cameras_that_hold_a_lease_now_are_checked_at_once_and_new_leases_are_read()
    {
        await using var h = new Harness();
        h.NewCamera(1, managed: false);
        h.NewCamera(2, managed: false);
        h.Dhcp.Active.Add(new DhcpAck(0, Harness.Mac(1), Harness.Address(1), DateTimeOffset.UnixEpoch));
        h.Dhcp.Ack(Harness.Mac(3), "192.168.15.99"); // before On: never read

        await h.StartAsync(h.Profile());
        await h.RunUntilAsync(() => h.ProvisionRuns.Count == 1, "the active lease");

        h.Dhcp.Ack(Harness.Mac(2), Harness.Address(2));
        await h.Engine.PollAsync(CancellationToken.None);
        await h.RunUntilAsync(() => h.ProvisionRuns.Count == 2, "the new lease");
        Assert.Equal([h.Device(1).Id, h.Device(2).Id], h.ProvisionedDevices);
        Assert.DoesNotContain(h.AutoAdd.Calls, c => c.EndsWith(Harness.Serial(3), StringComparison.Ordinal));
    }

    [Fact]
    public async Task On_and_the_last_lease_read_survive_a_restart_and_off_is_stored()
    {
        await using var h = new Harness();
        await h.StartAsync(h.Profile());
        h.Dhcp.Ack(Harness.Mac(9), "192.168.15.199");
        await h.Engine.PollAsync(CancellationToken.None);

        await h.RestartAsync();
        Assert.True(h.Engine.IsOn);
        Assert.Equal("admin", h.Engine.State.OnBy);
        Assert.Equal(1, h.Engine.State.AckAfter);
        Assert.Null(h.Engine.StarterTasks); // the administrator's sign-in is not kept

        await h.Engine.TurnOffAsync(CancellationToken.None);
        await h.RestartAsync();
        Assert.False(h.Engine.IsOn);
    }

    // ---- the task ----

    [Fact]
    public async Task The_task_checks_the_camera_then_runs_one_named_step_per_node()
    {
        await using var h = new Harness();
        await h.StartAsync(h.Profile(Harness.Node("time", NodeKind.DateTime, Harness.Time1)));
        var (cam, device) = h.NewCamera(1);
        h.AutoAdd.Report(device!);
        await Eventually.TrueAsync(() => h.ProvisionRuns.Count == 1, "the task started");

        var result = await h.ExecuteLastAsync();

        Assert.Null(result.Failure);
        Assert.Equal(
            [$"{ProvisioningService.CheckStep}: Done", "Set the password: Done", "Set the date and time: Done", "Completed: Done"],
            result.Steps);
        Assert.Equal(ProvisioningService.NewText, result.Detail(ProvisioningService.CheckStep));
        Assert.Equal("Password set.", result.Detail("Set the password"));
        Assert.Equal(1, cam.PasswordWrites);
        var run = await h.Engine.ReadRunAsync(device!.Id, CancellationToken.None);
        Assert.True(run!.Looked);
        Assert.All(run.Results, r => Assert.Equal(NodeState.Success, r!.State));
        Assert.Null(await h.Engine.PlanAgainAsync(device.Id, CancellationToken.None)); // nothing failed
    }

    [Fact]
    public async Task A_camera_with_a_password_or_still_starting_is_never_changed()
    {
        await using var h = new Harness();
        await h.StartAsync(h.Profile());
        var (configured, withPassword) = h.NewCamera(1, hasPassword: true);
        var (starting, stillStarting) = h.NewCamera(2, configure: c => c.Starting = true);
        await h.Engine.StartForAsync([withPassword!.Id, stillStarting!.Id], CancellationToken.None);
        var run = Assert.Single(h.ProvisionRuns);

        var first = await h.ExecuteAsync(run, withPassword.Id);
        var second = await h.ExecuteAsync(run, stillStarting.Id);

        Assert.Equal(ProvisioningService.HasPasswordText, first.Failure!.Message);
        Assert.Equal($"{ProvisioningService.CheckStep}: Failed", first.Steps[0]);
        Assert.Equal("Set the password: Skipped", first.Steps[1]);
        Assert.Equal("The camera did not finish starting within 2 minutes. Nothing was changed.", second.Failure!.Message);
        Assert.Equal(0, configured.PasswordWrites + starting.PasswordWrites);
        Assert.DoesNotContain(h.AutoAdd.Calls, c => c.StartsWith("password", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Set_password_failing_stops_the_task()
    {
        await using var h = new Harness();
        await h.StartAsync(h.Profile(Harness.Node("time", NodeKind.DateTime, Harness.Time1)));
        var (_, device) = h.NewCamera(1, configure: c => c.RefusesPassword = true);
        await h.Engine.StartForAsync([device!.Id], CancellationToken.None);

        var result = await h.ExecuteLastAsync();

        Assert.Equal("The device rejected the password: too short.", result.Failure!.Message);
        Assert.Equal(["Check that the camera is new: Done", "Set the password: Failed", "Set the date and time: Skipped"], result.Steps);
        Assert.DoesNotContain(h.Ctx.TaskRuns, r => r.PluginId != ProvisioningInfo.TaskPluginId);
    }

    [Fact]
    public async Task Any_other_failing_node_continues_unless_continue_on_error_is_off_and_a_warning_always_continues()
    {
        await using var h = new Harness();
        h.Decide = run => JsonDocument.Parse(run.PayloadJson!).RootElement.GetProperty("timeZone").GetString() switch
        {
            "Europe/Vienna" => (TaskEndState.Failed, "The camera has no time API."),
            "Europe/Berlin" => (TaskEndState.DoneWithWarnings, "NTS is not available."),
            "Europe/Paris" => (TaskEndState.Failed, "The camera has no time API."),
            _ => null,
        };
        await h.StartAsync(h.Profile(
            Harness.Node("vienna", NodeKind.DateTime, Harness.Time1),
            Harness.Node("berlin", NodeKind.DateTime, Harness.Time1 with { TimeZone = "Europe/Berlin" }),
            Harness.Node("paris", NodeKind.DateTime, Harness.Time1 with { TimeZone = "Europe/Paris" }, continueOnError: false),
            Harness.Node("after", NodeKind.Autofocus)));
        var (_, device) = h.NewCamera(1);
        await h.Engine.StartForAsync([device!.Id], CancellationToken.None);

        var result = await h.ExecuteLastAsync();

        Assert.Equal("2 steps failed. The camera has no time API.", result.Failure!.Message);
        var steps = result.Context.Steps.Snapshot();
        Assert.Equal(
            [TaskStepState.Done, TaskStepState.Done, TaskStepState.Failed, TaskStepState.Warning, TaskStepState.Failed, TaskStepState.Skipped],
            steps.Select(s => s.State));
        var run = await h.Engine.ReadRunAsync(device.Id, CancellationToken.None);
        Assert.Equal([NodeState.Success, NodeState.Error, NodeState.Warning, NodeState.Error, (NodeState?)null], run!.Results.Select(r => r?.State));
    }

    [Fact]
    public async Task Run_again_runs_only_the_failed_steps_in_order_without_the_check_and_set_password()
    {
        await using var h = new Harness();
        var vienna = true;
        h.Decide = run => vienna && run.PayloadJson!.Contains("Europe/Vienna", StringComparison.Ordinal) ? (TaskEndState.Failed, "No time API.") : null;
        await h.StartAsync(h.Profile(
            Harness.Node("vienna", NodeKind.DateTime, Harness.Time1),
            Harness.Node("berlin", NodeKind.DateTime, Harness.Time1 with { TimeZone = "Europe/Berlin" }),
            Harness.Node("stop", NodeKind.DateTime, Harness.Time1 with { TimeZone = "Europe/Vienna" }, continueOnError: false),
            Harness.Node("never", NodeKind.DateTime, Harness.Time1 with { TimeZone = "Europe/Berlin" })));
        var (cam, device) = h.NewCamera(1);
        await h.Engine.StartForAsync([device!.Id], CancellationToken.None);
        await h.ExecuteLastAsync();

        var plan = await h.Engine.PlanAgainAsync(device.Id, CancellationToken.None);
        Assert.Equal([1, 3, 4], plan!.Value.Nodes!); // red, red, never ran
        vienna = false;
        var reply = await h.Engine.RunAgainAsync([device.Id, Guid.NewGuid()], CancellationToken.None);
        Assert.Equal(new RunAgainReply(1, 1), reply);
        var again = PayloadOf(h.ProvisionRuns[^1]).For(device.Id);
        Assert.False(again.Look);
        Assert.Equal(new TaskAudit("Provision with Lobby"), h.ProvisionRuns[^1].Audit); // the entry of the user who pressed it
        var result = await h.ExecuteLastAsync();

        Assert.Null(result.Failure);
        Assert.Equal(["Set the date and time: Done", "Set the date and time: Done", "Set the date and time: Done", "Completed: Done"], result.Steps);
        Assert.Equal(1, cam.PasswordWrites); // Set password did not run again
        var run = await h.Engine.ReadRunAsync(device.Id, CancellationToken.None);
        Assert.Equal(NodeState.Success, run!.Results[1]!.State);
        Assert.Equal(NodeState.Success, run.Results[2]!.State); // kept from the first run
    }

    [Fact]
    public async Task Run_again_of_a_camera_that_failed_the_check_checks_it_again_and_runs_everything()
    {
        await using var h = new Harness();
        await h.StartAsync(h.Profile());
        var (cam, device) = h.NewCamera(1, configure: c => c.Starting = true);
        await h.Engine.StartForAsync([device!.Id], CancellationToken.None);
        Assert.NotNull((await h.ExecuteLastAsync()).Failure);

        cam.Starting = false;
        Assert.Equal(new RunAgainReply(1, 0), await h.Engine.RunAgainAsync([device.Id], CancellationToken.None));
        var again = PayloadOf(h.ProvisionRuns[^1]).For(device.Id);
        Assert.True(again.Look);
        Assert.Null(again.Nodes);
        Assert.Null((await h.ExecuteLastAsync()).Failure);
        Assert.Equal(1, cam.PasswordWrites);
    }

    [Fact]
    public async Task Run_again_of_cameras_that_failed_at_different_nodes_is_one_run_with_one_audit_entry()
    {
        await using var h = new Harness();
        await h.StartAsync(h.Profile(
            Harness.Node("vienna", NodeKind.DateTime, Harness.Time1),
            Harness.Node("berlin", NodeKind.DateTime, Harness.Time1 with { TimeZone = "Europe/Berlin" })));
        var (_, first) = h.NewCamera(1);
        var (_, second) = h.NewCamera(2);
        h.Decide = run => run.DeviceIds.Contains(first!.Id)
            ? (run.PayloadJson!.Contains("Europe/Vienna", StringComparison.Ordinal) ? (TaskEndState.Failed, "No time API.") : null)
            : (run.PayloadJson!.Contains("Europe/Berlin", StringComparison.Ordinal) ? (TaskEndState.Failed, "No time API.") : null);
        await h.Engine.StartForAsync([first!.Id], CancellationToken.None);
        await h.ExecuteLastAsync();
        await h.Engine.StartForAsync([second!.Id], CancellationToken.None);
        await h.ExecuteLastAsync();
        var runs = h.ProvisionRuns.Count;

        Assert.Equal(new RunAgainReply(2, 0), await h.Engine.RunAgainAsync([first.Id, second.Id], CancellationToken.None));

        var again = Assert.Single(h.ProvisionRuns.Skip(runs));
        Assert.Equal([first.Id, second.Id], again.DeviceIds);
        Assert.Equal(new TaskAudit("Provision with Lobby"), again.Audit);
        Assert.Equal([1], PayloadOf(again).For(first.Id).Nodes!);
        Assert.Equal([2], PayloadOf(again).For(second.Id).Nodes!);
        Assert.All(new[] { first.Id, second.Id }, id => Assert.False(PayloadOf(again).For(id).Look));
    }

    [Fact]
    public async Task At_most_eight_provisioning_tasks_run_at_once()
    {
        await using var h = new Harness();
        var plugin = new ProvisioningPlugin(new ProvisioningOptions(), h.Time, h.Probe);
        Assert.Equal(8, plugin.TaskPlugins.Single().MaxParallelDevices); // the task engine's limit per plugin (min with the setting)
        await plugin.DisposeAsync();

        await h.StartAsync(h.Profile());
        var devices = Enumerable.Range(1, 20).Select(i => h.NewCamera(i).Device!).ToList();
        foreach (var device in devices)
        {
            h.AutoAdd.Report(device);
        }

        await Eventually.TrueAsync(() => h.ProvisionedDevices.Count == 20, "every camera got its task");
        Assert.Equal(20, h.ProvisionedDevices.Distinct().Count());
    }
}
