using System.Text.Json;

using Oadm.Plugins.Provisioning.Client;

namespace Oadm.Plugins.Provisioning.Tests.Client;

/// <summary>The editor Manage profiles opens: it records the editor and, when asked, saves it.</summary>
internal sealed class FakeProfilesUi : IProfilesUi
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

/// <summary>Manage profiles: the list with its states, New, Edit, Activate and Delete.</summary>
public sealed class ProfilesDialogTests
{
    private static async Task<(ProfilesDialogViewModel Dialog, FakePageContext Ctx, FakeProfilesUi Ui)> OpenAsync(ProfilesReply? profiles = null)
    {
        var ctx = Samples.Context(profiles: profiles);
        var ui = new FakeProfilesUi();
        var dialog = new ProfilesDialogViewModel(ctx) { Ui = ui };
        await dialog.LoadAsync();
        return (dialog, ctx, ui);
    }

    [Fact]
    public async Task The_list_shows_each_profile_with_its_nodes_and_state()
    {
        var (dialog, _, _) = await OpenAsync(new ProfilesReply(
            [new ProfileSummary(Samples.ProfileId, "Lobby cameras", 5, true, true), new ProfileSummary(Guid.NewGuid(), "Draft", 3, false, false)], Samples.ProfileId));
        Assert.Equal(["Lobby cameras", "Draft"], dialog.Rows.Select(r => r.Name));
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
    public async Task New_and_edit_open_the_profile_editor_and_read_the_list_again_after_a_save()
    {
        var (dialog, ctx, ui) = await OpenAsync();
        dialog.Selected = dialog.Rows.Single(r => r.Name == "Lobby cameras");
        await dialog.EditCommand.ExecuteAsync(null);
        Assert.Equal("Lobby cameras", Assert.Single(ui.Editors).Name);
        var reads = ctx.CallsOf(ProvisioningMethods.ListProfiles);

        ui.Save = true;
        await dialog.NewCommand.ExecuteAsync(null);
        Assert.Equal(ProvisioningTexts.NewProfileTitle, ui.Editors[1].Title);
        var saved = JsonSerializer.Deserialize<SaveProfileRequest>(ctx.LastPayloadOf(ProvisioningMethods.SaveProfile)!, Samples.Json)!;
        Assert.Equal(ProvisioningTexts.DefaultName, saved.Profile.Name);
        Assert.Equal(reads + 1, ctx.CallsOf(ProvisioningMethods.ListProfiles));
    }
}
