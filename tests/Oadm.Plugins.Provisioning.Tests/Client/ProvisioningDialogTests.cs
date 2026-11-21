using System.Text.Json;

using Oadm.Plugins.Provisioning.Client;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Provisioning.Tests.Client;

/// <summary>The editor Provisioning management opens: it records the editor and, when asked, saves it.</summary>
internal sealed class FakeProvisioningUi : IProvisioningUi
{
    public List<ProfileEditorViewModel> Editors { get; } = [];

    public bool Save { get; set; }

    public async Task ShowProfileEditorAsync(ProfileEditorViewModel editor)
    {
        Editors.Add(editor);
        await editor.LoadAsync();
        await editor.CheckTask;
        if (Save)
        {
            await editor.SaveCommand.ExecuteAsync(null);
        }
    }
}

/// <summary>Provisioning management: On and Off with the refusal by the switch, the active profile, the warnings, the profile list.</summary>
public sealed class ProvisioningDialogTests
{
    private static async Task<(ProvisioningDialogViewModel Dialog, FakePageContext Ctx, FakeProvisioningUi Ui)> OpenAsync(ProfilesReply? profiles = null, StatusReply? status = null)
    {
        var ctx = Samples.Context(status, profiles);
        var ui = new FakeProvisioningUi();
        var dialog = new ProvisioningDialogViewModel(ctx) { Ui = ui };
        await dialog.LoadAsync();
        return (dialog, ctx, ui);
    }

    private static StatusReply Off(string? refusal = null) => Samples.Status(on: false) with { TurnOnRefusal = refusal, Warnings = [] };

    [Fact]
    public async Task The_dialog_shows_the_state_the_active_profile_and_the_warnings()
    {
        var (dialog, _, _) = await OpenAsync();
        Assert.True(dialog.IsOn);
        Assert.Equal("Lobby cameras", dialog.ActiveChoice?.Name);
        Assert.Equal(["Lobby cameras", "Parking"], dialog.ActiveChoices.Select(r => r.Name));
        Assert.True(dialog.HasWarnings);
        Assert.Single(dialog.Warnings);

        var (off, _, _) = await OpenAsync(status: Off());
        Assert.False(off.IsOn);
        Assert.False(off.HasWarnings);
    }

    [Fact]
    public async Task On_and_off_call_the_server_and_follow_its_answer()
    {
        var (dialog, ctx, _) = await OpenAsync(status: Off());
        await dialog.ToggleCommand.ExecuteAsync(null);
        Assert.Equal(1, ctx.CallsOf(ProvisioningMethods.TurnOn));
        Assert.True(dialog.IsOn);
        Assert.Null(dialog.SwitchError);

        await dialog.ToggleCommand.ExecuteAsync(null);
        Assert.Equal(1, ctx.CallsOf(ProvisioningMethods.TurnOff));
        Assert.False(dialog.IsOn);
    }

    [Fact]
    public async Task On_is_refused_by_the_switch_with_the_servers_reason()
    {
        var (dialog, ctx, _) = await OpenAsync(status: Off(ProvisioningPlugin.ChooseActiveFirst));
        Assert.Equal(ProvisioningPlugin.ChooseActiveFirst, dialog.SwitchTip);
        var flips = 0;
        dialog.PropertyChanged += (_, e) => flips += e.PropertyName == nameof(ProvisioningDialogViewModel.IsOn) ? 1 : 0;

        await dialog.ToggleCommand.ExecuteAsync(null);
        Assert.Equal(0, ctx.CallsOf(ProvisioningMethods.TurnOn)); // the server would refuse it: nothing is sent
        Assert.Equal(ProvisioningPlugin.ChooseActiveFirst, dialog.SwitchError);
        Assert.True(dialog.HasSwitchError);
        Assert.False(dialog.IsOn);
        Assert.True(flips > 0); // the switch is told to show Off again

        // A refusal of the server itself shows there too.
        ctx.Answers[ProvisioningMethods.GetStatus] = _ => Samples.Serialize(Off());
        await dialog.LoadAsync();
        ctx.Answers[ProvisioningMethods.TurnOn] = _ => throw new InvalidOperationException("Fix the profile first.");
        await dialog.ToggleCommand.ExecuteAsync(null);
        Assert.Equal("Fix the profile first.", dialog.SwitchError);
        Assert.Null(dialog.ErrorText);
    }

    [Fact]
    public async Task Choosing_the_active_profile_activates_it_and_a_refusal_shows_below_the_choice()
    {
        var (dialog, ctx, _) = await OpenAsync();
        var parking = dialog.ActiveChoices.Single(r => r.Name == "Parking");
        ctx.Answers[ProvisioningMethods.ActivateProfile] = _ => throw new InvalidOperationException("Fix the profile first.");
        dialog.ActiveChoice = parking;
        await Oadm.Sdk.Testing.Eventually.TrueAsync(() => !dialog.IsBusy && dialog.ActiveError is not null, "the refusal");
        Assert.Equal("Fix the profile first.", dialog.ActiveError);
        Assert.Equal("Lobby cameras", dialog.ActiveChoice?.Name); // the choice goes back

        ctx.Answers[ProvisioningMethods.ActivateProfile] = _ => Samples.Serialize(Samples.Status(on: false));
        ctx.Answers[ProvisioningMethods.ListProfiles] = _ => Samples.Serialize(new ProfilesReply(
            [new ProfileSummary(Samples.ProfileId, "Lobby cameras", 5, false, true), new ProfileSummary(parking.Id, "Parking", 3, true, true)], parking.Id));
        dialog.ActiveChoice = parking;
        await Oadm.Sdk.Testing.Eventually.TrueAsync(() => !dialog.IsBusy && dialog.ActiveChoice?.Name == "Parking", "Parking is active");
        Assert.Contains(parking.Id.ToString(), ctx.LastPayloadOf(ProvisioningMethods.ActivateProfile), StringComparison.Ordinal);
        Assert.Null(dialog.ActiveError);
    }

    [Fact]
    public async Task A_status_event_updates_the_dialog_and_reads_the_profiles_again()
    {
        var (dialog, ctx, _) = await OpenAsync(status: Off());
        var reads = ctx.CallsOf(ProvisioningMethods.ListProfiles);
        await dialog.OnEventAsync(new PluginEvent(ProvisioningMethods.StatusTopic, Samples.Serialize(Samples.Status(on: true))));
        Assert.True(dialog.IsOn);
        Assert.Equal(reads + 1, ctx.CallsOf(ProvisioningMethods.ListProfiles));

        await dialog.OnEventAsync(new PluginEvent("other", "{}"));
        Assert.Equal(reads + 1, ctx.CallsOf(ProvisioningMethods.ListProfiles));
    }

    [Fact]
    public async Task The_watch_reads_first_and_applies_pushed_events()
    {
        var ctx = Samples.Context(Off());
        var dialog = new ProvisioningDialogViewModel(ctx);
        using var cts = new CancellationTokenSource();
        var watch = dialog.WatchAsync(cts.Token);
        await Oadm.Sdk.Testing.Eventually.TrueAsync(() => ctx.CallsOf(ProvisioningMethods.ListProfiles) == 1 && !dialog.IsLoading, "the first read");
        Assert.False(dialog.IsOn);

        ctx.Push(ProvisioningMethods.StatusTopic, Samples.Status(on: true));
        await Oadm.Sdk.Testing.Eventually.TrueAsync(() => dialog.IsOn, "the pushed status");
        await cts.CancelAsync();
        await watch;
    }

    [Fact]
    public async Task The_list_shows_each_profile_with_its_nodes_and_state()
    {
        var (dialog, _, _) = await OpenAsync(new ProfilesReply(
            [new ProfileSummary(Samples.ProfileId, "Lobby cameras", 5, true, true), new ProfileSummary(Guid.NewGuid(), "Draft", 3, false, false)], Samples.ProfileId));
        Assert.Equal(["Lobby cameras", "Draft"], dialog.Rows.Select(r => r.Name));
        Assert.Equal(["Lobby cameras"], dialog.ActiveChoices.Select(r => r.Name)); // a draft is never offered as the active profile
        Assert.Equal(ProvisioningTexts.Active, dialog.Rows[0].StateText);
        Assert.Equal(ProvisioningTexts.HasErrors, dialog.Rows[1].StateText);
        Assert.False(dialog.EditCommand.CanExecute(null));
        Assert.False(dialog.DeleteCommand.CanExecute(null));

        dialog.Selected = dialog.Rows[1];
        Assert.False(dialog.ActivateCommand.CanExecute(null)); // a draft never becomes active
        Assert.Equal(ProvisioningTexts.FixProfileFirst, dialog.ActivateTip);
        dialog.Selected = dialog.Rows[0];
        Assert.False(dialog.ActivateCommand.CanExecute(null));
        Assert.Equal(ProvisioningTexts.ActiveAlready, dialog.ActivateTip);
    }

    [Fact]
    public async Task Activate_makes_the_chosen_profile_active()
    {
        var (dialog, ctx, _) = await OpenAsync();
        dialog.Selected = dialog.Rows.Single(r => r.Name == "Parking");
        Assert.True(dialog.ActivateCommand.CanExecute(null));
        await dialog.ActivateCommand.ExecuteAsync(null);
        Assert.Contains(dialog.Rows.Single(r => r.Name == "Parking").Id.ToString(), ctx.LastPayloadOf(ProvisioningMethods.ActivateProfile), StringComparison.Ordinal);
        Assert.Null(dialog.ErrorText);

        ctx.Answers[ProvisioningMethods.ActivateProfile] = _ => throw new InvalidOperationException("Fix the profile first.");
        dialog.Selected = dialog.Rows.Single(r => r.Name == "Parking");
        await dialog.ActivateCommand.ExecuteAsync(null);
        Assert.Equal("Fix the profile first.", dialog.ErrorText);
    }

    [Fact]
    public async Task Delete_asks_with_a_red_delete()
    {
        var (dialog, ctx, _) = await OpenAsync();
        dialog.Selected = dialog.Rows[0];
        ctx.ConfirmAnswer = false;
        await dialog.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(0, ctx.CallsOf(ProvisioningMethods.DeleteProfile));
        Assert.Equal((ProvisioningTexts.DeleteProfile, "The profile Lobby cameras is deleted.", "Delete"), ctx.Confirmations.Single());

        ctx.ConfirmAnswer = true;
        await dialog.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(1, ctx.CallsOf(ProvisioningMethods.DeleteProfile));
        Assert.Empty(dialog.Rows);
        Assert.True(dialog.ShowEmpty);
    }

    [Fact]
    public async Task New_and_edit_open_the_profile_editor_with_the_dhcp_server_and_read_the_list_again_after_a_save()
    {
        var (dialog, ctx, ui) = await OpenAsync();
        dialog.Selected = dialog.Rows.Single(r => r.Name == "Lobby cameras");
        await dialog.EditCommand.ExecuteAsync(null);
        Assert.Equal("Lobby cameras", Assert.Single(ui.Editors).Name);
        var reads = ctx.CallsOf(ProvisioningMethods.ListProfiles);
        var statusReads = ctx.CallsOf(ProvisioningMethods.GetStatus);

        ui.Save = true;
        await dialog.NewCommand.ExecuteAsync(null);
        Assert.Equal(ProvisioningTexts.NewProfileTitle, ui.Editors[1].Title);
        var saved = JsonSerializer.Deserialize<SaveProfileRequest>(ctx.LastPayloadOf(ProvisioningMethods.SaveProfile)!, Samples.Json)!;
        Assert.Equal(ProvisioningTexts.DefaultName, saved.Profile.Name);
        Assert.Equal(reads + 1, ctx.CallsOf(ProvisioningMethods.ListProfiles));
        Assert.Equal(statusReads + 1, ctx.CallsOf(ProvisioningMethods.GetStatus)); // the first saved profile may be active now

        // The status's DHCP server reaches the editor: a new Network node starts in its network.
        var network = ui.Editors[1].AddAfter(0, Samples.Kinds.Single(k => k.Kind == Model.NodeKind.Network))!;
        ProfileEditorTests.UsePool(network);
        Assert.Equal("10.0.0.", network.Field<TextFieldViewModel>("first").Text);
    }
}
