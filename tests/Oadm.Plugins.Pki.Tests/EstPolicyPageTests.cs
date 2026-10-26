using Avalonia.Controls;
using Avalonia.VisualTree;

using Oadm.Plugins.Est.Serving;
using Oadm.Plugins.Pki.Client;
using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// The EST policies on the PKI page (docs/spec/pki.md "Policies"): the EST enrollment group with the converted defaults,
/// the forms of Enrollment (profiles: all, chosen ones, or no enrollment; the device choice) and Sign-in CA (CA, for new
/// certificates or renewals; no devices) with their errors below the fields.
/// </summary>
public sealed class EstPolicyPageTests
{
    /// <summary>A PKI with the EST policies of a new server and the EST server's two profiles.</summary>
    internal static async Task<PkiHarness> StartAsync()
    {
        var pki = await PolicyPageTests.StartAsync();
        var est = new FakeSettings();
        pki.Policies.ProfileNames = ct => EstPolicies.ProfileNamesAsync(est, ct); // the ready-made profiles https and dot1x
        Assert.True(await EstPolicies.ConvertRulesAsync(est, pki.Policies, CancellationToken.None));
        return pki;
    }

    private static PluginPageContext Page(PkiHarness pki) =>
        new(pki.Plugin, pki.Hub) { DeviceList = [new FakeDevice { Serial = "ACCC8E000001", Address = "10.0.0.48", Tags = ["PTZ"] }, new FakeDevice { Serial = "ACCC8E000002", Address = "10.0.0.49" }] };

    private static PolicyEditorViewModel OpenSignIn(PoliciesViewModel page, string name)
    {
        page.SelectedRow = page.Rows.Single(r => r.Name == name && r.Source.Kind == PolicyKinds.SignInCa);
        return page.Editor!;
    }

    [Fact]
    public async Task The_est_group_lists_enrollment_with_its_built_in_policy_first_and_the_sign_in_cas()
    {
        await using var pki = await StartAsync();
        var page = new PoliciesViewModel(Page(pki));
        await page.LoadAsync(CancellationToken.None);

        Assert.Equal(
            [
                ("All devices", "Enrollment", "These cameras may get certificates from the EST server for every profile.", true),
                ("Axis device ID", "EST sign-in CA", "Cameras sign in to the EST server with their Axis device ID for new certificates and renewals.", false),
                ("OADM Root CA SERVER01", "EST sign-in CA", "Cameras with a certificate from OADM Root CA SERVER01 may sign in to renew it.", false),
            ],
            page.Rows.Where(r => r.Group == PolicyTableGroups.Enrollment).Select(r => (r.Name, r.Type, r.Summary, r.IsSystem)));
        Assert.Contains(page.Rows, r => r.Name == "EST server" && r.Group == PolicyTableGroups.Servers && r.IsSystem);
        Assert.Equal(["https", "dot1x"], page.List.EstProfiles);
        Assert.Equal(["Axis device ID", "OADM Root CA SERVER01"], page.List.SignInCas.Select(c => c.Name));
    }

    [Fact]
    public async Task Enrollment_chooses_profiles_and_devices_with_errors_below_the_fields()
    {
        await using var pki = await StartAsync();
        var ctx = Page(pki);
        var page = new PoliciesViewModel(ctx);
        await page.LoadAsync(CancellationToken.None);
        var editor = page.NewEditor(PolicyKinds.Enrollment); // Create, then Enrollment
        Assert.Equal([PolicyKinds.Enrollment], editor.KindChoices.Select(k => k.Value));

        Assert.True(editor.IsEnrollment);
        Assert.True(editor.HasDevices);
        Assert.True(editor.OffersAllDevices);
        Assert.Equal(["All profiles", "Chosen profiles", "No enrollment"], PolicyEditorViewModel.ProfileScopeChoices.Select(c => c.Label));
        Assert.Equal(["https", "dot1x"], editor.ProfileChoices.Select(p => p.Name));
        Assert.False(editor.IsChosenProfiles);

        // Chosen profiles without one: the error below the profiles, Save off.
        editor.Name = "PTZ";
        editor.ProfileScope = PolicyEditorViewModel.ProfileScopeChoices[1];
        Assert.True(editor.IsChosenProfiles);
        editor.ProfileChoices[0].IsChecked = true;
        editor.ProfileChoices[0].IsChecked = false;
        Assert.Equal(PolicyTexts.ChooseProfiles, editor.ProfilesError);
        Assert.False(editor.CanSave);
        editor.ProfileChoices[1].IsChecked = true;
        Assert.Null(editor.ProfilesError);

        // All devices overlaps the default "All devices": below the Devices field.
        editor.Scope = PolicyEditorViewModel.DeviceScopes[0];
        Assert.False(await editor.SaveAsync());
        Assert.Equal(PolicyTexts.OverlapAll("PTZ", PolicyLimits.AllDevicesName), editor.ErrorOf(nameof(PolicyEditorViewModel.Scope)));

        // The default for chosen devices only, then the new one saves.
        var all = page.List.Rows.Single(r => r.Policy.Kind == PolicyKinds.Enrollment).Policy;
        Assert.Null((await pki.InvokeAsync<PolicySaveReply>(PolicyMethods.SavePolicy, all with { Devices = new PolicyDevices { Serials = ["ACCC8E000002"] } })).Error);
        editor.Scope = PolicyEditorViewModel.DeviceScopes[1];
        var picker = editor.NewPicker();
        picker.Tags.Single(t => t.Name == "PTZ").IsChecked = true;
        editor.UsePicker(picker);
        Assert.True(await editor.SaveAsync());
        var saved = pki.Policies.PoliciesOf(PolicyKinds.Enrollment).Single(p => p.Name == "PTZ");
        Assert.Equal((PolicyProfiles.Chosen, "dot1x", "PTZ"), (saved.ProfileScope, saved.Profiles!.Single(), saved.Devices!.Tags.Single()));
        Assert.Equal("These cameras may get certificates from the EST server for the profile dot1x.", page.List.Rows.Single(r => r.Policy.Id == saved.Id).Summary);

        // A policy shows its profiles, also one the EST server no longer has (so it can be unchecked).
        var stale = new PolicyEditorViewModel(ctx, page, saved with { Profiles = ["dot1x", "web"] }, page.List, ctx.Devices);
        Assert.Equal([("https", false), ("dot1x", true), ("web", true)], stale.ProfileChoices.Select(p => (p.Name, p.IsChecked)));
        Assert.False(await stale.SaveAsync());
        Assert.Equal(PolicyTexts.UnknownProfile("web"), stale.ProfilesError);
    }

    [Fact]
    public async Task Sign_in_ca_has_no_devices_and_the_same_ca_twice_is_refused_below_the_ca()
    {
        await using var pki = await StartAsync();
        var page = new PoliciesViewModel(Page(pki));
        await page.LoadAsync(CancellationToken.None);
        var editor = page.NewEditor(PolicyKinds.SignInCa);

        Assert.True(editor.IsSignInCa);
        Assert.False(editor.HasDevices);
        Assert.Equal(["Axis device ID", "OADM Root CA SERVER01"], editor.SignInCaChoices.Select(c => c.Label));
        Assert.Equal(["New certificates", "Renewals"], PolicyEditorViewModel.SignInForChoices.Select(c => c.Label));
        Assert.Equal(PolicyEditorViewModel.SignInNewHint, editor.SignInForHint);
        editor.SignInFor = PolicyEditorViewModel.SignInForChoices[1];
        Assert.Null(editor.SignInForHint);

        editor.Name = "Again";
        Assert.True(editor.CanSave);
        Assert.False(await editor.SaveAsync());
        Assert.Equal(PolicyTexts.SignInCaTaken, editor.ErrorOf(nameof(PolicyEditorViewModel.SignInCa)));

        // The default for renewals: the Axis device ID then renews only.
        var axis = OpenSignIn(page, "Axis device ID");
        Assert.Equal(("Axis device ID", "New certificates"), (axis.SignInCa!.Label, axis.SignInFor.Label));
        axis.SignInFor = PolicyEditorViewModel.SignInForChoices[1];
        Assert.True(await axis.SaveAsync());
        Assert.Equal(PolicySignIn.Renewal, pki.Policies.PoliciesOf(PolicyKinds.SignInCa).Single(p => p.Name == "Axis device ID").SignInFor);
    }
}

/// <summary>The forms of the EST policies in the side panel, rendered headless (screenshots into OADM_SCREENSHOT_DIR).</summary>
[Collection(HeadlessSessions.Name)]
public sealed class EstPolicyHeadlessTests
{
    [Fact]
    public async Task The_enrollment_and_sign_in_ca_forms_render_in_the_panel()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        await using var pki = await EstPolicyPageTests.StartAsync();
        var ctx = new PluginPageContext(pki.Plugin, pki.Hub) { DeviceList = [new FakeDevice { Serial = "ACCC8E000001", Address = "10.0.0.48", Tags = ["PTZ"] }, new FakeDevice { Serial = "ACCC8E000002", Address = "10.0.0.49" }] };
        var checks = await HeadlessEntry.Session.Dispatch(async () =>
        {
            var view = new PkiView();
            using var vm = new PkiViewModel(ctx, new FakeClientTrust());
            var window = HeadlessPageTests.Host(view, 1700, 1100);
            window.Show();
            view.DataContext = vm;
            await vm.LoadAsync();
            await vm.Policies.LoadAsync(CancellationToken.None);
            vm.ShowTab(PkiViewModel.PoliciesTabId);

            // Create, then Enrollment: chosen profiles without one, the error below them.
            await vm.Policies.CreateCommand.ExecuteAsync(null);
            vm.Policies.SelectedKind = vm.Policies.KindChoices.Single(k => k.Kind == PolicyKinds.Enrollment);
            vm.Policies.NextCommand.Execute(null);
            var enrollment = vm.Policies.Editor!;
            enrollment.Name = "PTZ";
            enrollment.ProfileScope = PolicyEditorViewModel.ProfileScopeChoices[1];
            enrollment.ProfileChoices[0].IsChecked = true;
            enrollment.ProfileChoices[0].IsChecked = false;
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(window, outDir, "pki-policy-enrollment.png");
            var enrollmentTexts = Texts(window);

            vm.Policies.Editor = null; // leave without the question
            vm.Policies.SelectedRow = vm.Policies.Rows.Single(r => r.Name == "Axis device ID" && r.Source.Kind == PolicyKinds.SignInCa);
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(window, outDir, "pki-policy-sign-in-ca.png");
            var signInTexts = Texts(window);
            window.Close();
            return (enrollmentTexts, signInTexts);
        }, CancellationToken.None);

        Assert.Contains("New Enrollment policy", checks.enrollmentTexts);
        Assert.Contains("Profiles", checks.enrollmentTexts);
        Assert.Contains("https", checks.enrollmentTexts);
        Assert.Contains("dot1x", checks.enrollmentTexts);
        Assert.Contains(PolicyTexts.ChooseProfiles, checks.enrollmentTexts);
        Assert.Contains("Choose devices", checks.enrollmentTexts);
        Assert.Contains(PolicyKinds.Description(PolicyKinds.Enrollment), checks.enrollmentTexts);
        Assert.Contains(PolicyEditorViewModel.SignInNewHint, checks.signInTexts);
        Assert.Contains(PolicyKinds.Description(PolicyKinds.SignInCa), checks.signInTexts);
        Assert.DoesNotContain("Choose devices", checks.signInTexts);
    }

    private static List<string?> Texts(Window window) =>
    [
        .. window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text),
        .. window.GetVisualDescendants().OfType<CheckBox>().Where(c => c.IsEffectivelyVisible).Select(c => c.Content as string),
    ];
}
