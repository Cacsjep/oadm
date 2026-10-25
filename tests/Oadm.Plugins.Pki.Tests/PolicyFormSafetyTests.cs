using Oadm.Plugins.Pki.Client;
using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// The policy form after the review of 2026-10-25 (docs/spec/pki.md "Policies"): the devices are a field of the validator, a
/// reload never replaces a form whose dialog is open, a save over another user's change is refused, a save locks the form,
/// a refusal of a field the form does not show goes into a message, and an issuing policy whose CA cannot issue has its dot.
/// </summary>
public sealed class PolicyFormSafetyTests
{
    [Fact]
    public async Task The_overlap_blocks_save_below_the_devices_until_the_choice_changes()
    {
        await using var pki = await PolicyPageTests.StartAsync();
        var page = new PoliciesViewModel(PolicyPageTests.Page(pki));
        await page.LoadAsync(CancellationToken.None);
        var editor = page.NewEditor(PolicyKinds.Vlan);
        editor.Name = "PTZ";
        editor.Vlan = "40";
        var picker = editor.NewPicker();
        picker.Visible.Single(i => i.Id == PolicyPageTests.Camera.Id).Chosen = true; // 10.0.0.48 is in Building B already
        editor.UsePicker(picker);
        Assert.True(editor.CanSave);

        Assert.False(await editor.SaveAsync());

        Assert.Equal("10.0.0.48 is in PTZ and Building B.", editor.DevicesError);
        Assert.False(editor.CanSave); // the validator holds it: Save waits for another choice
        Assert.Equal("10.0.0.48 is in PTZ and Building B.", editor.SaveBlockedReason);
        var other = editor.NewPicker();
        Assert.Equal("10.0.0.48 is in PTZ and Building B.", other.ErrorText); // the picker shows it too
        other.Visible.Single(i => i.Id == PolicyPageTests.Camera.Id).Chosen = false;
        other.Visible.Single(i => i.Id == PolicyPageTests.Other.Id).Chosen = true;
        editor.UsePicker(other);
        Assert.Null(editor.DevicesError);
        Assert.True(editor.CanSave);
    }

    [Fact]
    public async Task Tags_of_a_block_that_also_revokes_are_an_error_of_the_devices_field()
    {
        await using var pki = await PolicyPageTests.StartAsync();
        var page = new PoliciesViewModel(PolicyPageTests.Page(pki));
        await page.LoadAsync(CancellationToken.None);
        var editor = page.NewEditor(PolicyKinds.Block);
        editor.Name = "Stolen";
        var picker = editor.NewPicker();
        picker.Tags.Single(t => t.Name == "PTZ").IsChecked = true;
        editor.UsePicker(picker);
        Assert.Null(editor.DevicesError);

        editor.AlsoRevoke = true;

        Assert.Equal(PolicyTexts.RevokeNoTags, editor.DevicesError);
        Assert.False(editor.CanSave);
        editor.AlsoRevoke = false;
        Assert.Null(editor.DevicesError);
        Assert.True(editor.CanSave);
    }

    [Fact]
    public async Task A_reload_while_choose_devices_is_open_keeps_the_form_and_its_choice()
    {
        await using var pki = await PolicyPageTests.StartAsync();
        var page = new PoliciesViewModel(PolicyPageTests.Page(pki));
        await page.LoadAsync(CancellationToken.None);
        var editor = PolicyPageTests.Open(page, "Storage room");
        page.Dialogs = new PolicyPageTests.PickerAnswers(true, p =>
        {
            page.Apply(page.List); // the list came again while the dialog was open (another user saved something)
            p.Visible.Single(i => i.Id == PolicyPageTests.Camera.Id).Chosen = true;
        });

        await editor.ChooseDevicesCommand.ExecuteAsync(null);

        Assert.Same(editor, page.Editor); // the dialog's choice went into the form that is still open
        Assert.True(editor.IsDirty);
        Assert.Equal("2 devices", editor.DevicesText);
    }

    [Fact]
    public async Task A_save_over_a_change_another_user_made_meanwhile_is_refused()
    {
        await using var pki = await PolicyPageTests.StartAsync();
        var mine = PolicyPageTests.Page(pki);
        var page = new PoliciesViewModel(mine);
        await page.LoadAsync(CancellationToken.None);
        var editor = PolicyPageTests.Open(page, "Building B");
        var stored = pki.Policies.All.Single(p => p.Name == "Building B");
        Assert.Null((await pki.InvokeAsync<PolicySaveReply>(PolicyMethods.SavePolicy, stored with { Vlan = 31 })).Error); // another user

        editor.Vlan = "32";
        Assert.False(await editor.SaveAsync());

        Assert.Equal((editor.Title, PolicyTexts.ChangedElsewhere), Assert.Single(mine.Messages));
        Assert.Equal(31, pki.Policies.All.Single(p => p.Name == "Building B").Vlan); // nothing overwritten
    }

    [Fact]
    public async Task A_save_locks_the_form_and_its_delete()
    {
        await using var pki = await PolicyPageTests.StartAsync();
        var page = new PoliciesViewModel(PolicyPageTests.Page(pki));
        await page.LoadAsync(CancellationToken.None);
        var editor = PolicyPageTests.Open(page, "Storage room");
        Assert.True(editor.IsEditable);
        Assert.True(editor.DeleteCommand.CanExecute(null));

        editor.IsBusy = true;

        Assert.False(editor.IsEditable);
        Assert.False(editor.DeleteCommand.CanExecute(null));
        Assert.False(page.DeleteOpenCommand.CanExecute(null));
        Assert.True(page.CanDelete); // still shown, it waits
    }

    [Fact]
    public async Task An_issuing_policy_whose_ca_cannot_issue_has_its_dot_also_when_no_ca_can_issue()
    {
        await using var pki = await PolicyPageTests.StartAsync();
        var page = new PoliciesViewModel(PolicyPageTests.Page(pki));
        await page.LoadAsync(CancellationToken.None);
        Assert.Null(page.Rows.Single(r => r.Name == "HTTPS").Problem);

        page.Apply(page.List with { IssuingCas = [] }); // the OADM CA key cannot be read: no CA can issue

        Assert.Equal(PolicyTexts.CannotIssue, page.Rows.Single(r => r.Name == "HTTPS").Problem);
    }
}
