using System.Text.Json;

using Oadm.Plugins.Provisioning.Client;
using Oadm.Plugins.Provisioning.Engine;
using Oadm.Plugins.Provisioning.Model;

namespace Oadm.Plugins.Provisioning.Tests.Client;

/// <summary>The page's view model against canned server answers: no Avalonia.</summary>
public sealed class PageViewModelTests
{
    private static async Task<(ProvisioningViewModel Vm, FakePageContext Ctx)> LoadAsync(FakePageContext? ctx = null)
    {
        ctx ??= Samples.Context();
        var vm = new ProvisioningViewModel(ctx);
        await vm.LoadAsync();
        return (vm, ctx);
    }

    [Fact]
    public async Task Load_fills_the_dhcp_card_the_state_the_profiles_and_every_camera()
    {
        var (vm, _) = await LoadAsync();

        Assert.True(vm.DhcpIsRunning);
        Assert.Equal("Running", vm.DhcpStatusText);
        Assert.Equal("Interface Ethernet (10.0.0.17)", vm.DhcpInterfaceText);
        Assert.Equal("Range 10.0.0.100 to 10.0.0.199", vm.DhcpRangeText);
        Assert.Equal(ProvisioningTexts.Edit, vm.DhcpButtonText);
        Assert.Equal("The pool overlaps the DHCP server's range 10.0.0.100 to 10.0.0.199.", vm.WarningText);
        Assert.True(vm.IsStarted);
        Assert.StartsWith("Started ", vm.StateText, StringComparison.Ordinal);
        Assert.EndsWith(" by anna", vm.StateText, StringComparison.Ordinal);
        Assert.Equal("Lobby cameras", vm.ActiveProfile?.Name);
        Assert.Equal(8, vm.Rows.Count);
        Assert.False(vm.ShowEmpty);
        Assert.False(vm.IsLoading);

        var failed = vm.Rows.Single(r => r.State == CameraState.Failed);
        Assert.Equal("Failed", failed.StateText);
        Assert.Equal("Set IP", failed.NodeText);
        Assert.StartsWith("The camera did not take 192.168.15.12", failed.Problem, StringComparison.Ordinal);
        Assert.Equal("AXIS Q6135-LE", failed.Model);
        Assert.Equal(ProvisioningTexts.WaitingForApproval, vm.Rows.Single(r => r.IsWaiting).StateText);
        Assert.Equal(ProvisioningTexts.NotNew, vm.Rows.Single(r => r.State == CameraState.NotNew).StateText);
    }

    [Fact]
    public async Task Cameras_are_read_in_pages()
    {
        var rows = Enumerable.Range(0, 2500).Select(i => Samples.Row(1, "AXIS P3265-LVE", "10.0.0.1", CameraState.Done, 4, []) with { Mac = $"AC:CC:8E:{i / 256:X2}:{i % 256:X2}:00" }).ToList();
        var (vm, ctx) = await LoadAsync(Samples.Context(cameras: rows));

        Assert.Equal(2500, vm.CameraCount);
        Assert.Equal(3, ctx.CallsOf(ProvisioningMethods.ListCameras));
    }

    [Fact]
    public async Task The_loading_overlay_shows_until_the_first_read_arrived()
    {
        var ctx = Samples.Context();
        var gate = new TaskCompletionSource();
        ctx.Gate = gate.Task;
        var vm = new ProvisioningViewModel(ctx);
        var load = vm.LoadAsync();
        Assert.True(vm.IsLoading);
        Assert.False(vm.ShowEmpty);
        gate.SetResult();
        await load;
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task The_empty_line_says_what_to_do_first()
    {
        var (vm, _) = await LoadAsync(Samples.Context(Samples.Status(started: false, dhcpRunning: false), []));
        Assert.True(vm.ShowEmpty);
        Assert.Equal(ProvisioningTexts.EmptyDhcpFirst, vm.EmptyText);
        Assert.Equal(ProvisioningTexts.TurnOn, vm.DhcpButtonText);
        Assert.Equal(ProvisioningTexts.DhcpFirst, vm.StartBlockedReason);
        Assert.False(vm.StartCommand.CanExecute(null));

        (vm, _) = await LoadAsync(Samples.Context(Samples.Status(started: false), [], new ProfilesReply([], null)));
        Assert.Equal(ProvisioningTexts.EmptyProfileFirst, vm.EmptyText);
        Assert.Equal(ProvisioningTexts.ProfileFirst, vm.StartBlockedReason);

        (vm, _) = await LoadAsync(Samples.Context(Samples.Status(started: false), []));
        Assert.Equal(ProvisioningTexts.EmptyNotStarted, vm.EmptyText);
        Assert.Null(vm.StartBlockedReason);
        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.Null(vm.WarningText);

        (vm, _) = await LoadAsync(Samples.Context(Samples.Status(started: false), [], Samples.Profiles(valid: false)));
        Assert.Equal(ProvisioningTexts.FixProfileFirst, vm.StartBlockedReason);
        Assert.Equal(ProvisioningTexts.FixProfileFirst, vm.StartTip);

        (vm, _) = await LoadAsync(Samples.Context(Samples.Status(started: true, counts: new CameraCounts(0, 0, 0, 0, 0, 0, 0, 0)), []));
        Assert.Equal(ProvisioningTexts.EmptyStarted, vm.EmptyText);

        vm.SearchText = "nothing";
        Assert.Equal(ProvisioningTexts.EmptyStarted, vm.EmptyText);
    }

    [Fact]
    public async Task Start_and_stop_call_the_server_and_stop_asks_first()
    {
        var (vm, ctx) = await LoadAsync(Samples.Context(Samples.Status(started: false), []));
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(1, ctx.CallsOf(ProvisioningMethods.Start));
        Assert.True(vm.IsStarted);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.True(vm.StopCommand.CanExecute(null));

        ctx.ConfirmAnswer = false;
        await vm.StopCommand.ExecuteAsync(null);
        Assert.Equal(0, ctx.CallsOf(ProvisioningMethods.Stop));
        Assert.Equal((ProvisioningTexts.StopTitle, ProvisioningTexts.StopMessage, ProvisioningTexts.Stop), ctx.Confirmations.Single());

        ctx.ConfirmAnswer = true;
        await vm.StopCommand.ExecuteAsync(null);
        Assert.Equal(1, ctx.CallsOf(ProvisioningMethods.Stop));
        Assert.False(vm.IsStarted);
        Assert.Equal(ProvisioningTexts.NotStarted, vm.StateText);
    }

    [Fact]
    public async Task A_refused_start_shows_the_reason()
    {
        var ctx = Samples.Context(Samples.Status(started: false), []);
        ctx.Answers[ProvisioningMethods.Start] = _ => throw new InvalidOperationException("Turn on the DHCP server first.");
        var (vm, _) = await LoadAsync(ctx);
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal("Turn on the DHCP server first.", vm.ErrorText);
        Assert.False(vm.IsStarted);
    }

    [Fact]
    public async Task Turn_on_and_edit_open_the_dhcp_servers_own_page()
    {
        var (vm, ctx) = await LoadAsync();
        await vm.OpenDhcpCommand.ExecuteAsync(null);
        Assert.Equal(["plugin:oadm.dhcp-server"], ctx.Opened);
    }

    [Fact]
    public async Task Choosing_another_profile_makes_it_active_and_a_refusal_goes_back()
    {
        var (vm, ctx) = await LoadAsync();
        var parking = vm.Profiles.Single(p => p.Name == "Parking");
        vm.ActiveProfile = parking;
        await Oadm.Sdk.Testing.Eventually.TrueAsync(() => ctx.CallsOf(ProvisioningMethods.ActivateProfile) == 1, "activate was called");
        Assert.Contains(parking.Id.ToString(), ctx.LastPayloadOf(ProvisioningMethods.ActivateProfile), StringComparison.Ordinal);

        ctx.Answers[ProvisioningMethods.ActivateProfile] = _ => throw new InvalidOperationException("Fix the profile first. Set IP: Enter an IP address.");
        await vm.LoadAsync();
        var before = vm.ActiveProfile;
        vm.ActiveProfile = vm.Profiles.Single(p => p.Name == "Parking");
        await Oadm.Sdk.Testing.Eventually.TrueAsync(() => vm.ErrorText is not null, "the refusal shows");
        Assert.Equal("Fix the profile first. Set IP: Enter an IP address.", vm.ErrorText);
        Assert.Equal(before, vm.ActiveProfile);
    }

    [Fact]
    public async Task Manage_profiles_opens_the_dialog_and_reads_the_profiles_again_after_it()
    {
        var (vm, ctx) = await LoadAsync();
        var ui = new FakePageUi();
        vm.Ui = ui;
        var before = ctx.CallsOf(ProvisioningMethods.ListProfiles);
        await vm.ManageProfilesCommand.ExecuteAsync(null);
        var dialog = Assert.Single(ui.Dialogs);
        Assert.Equal(before + 1, ctx.CallsOf(ProvisioningMethods.ListProfiles));

        // The page's DHCP server reaches the editor: a new Set IP starts in its range.
        var profilesUi = new FakeProfilesUi();
        dialog.Ui = profilesUi;
        await dialog.NewCommand.ExecuteAsync(null);
        var setIp = Assert.Single(profilesUi.Editors).Nodes.Single(n => n.Kind == NodeKind.SetIp);
        Assert.Equal("10.0.0.", setIp.Field<TextFieldViewModel>("first").Text);
    }

    [Fact]
    public async Task The_row_actions_follow_the_selection()
    {
        var (vm, ctx) = await LoadAsync();
        Assert.False(vm.RunFailedAgainCommand.CanExecute(null));
        Assert.False(vm.ContinueCommand.CanExecute(null));
        Assert.False(vm.RemoveCommand.CanExecute(null));

        var failed = vm.Rows.Single(r => r.State == CameraState.Failed);
        var waiting = vm.Rows.Single(r => r.IsWaiting);
        var done = vm.Rows.Single(r => r.State == CameraState.Done);
        vm.SetSelection([done, failed, waiting]);
        Assert.True(vm.RunFailedAgainCommand.CanExecute(null));
        Assert.True(vm.ContinueCommand.CanExecute(null));
        Assert.Same(waiting, vm.FlowCamera);

        await vm.RunFailedAgainCommand.ExecuteAsync(null);
        Assert.Equal([failed.Mac], JsonSerializer.Deserialize<MacsRequest>(ctx.LastPayloadOf(ProvisioningMethods.RunFailedAgain)!, Samples.Json)!.Macs);
        await vm.ContinueCommand.ExecuteAsync(null);
        Assert.Equal([waiting.Mac], JsonSerializer.Deserialize<MacsRequest>(ctx.LastPayloadOf(ProvisioningMethods.Continue)!, Samples.Json)!.Macs);

        ctx.ConfirmAnswer = false;
        await vm.RemoveCommand.ExecuteAsync(null);
        Assert.Equal(0, ctx.CallsOf(ProvisioningMethods.RemoveCameras));
        Assert.Equal((ProvisioningTexts.RemoveTitle, "3 rows are removed. The cameras stay in OADM.", ProvisioningTexts.Remove), ctx.Confirmations.Single());

        ctx.ConfirmAnswer = true;
        await vm.RemoveCommand.ExecuteAsync(null);
        Assert.Equal(5, vm.Rows.Count);
        Assert.Empty(vm.SelectedRows);
        Assert.Null(vm.FlowCamera);
    }

    [Fact]
    public async Task Without_the_right_to_manage_nothing_changes()
    {
        var ctx = Samples.Context();
        ctx.Denied.Add(Oadm.Sdk.Plugins.PermissionKeys.Manage);
        var (vm, _) = await LoadAsync(ctx);
        vm.SetSelection(vm.Rows);
        Assert.False(vm.MayManage);
        Assert.False(vm.StopCommand.CanExecute(null));
        Assert.False(vm.RunFailedAgainCommand.CanExecute(null));
        Assert.False(vm.RemoveCommand.CanExecute(null));
        Assert.False(vm.ManageProfilesCommand.CanExecute(null));
    }

    [Fact]
    public async Task Camera_events_change_rows_in_place_add_new_ones_drop_removed_ones_and_ignore_stale_ones()
    {
        var (vm, ctx) = await LoadAsync();
        var queued = vm.Rows.Single(r => r.State == CameraState.Queued);
        var notNew = vm.Rows.Single(r => r.State == CameraState.NotNew);
        var running = Samples.Row(6, "AXIS P3268-LV", "10.0.0.134", CameraState.Running, 0, [null, null, null, null, null], version: 5);
        var added = Samples.Row(9, "AXIS M1135-E", "10.0.0.140", CameraState.Queued, -1, [], version: 1);
        var counts = new CameraCounts(8, 1, 3, 1, 1, 1, 1, 0);

        vm.HandleEvent(new Oadm.Sdk.Plugins.PluginEvent(ProvisioningMethods.CamerasTopic, Samples.Serialize(new CamerasEvent([running, added], [notNew.Mac], counts, 9))));

        Assert.Same(queued, vm.Rows.Single(r => r.Mac == running.Mac));
        Assert.Equal(CameraState.Running, queued.State);
        Assert.Equal("Set password", queued.NodeText);
        Assert.DoesNotContain(vm.Rows, r => r.Mac == notNew.Mac);
        Assert.Contains(vm.Rows, r => r.Mac == added.Mac);
        Assert.Equal(8, vm.Rows.Count);

        // An older version of a row never overwrites a newer one.
        vm.HandleEvent(new Oadm.Sdk.Plugins.PluginEvent(ProvisioningMethods.CamerasTopic,
            Samples.Serialize(new CamerasEvent([running with { State = CameraState.Queued, Version = 4 }], [], counts, 10))));
        Assert.Equal(CameraState.Running, queued.State);
        _ = ctx;
    }

    [Fact]
    public async Task A_status_event_after_start_clears_the_last_sessions_rows()
    {
        var (vm, _) = await LoadAsync(Samples.Context(Samples.Status(started: false)));
        Assert.Equal(8, vm.Rows.Count);
        vm.HandleEvent(new Oadm.Sdk.Plugins.PluginEvent(ProvisioningMethods.StatusTopic, Samples.Serialize(Samples.Status(started: true, counts: new CameraCounts(0, 0, 0, 0, 0, 0, 0, 0)))));
        Assert.True(vm.IsStarted);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task The_search_filters_by_mac_model_serial_and_address()
    {
        var (vm, _) = await LoadAsync();
        vm.SearchText = "q6135";
        Assert.Equal("AXIS Q6135-LE", Assert.Single(vm.Rows).Model);
        vm.SearchText = "10.0.0.13";
        Assert.Equal(6, vm.Rows.Count);
        vm.SearchText = "ac:cc:8e:4a:00:07";
        Assert.Single(vm.Rows);
        vm.SearchText = "ACCC8E4A0008";
        Assert.Single(vm.Rows);
        vm.SearchText = "nothing";
        Assert.True(vm.ShowEmpty);
        Assert.Equal(ProvisioningTexts.NoMatch, vm.EmptyText);
        vm.SearchText = string.Empty;
        Assert.Equal(8, vm.Rows.Count);
    }

    [Fact]
    public async Task A_cameras_flow_colours_each_node_by_its_result()
    {
        var (vm, _) = await LoadAsync();
        vm.SetSelection([vm.Rows.Single(r => r.State == CameraState.Failed)]);

        var nodes = vm.Flow.Nodes;
        Assert.Equal(["Set password", "Add to OADM", "Wait for approval", "Set IP", "Date and time"], nodes.Select(n => n.Name));
        Assert.Equal(["Oadm.StatusOkBrush", "Oadm.StatusWarningBrush", "Oadm.StatusOkBrush", "Oadm.StatusErrorBrush", "Oadm.TextDisabledBrush"], nodes.Select(n => n.BrushKey));
        Assert.Equal("Password set.", nodes[0].Line);
        Assert.Equal("Added to OADM. The tag Lobby could not be set.", nodes[1].Line);
        Assert.True(nodes[3].HasError);
        Assert.StartsWith("The camera did not take 192.168.15.12", nodes[3].Below, StringComparison.Ordinal);
        Assert.Equal(ProvisioningTexts.NotRun, nodes[4].Line);
        Assert.True(nodes[4].IsDimmed);
        Assert.False(vm.Flow.CanSelect);

        vm.SetSelection([vm.Rows.Single(r => r.IsWaiting)]);
        Assert.Equal("Oadm.AccentBrush", vm.Flow.Nodes[2].BrushKey);
        Assert.Equal(ProvisioningTexts.WaitingForApproval, vm.Flow.Nodes[2].Line);

        vm.SetSelection([]);
        Assert.False(vm.HasFlow);
        Assert.Empty(vm.Flow.Nodes);
    }

    [Fact]
    public async Task The_open_flow_follows_the_cameras_events()
    {
        var (vm, _) = await LoadAsync();
        var running = vm.Rows.Single(r => r.Mac == "AC:CC:8E:4A:00:04");
        vm.SetSelection([running]);
        Assert.Equal("Oadm.AccentBrush", vm.Flow.Nodes[3].BrushKey);

        var done = running.Row with
        {
            State = CameraState.Done, NodeIndex = 4, Version = 2,
            Results = [.. running.Row.Results.Take(3), NodeResult.Success("192.168.15.13 assigned."), NodeResult.Success("Time zone Europe/Vienna, NTP 10.0.0.17.")],
        };
        vm.HandleEvent(new Oadm.Sdk.Plugins.PluginEvent(ProvisioningMethods.CamerasTopic, Samples.Serialize(new CamerasEvent([done], [], Samples.Counts(Samples.Cameras()), 3))));
        Assert.All(vm.Flow.Nodes, n => Assert.Equal("Oadm.StatusOkBrush", n.BrushKey));
        Assert.Equal("192.168.15.13 assigned.", vm.Flow.Nodes[3].Line);
    }
}
