using Oadm.Plugins.Provisioning.Engine;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Provisioning.Nodes;
using Oadm.Sdk.Tasks;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Provisioning.Tests;

/// <summary>The session and the flows (docs/spec/provisioning.md "Rules that never break" and "Tests").</summary>
public sealed class EngineTests
{
    [Fact]
    public async Task Only_factory_new_cameras_start_a_flow_and_a_configured_camera_is_never_changed()
    {
        await using var h = new Harness();
        await h.StartAsync(h.Profile());

        h.Ack(1);
        var configured = h.Ack(2, hasPassword: true);
        h.Engine.OnAck("00:11:22:33:44:55", "192.168.15.150"); // no Axis MAC: no row
        await h.RunUntilAsync(() => h.Row(1).State == CameraState.Done && h.Row(2).State == CameraState.NotNew, "both cameras ended");

        Assert.Equal(2, h.Engine.Counts().Total);
        Assert.Equal(ProvisioningEngine.NotNewText, h.Row(2).Message);
        Assert.Equal(0, configured.PasswordWrites);
        Assert.DoesNotContain(h.AutoAdd.Calls, c => c.EndsWith(Harness.Serial(2), StringComparison.Ordinal) && !c.StartsWith("look", StringComparison.Ordinal));
        Assert.Equal(["Password set.", "Added to OADM."], h.Row(1).Results.Select(r => r!.Message));
        Assert.NotNull(h.Row(1).DeviceId);
    }

    [Fact]
    public async Task No_flow_starts_while_provisioning_is_stopped()
    {
        await using var h = new Harness();
        h.Ack(1);
        Assert.Equal(0, h.Engine.Counts().Total);

        await h.StartAsync(h.Profile());
        await h.Engine.StopAsync(CancellationToken.None);
        h.Ack(2);
        Assert.Equal(0, h.Engine.Counts().Total);
        Assert.Null(h.Engine.Session);
    }

    [Fact]
    public async Task Set_password_failing_stops_the_flow()
    {
        await using var h = new Harness();
        await h.StartAsync(h.Profile(Harness.Node("time", NodeKind.DateTime, Harness.Time1)));
        h.Ack(1, configure: c => c.RefusesPassword = true);

        await h.RunUntilAsync(() => h.Row(1).State == CameraState.Failed, "the flow ended");

        var results = h.Row(1).Results;
        Assert.Equal(NodeState.Error, results[0]!.State);
        Assert.Equal("The device rejected the password: too short.", results[0]!.Message);
        Assert.Null(results[1]);
        Assert.Null(results[2]);
        Assert.DoesNotContain(h.AutoAdd.Calls, c => c.StartsWith("add", StringComparison.Ordinal));
        Assert.Empty(h.Ctx.TaskRuns);
    }

    [Fact]
    public async Task Any_other_failing_node_continues_unless_continue_on_error_is_off_and_a_warning_always_continues()
    {
        await using var h = new Harness();
        h.Outcomes["oadm.datetime"] = (TaskEndState.Failed, "The camera has no time API.");
        await h.StartAsync(h.Profile(
            Harness.Node("time", NodeKind.DateTime, Harness.Time1),
            Harness.Node("time-2", NodeKind.DateTime, Harness.Time1 with { TimeZone = "Europe/Berlin" }, continueOnError: false),
            Harness.Node("approve", NodeKind.WaitForApproval)));
        h.Ack(1);

        await h.RunUntilAsync(() => h.Row(1).State == CameraState.Failed, "the flow ended");

        var results = h.Row(1).Results;
        Assert.Equal(NodeState.Error, results[2]!.State); // continued
        Assert.Equal("The camera has no time API.", results[2]!.Message);
        Assert.Equal(NodeState.Error, results[3]!.State); // Continue on error off: stopped here
        Assert.Null(results[4]);

        // A warning continues.
        await using var w = new Harness();
        w.Outcomes["oadm.datetime"] = (TaskEndState.DoneWithWarnings, "NTS is not available; NTP is used.");
        await w.StartAsync(w.Profile(Harness.Node("time", NodeKind.DateTime, Harness.Time1 with { Nts = true }, continueOnError: false)));
        w.Ack(1);
        await w.RunUntilAsync(() => w.Row(1).State == CameraState.DoneWithWarnings, "the flow ended with a warning");
        Assert.Equal("NTS is not available; NTP is used.", w.Row(1).Results[2]!.Message);
    }

    [Fact]
    public async Task Run_failed_again_runs_only_the_red_nodes_in_order()
    {
        await using var h = new Harness();
        h.Outcomes["oadm.datetime"] = (TaskEndState.Failed, "The camera did not answer.");
        await h.StartAsync(h.Profile(
            Harness.Node("time", NodeKind.DateTime, Harness.Time1),
            Harness.Node("time-2", NodeKind.DateTime, Harness.Time1 with { TimeZone = "Europe/Berlin" })));
        h.Ack(1);
        await h.RunUntilAsync(() => h.Row(1).State == CameraState.Failed, "the flow failed");
        Assert.Equal(2, h.Ctx.TaskRuns.Count);
        var passwordWrites = h.AutoAdd.Cams[Harness.Serial(1)].PasswordWrites;

        h.Outcomes["oadm.datetime"] = (TaskEndState.Done, null);
        Assert.Equal(1, h.Engine.RunFailedAgain([Harness.Mac(1)]));
        await h.RunUntilAsync(() => h.Row(1).State == CameraState.Done, "the red nodes ran again");

        Assert.Equal(4, h.Ctx.TaskRuns.Count);
        Assert.Contains("Europe/Vienna", h.Ctx.TaskRuns[2].PayloadJson, StringComparison.Ordinal); // in flow order
        Assert.Contains("Europe/Berlin", h.Ctx.TaskRuns[3].PayloadJson, StringComparison.Ordinal);
        Assert.Equal(passwordWrites, h.AutoAdd.Cams[Harness.Serial(1)].PasswordWrites); // the green nodes did not run again
        Assert.Single(h.AutoAdd.Calls, c => c.StartsWith("add", StringComparison.Ordinal));
        Assert.Equal(0, h.Engine.RunFailedAgain([Harness.Mac(1)])); // only failed cameras
    }

    [Fact]
    public async Task At_most_8_cameras_run_at_once_and_more_are_queued()
    {
        await using var h = new Harness();
        h.AutoAdd.PasswordGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await h.StartAsync(h.Profile());
        for (var i = 1; i <= 12; i++)
        {
            h.Ack(i);
        }

        await Eventually.TrueAsync(() => h.AutoAdd.WaitingAtGate == 8, "8 cameras at the gate");
        Assert.Equal(4, h.Engine.Counts().Queued);
        Assert.Equal(8, h.Engine.Counts().Running);

        h.AutoAdd.PasswordGate.SetResult();
        await h.RunUntilAsync(() => h.Engine.Counts().Done == 12, "every camera done");
        Assert.Equal(8, h.Engine.PeakParallel);
    }

    [Fact]
    public async Task A_camera_waits_for_approval_without_holding_a_place_until_continue()
    {
        await using var h = new Harness(new ProvisioningOptions { MaxParallel = 1 });
        await h.StartAsync(h.Profile(Harness.Node("approve", NodeKind.WaitForApproval), Harness.Node("time", NodeKind.DateTime, Harness.Time1)));
        h.Ack(1);
        await h.RunUntilAsync(() => h.Row(1).WaitingForApproval, "the first camera waits");

        h.Ack(2); // the only place is free while the first one waits
        await h.RunUntilAsync(() => h.Row(2).WaitingForApproval, "the second camera waits too");
        Assert.Equal(2, h.Engine.Counts().Waiting);
        Assert.Empty(h.Ctx.TaskRuns);

        Assert.Equal(1, h.Engine.Continue([Harness.Mac(1)], "anna"));
        await h.RunUntilAsync(() => h.Row(1).State == CameraState.Done, "the first camera went on");
        Assert.Equal("Continued by anna.", h.Row(1).Results[2]!.Message);
        Assert.True(h.Row(2).WaitingForApproval);
        Assert.Equal(0, h.Engine.Continue([Harness.Mac(1)], "anna")); // nothing waits there any more
    }

    [Fact]
    public async Task A_camera_that_does_not_answer_within_2_minutes_fails_at_set_password()
    {
        await using var h = new Harness();
        await h.StartAsync(h.Profile());
        h.Ack(1, configure: c => c.Answers = false);

        await h.RunUntilAsync(() => h.Row(1).State == CameraState.Failed, "the camera failed");

        Assert.Equal("The camera did not answer at 192.168.15.101 within 2 minutes.", h.Row(1).Results[0]!.Message);
        Assert.Equal(0, h.AutoAdd.Cams[Harness.Serial(1)].PasswordWrites);
    }

    [Fact]
    public async Task A_camera_on_dhcp_follows_its_new_address()
    {
        await using var h = new Harness();
        await h.StartAsync(h.Profile());
        h.Ack(1, configure: c => c.Answers = false);
        await h.RunUntilAsync(() => h.Row(1).State == CameraState.Failed, "the camera failed");

        h.Engine.OnAck(Harness.Mac(1), "192.168.15.140"); // a new lease after a restart of the camera

        Assert.Equal("192.168.15.140", h.Row(1).Address);
    }

    [Fact]
    public async Task Rows_that_could_not_be_stored_are_stored_with_the_next_write()
    {
        await using var h = new Harness();
        await h.StartAsync(h.Profile());
        h.Ack(1, hasPassword: true);
        await h.RunUntilAsync(() => h.Row(1).State == CameraState.NotNew, "the camera was looked at");

        h.Records.FailWrites = int.MaxValue;
        h.Engine.OnAck(Harness.Mac(1), "192.168.15.150");
        await Assert.ThrowsAsync<IOException>(() => h.Engine.FlushAsync(CancellationToken.None));
        h.Records.FailWrites = 0;
        await h.Engine.FlushAsync(CancellationToken.None);

        var stored = await h.Ctx.PluginRecords.ListAsync(ProvisioningEngine.CamerasCollection, CancellationToken.None);
        Assert.Contains(stored.Values, json => json.Contains("192.168.15.150", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stop_ends_the_flows_and_the_session_and_rows_stay_until_the_next_start()
    {
        await using var h = new Harness();
        await h.StartAsync(h.Profile(Harness.Node("approve", NodeKind.WaitForApproval)));
        h.Ack(1);
        await h.RunUntilAsync(() => h.Row(1).WaitingForApproval, "the camera waits");

        await h.Engine.StopAsync(CancellationToken.None);

        Assert.Equal(CameraState.Failed, h.Row(1).State);
        Assert.Equal(ProvisioningEngine.StoppedText, h.Row(1).Results[2]!.Message);
        Assert.Null(await h.Ctx.PluginSettings.GetAsync(ProvisioningEngine.SessionKey, CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => h.Engine.RunFailedAgain([Harness.Mac(1)]));

        await h.StartAsync(h.Profile());
        Assert.Equal(0, h.Engine.Counts().Total);
    }

    [Fact]
    public async Task Start_is_refused_without_a_running_dhcp_server_and_twice()
    {
        await using var h = new Harness();
        h.Dhcp.State = h.Dhcp.State with { IsRunning = false };
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => h.StartAsync(h.Profile()));
        Assert.Equal("Turn on the DHCP server first.", refused.Message);

        h.Dhcp.State = h.Dhcp.State with { IsRunning = true };
        await h.StartAsync(h.Profile());
        Assert.Equal(ProvisioningEngine.StartedAlready, (await Assert.ThrowsAsync<InvalidOperationException>(() => h.StartAsync(h.Profile()))).Message);
    }

    [Fact]
    public async Task Leases_reach_the_session_through_the_dhcp_server_and_cameras_with_a_lease_at_start_too()
    {
        await using var h = new Harness();
        h.AutoAdd.Cam(Harness.Serial(1));
        h.AutoAdd.Cam(Harness.Serial(2));
        h.Dhcp.Ack(Harness.Mac(9), "192.168.15.109"); // before Start: not read again
        h.Dhcp.Active.Add(new Sdk.Network.DhcpAck(0, Harness.Mac(1), "192.168.15.101", DateTimeOffset.UnixEpoch));
        await h.StartAsync(h.Profile());
        Assert.NotNull(h.Engine.Find(Harness.Mac(1)));

        h.Dhcp.Ack(Harness.Mac(2), "192.168.15.102");
        await h.Engine.PollAsync(CancellationToken.None);

        Assert.NotNull(h.Engine.Find(Harness.Mac(2)));
        Assert.Null(h.Engine.Find(Harness.Mac(9)));
        Assert.Equal(2, h.Engine.Session!.AckAfter);
        await h.RunUntilAsync(() => h.Engine.Counts().Done == 2, "both done");
    }

    [Fact]
    public async Task The_session_and_the_rows_survive_a_restart_and_a_flow_that_ran_ends_failed()
    {
        await using var h = new Harness();
        await h.StartAsync(h.Profile(Harness.Node("approve", NodeKind.WaitForApproval)));
        h.Ack(1);
        h.Ack(2, hasPassword: true);
        await h.RunUntilAsync(() => h.Row(1).WaitingForApproval && h.Row(2).State == CameraState.NotNew, "one waits, one is not new");
        await h.Engine.FlushAsync(CancellationToken.None);

        await h.RestartAsync();

        Assert.NotNull(h.Engine.Session);
        Assert.Equal(CameraState.Failed, h.Row(1).State);
        Assert.Equal(ProvisioningEngine.RestartedText, h.Row(1).Results[2]!.Message);
        Assert.Equal(CameraState.NotNew, h.Row(2).State);
        Assert.Equal(1, h.Engine.RunFailedAgain([Harness.Mac(1)]));
        await h.RunUntilAsync(() => h.Row(1).WaitingForApproval, "it waits again");
    }

    [Fact]
    public async Task Changes_reach_the_page_in_batches_with_the_rows_and_counts()
    {
        await using var h = new Harness();
        await h.StartAsync(h.Profile());
        for (var i = 1; i <= 3; i++)
        {
            h.Ack(i, hasPassword: true);
        }

        await h.RunUntilAsync(() => h.Engine.Counts().NotNew == 3, "three rows");
        await h.Engine.FlushAsync(CancellationToken.None);

        var events = h.Ctx.PublishedEvents.Where(e => e.Topic == ProvisioningEngine.CamerasTopic).ToList();
        Assert.Single(events);
        var batch = Sdk.Plugins.PluginJson.Parse<CamerasEvent>(events[0].PayloadJson, ProvisioningJson.Options);
        Assert.Equal(3, batch.Changed.Count);
        Assert.Equal(3, batch.Counts.NotNew);
        Assert.Equal(3, (await h.Ctx.PluginRecords.ListAsync(ProvisioningEngine.CamerasCollection, CancellationToken.None)).Count);

        Assert.Equal(1, h.Engine.Remove([Harness.Mac(1)]));
        await h.Engine.FlushAsync(CancellationToken.None);
        var removed = Sdk.Plugins.PluginJson.Parse<CamerasEvent>(h.Ctx.PublishedEvents.Last().PayloadJson, ProvisioningJson.Options);
        Assert.Equal([Harness.Mac(1)], removed.Removed);
        Assert.Equal(2, (await h.Ctx.PluginRecords.ListAsync(ProvisioningEngine.CamerasCollection, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task The_camera_table_is_paged_by_mac_address()
    {
        await using var h = new Harness();
        await h.StartAsync(h.Profile());
        for (var i = 1; i <= 5; i++)
        {
            h.Ack(i, hasPassword: true);
        }

        var first = h.Engine.Page(null, 2);
        Assert.Equal([Harness.Mac(1), Harness.Mac(2)], first.Rows.Select(r => r.Mac));
        Assert.Equal(5, first.Total);
        var second = h.Engine.Page(first.Next, 2);
        Assert.Equal([Harness.Mac(3), Harness.Mac(4)], second.Rows.Select(r => r.Mac));
        Assert.Null(h.Engine.Page(second.Next, 2).Next);
        await h.RunUntilAsync(() => h.Engine.Counts().NotNew == 5, "all looked at");
    }
}
