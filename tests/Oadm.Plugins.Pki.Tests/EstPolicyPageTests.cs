using Avalonia.Controls;
using Avalonia.VisualTree;

using Oadm.Plugins.Est.Serving;
using Oadm.Plugins.Pki.Client;
using Oadm.Plugins.Pki.Policies;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// The EST kinds on the PKI page (docs/spec/pki.md "Policies", stage 6): the filter EST, the rows of the converted defaults,
/// the editors of Enrollment (profiles: all, chosen ones, or no enrollment; the device choice) and Sign-in CA (CA, for new
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

    [Fact]
    public async Task The_est_filter_shows_enrollment_and_sign_in_ca_with_their_summaries()
    {
        await using var pki = await StartAsync();
        var card = new PoliciesViewModel(Page(pki));
        await card.LoadAsync(CancellationToken.None);

        Assert.Equal(["All", "Issuing", "Certificates", "802.1X", "EST"], card.Filters.Select(f => f.Label));
        card.SelectFilterCommand.Execute(PolicyGroups.Est);
        Assert.Equal([("EST server", "EST server", "The EST server uses a certificate from OADM Root CA SERVER01, valid 90 days, with an ECC key, renewed 30 days before it ends.", "-"),
            ("All devices", "Enrollment", "These cameras may get certificates from the EST server for every profile.", "All devices"), ("Axis device ID", "EST sign-in CA", "Cameras sign in to the EST server with their Axis device ID for new certificates and renewals.", "-"),
            ("OADM CA", "EST sign-in CA", "Cameras with a certificate from OADM Root CA SERVER01 may sign in to renew it.", "-")], card.Rows.Select(r => (r.Name, r.Kind, r.Summary, r.Devices)));
        Assert.Equal(["https", "dot1x"], card.List.EstProfiles);
        Assert.Equal(["Axis device ID", "OADM Root CA SERVER01"], card.List.SignInCas.Select(c => c.Name));
    }

    [Fact]
    public async Task Enrollment_chooses_profiles_and_devices_with_errors_below_the_fields()
    {
        await using var pki = await StartAsync();
        var card = new PoliciesViewModel(Page(pki));
        await card.LoadAsync(CancellationToken.None);
        var editor = card.NewEditor(null);
        editor.Kind = editor.KindChoices.Single(k => k.Value == PolicyKinds.Enrollment);

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

        // All devices overlaps the default "All devices": below the Devices choice.
        editor.Scope = PolicyEditorViewModel.DeviceScopes[0];
        Assert.False(await editor.SaveAsync());
        Assert.Equal(PolicyTexts.OverlapAll("PTZ", PolicyLimits.AllDevicesName), editor.ErrorOf(nameof(PolicyEditorViewModel.Scope)));

        // The default for chosen devices only, then the new one saves.
        var all = card.List.Rows.Single(r => r.Policy.Kind == PolicyKinds.Enrollment).Policy;
        Assert.Null((await card.SaveAsync(all with { Devices = new PolicyDevices { Serials = ["ACCC8E000002"] } })).Error);
        editor.Scope = PolicyEditorViewModel.DeviceScopes[1];
        editor.Picker.Tags.Single(t => t.Name == "PTZ").IsChecked = true;
        Assert.True(await editor.SaveAsync());
        var saved = pki.Policies.PoliciesOf(PolicyKinds.Enrollment).Single(p => p.Name == "PTZ");
        Assert.Equal((PolicyProfiles.Chosen, "dot1x", "PTZ"), (saved.ProfileScope, saved.Profiles!.Single(), saved.Devices!.Tags.Single()));
        Assert.Equal("These cameras may get certificates from the EST server for the profile dot1x.", card.List.Rows.Single(r => r.Policy.Id == saved.Id).Summary);

        // An existing policy shows its profiles, also one the EST server no longer has (so it can be unchecked).
        var stale = card.NewEditor(saved with { Profiles = ["dot1x", "web"] });
        Assert.Equal([("https", false), ("dot1x", true), ("web", true)], stale.ProfileChoices.Select(p => (p.Name, p.IsChecked)));
        Assert.False(await stale.SaveAsync());
        Assert.Equal(PolicyTexts.UnknownProfile("web"), stale.ProfilesError);
    }

    [Fact]
    public async Task Sign_in_ca_has_no_devices_and_the_same_ca_twice_is_refused_below_the_ca()
    {
        await using var pki = await StartAsync();
        var card = new PoliciesViewModel(Page(pki));
        await card.LoadAsync(CancellationToken.None);
        var editor = card.NewEditor(null);
        editor.Kind = editor.KindChoices.Single(k => k.Value == PolicyKinds.SignInCa);

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

        // Edit the default for renewals: the Axis device ID then renews only.
        var axis = card.NewEditor(card.List.Rows.Single(r => r.Policy.Kind == PolicyKinds.SignInCa && r.Policy.Name == "Axis device ID").Policy);
        Assert.Equal(("Axis device ID", "New certificates"), (axis.SignInCa!.Label, axis.SignInFor.Label));
        axis.SignInFor = PolicyEditorViewModel.SignInForChoices[1];
        Assert.True(await axis.SaveAsync());
        Assert.Equal(PolicySignIn.Renewal, pki.Policies.PoliciesOf(PolicyKinds.SignInCa).Single(p => p.Name == "Axis device ID").SignInFor);
    }
}

/// <summary>The editors of the EST kinds, rendered headless (screenshots into OADM_SCREENSHOT_DIR).</summary>
[Collection(HeadlessSessions.Name)]
public sealed class EstPolicyHeadlessTests
{
    [Fact]
    public async Task The_enrollment_and_sign_in_ca_editors_render()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        await using var pki = await EstPolicyPageTests.StartAsync();
        var ctx = new PluginPageContext(pki.Plugin, pki.Hub) { DeviceList = [new FakeDevice { Serial = "ACCC8E000001", Address = "10.0.0.48", Tags = ["PTZ"] }, new FakeDevice { Serial = "ACCC8E000002", Address = "10.0.0.49" }] };
        var checks = await HeadlessEntry.Session.Dispatch(async () =>
        {
            var card = new PoliciesViewModel(ctx);
            await card.LoadAsync(CancellationToken.None);

            var enrollment = card.NewEditor(null);
            enrollment.Kind = enrollment.KindChoices.Single(k => k.Value == PolicyKinds.Enrollment);
            enrollment.Name = "PTZ";
            enrollment.Scope = PolicyEditorViewModel.DeviceScopes[1];
            enrollment.ProfileScope = PolicyEditorViewModel.ProfileScopeChoices[1];
            enrollment.ProfileChoices[0].IsChecked = true;
            enrollment.ProfileChoices[0].IsChecked = false; // the error below the profiles
            var enrollmentWindow = PolicyFormView.CreateWindow(enrollment);
            Oadm.Client.App.ApplyCrispText(enrollmentWindow);
            enrollmentWindow.Show();
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(enrollmentWindow, outDir, "pki-policy-enrollment.png");
            var enrollmentTexts = Texts(enrollmentWindow);
            var enrollmentPicker = enrollmentWindow.IsPickerVisible;
            enrollmentWindow.Close();

            var signIn = card.NewEditor(card.List.Rows.Single(r => r.Policy.Kind == PolicyKinds.SignInCa && r.Policy.Name == "Axis device ID").Policy);
            var signInWindow = PolicyFormView.CreateWindow(signIn);
            Oadm.Client.App.ApplyCrispText(signInWindow);
            signInWindow.Show();
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(signInWindow, outDir, "pki-policy-sign-in-ca.png");
            var signInTexts = Texts(signInWindow);
            var signInPicker = signInWindow.IsPickerVisible;
            signInWindow.Close();
            return (enrollmentTexts, enrollmentPicker, signInTexts, signInPicker);
        }, CancellationToken.None);

        Assert.True(checks.enrollmentPicker);
        Assert.Contains("Profiles", checks.enrollmentTexts);
        Assert.Contains("https", checks.enrollmentTexts);
        Assert.Contains("dot1x", checks.enrollmentTexts);
        Assert.Contains(PolicyTexts.ChooseProfiles, checks.enrollmentTexts);
        Assert.Contains(PolicyKinds.Description(PolicyKinds.Enrollment), checks.enrollmentTexts);
        Assert.False(checks.signInPicker);
        Assert.Contains("Edit Axis device ID", checks.signInTexts);
        Assert.Contains(PolicyEditorViewModel.SignInNewHint, checks.signInTexts);
        Assert.Contains(PolicyKinds.Description(PolicyKinds.SignInCa), checks.signInTexts);
        Assert.DoesNotContain("Sites", checks.signInTexts);
    }

    private static List<string?> Texts(Window window) =>
    [
        .. window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text),
        .. window.GetVisualDescendants().OfType<CheckBox>().Where(c => c.IsEffectivelyVisible).Select(c => c.Content as string),
    ];
}
