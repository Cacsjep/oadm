using Avalonia.Controls;
using Avalonia.VisualTree;

using Oadm.Plugins.Pki.Client;
using Oadm.Plugins.Pki.Policies;
using Oadm.Plugins.Radius.Server;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// The Policies card and the policy editor of the PKI page (docs/spec/pki.md "The PKI page", "Policies") against the
/// in-process plugin: the rows and the filter, read only without the permission, none for a group user; the editor's kinds,
/// fields and errors below them, the device picker only for kinds with devices, the overlap below the device choice, the
/// revoke question with the count.
/// </summary>
public sealed class PolicyPageTests
{
    private static readonly FakeDevice Camera = new FakeDevice { Serial = "ACCC8E000001", Address = "10.0.0.48", Tags = ["PTZ"] };
    private static readonly FakeDevice Other = new FakeDevice { Serial = "ACCC8E000002", Address = "10.0.0.49" };

    /// <summary>A PKI with the 802.1X defaults (the 802.1X server's conversion of a new server), one VLAN, one Block.</summary>
    internal static async Task<PkiHarness> StartAsync(params IDeviceInfo[] devices)
    {
        var pki = await PkiHarness.StartAsync(devices: devices.Length == 0 ? [Camera, Other] : devices);
        await pki.Policies.ConvertOnceAsync(Radius.RadiusPlugin.PluginId, ct => RadiusPolicies.ConvertAsync(new FakeSettings(), pki.Service.Authorities(), ct), CancellationToken.None);
        await pki.InvokeAsync<PolicySaveReply>(PolicyMethods.SavePolicy, new PkiPolicy { Name = "Building B", Kind = PolicyKinds.Vlan, Vlan = 30, Devices = new PolicyDevices { ThisServer = true, ExceptSerials = ["ACCC8E000002"] } });
        await pki.InvokeAsync<PolicySaveReply>(PolicyMethods.SavePolicy, new PkiPolicy { Name = "Storage room", Kind = PolicyKinds.Block, Devices = new PolicyDevices { Serials = ["ACCC8E000002"] } });
        return pki;
    }

    private static PluginPageContext Page(PkiHarness pki) => new(pki.Plugin, pki.Hub) { DeviceList = [Camera, Other] };

    [Fact]
    public async Task The_card_lists_every_policy_in_age_order_and_the_filter_shows_one_group()
    {
        await using var pki = await StartAsync();
        var card = new PoliciesViewModel(Page(pki));
        await card.LoadAsync(CancellationToken.None);

        Assert.Equal(["All", "Certificates", "802.1X", "Revoke"], card.Filters.Select(f => f.Label)); // only groups with kinds now
        Assert.Equal(["All devices", "802.1X server", "OADM CA", "Axis device ID", "Building B", "Storage room"], card.Rows.Select(r => r.Name));
        Assert.Equal(["Expiry warning", "802.1X server", "Allow CA", "Allow CA", "VLAN", "Block"], card.Rows.Select(r => r.Kind));
        Assert.Equal("30 days", card.Rows[0].Summary);
        Assert.Equal("TLS 1.2 or later, 1,020 bytes, switch decides reauthentication", card.Rows[1].Summary);
        Assert.Equal("OADM Root CA SERVER01, only devices OADM manages", card.Rows[2].Summary); // the CA's name in PKI
        Assert.Equal("VLAN 30", card.Rows[4].Summary);
        Assert.Equal(["All devices", "-", "-", "-", "This server, except 1", "1 device"], card.Rows.Select(r => r.Devices));
        Assert.True(card.MayManage);
        Assert.True(card.AddCommand.CanExecute(null));

        card.SelectFilterCommand.Execute(PolicyGroups.Dot1x);
        Assert.Equal(["802.1X server", "OADM CA", "Axis device ID", "Building B", "Storage room"], card.Rows.Select(r => r.Name));
        Assert.True(card.Filters.Single(f => f.Group == PolicyGroups.Dot1x).IsSelected);
        card.SelectFilterCommand.Execute(PolicyGroups.Revoke);
        Assert.Empty(card.Rows);
        Assert.Equal("No policies of this group.", card.EmptyText);
        card.SelectFilterCommand.Execute(PolicyGroups.Certificates);
        Assert.Equal(["All devices"], card.Rows.Select(r => r.Name));
    }

    [Fact]
    public async Task Without_the_permission_the_card_is_read_only_and_a_group_user_reads_none()
    {
        await using var pki = await StartAsync();
        var ctx = Page(pki);
        ctx.Manage = false;
        var card = new PoliciesViewModel(ctx);
        await card.LoadAsync(CancellationToken.None);
        Assert.NotEmpty(card.Rows);
        Assert.False(card.MayManage);
        Assert.False(card.AddCommand.CanExecute(null));

        var calls = new List<string>();
        var group = Page(pki);
        group.HasAllDevices = false;
        group.Intercept = (method, payload) =>
        {
            calls.Add(method);
            return pki.Plugin.InvokeAsync(method, payload, CancellationToken.None);
        };
        var hidden = new PoliciesViewModel(group);
        await hidden.LoadAsync(CancellationToken.None);
        Assert.False(hidden.IsShown);
        Assert.Empty(hidden.Rows);
        Assert.DoesNotContain(PolicyMethods.GetPolicies, calls); // server-wide: nothing read
    }

    [Fact]
    public async Task A_new_vlan_policy_shows_errors_below_its_fields_and_the_overlap_below_the_device_choice()
    {
        await using var pki = await StartAsync();
        var card = new PoliciesViewModel(Page(pki));
        await card.LoadAsync(CancellationToken.None);
        var editor = card.NewEditor(null);

        Assert.True(editor.IsNew);
        Assert.Equal(PolicyKinds.Vlan, editor.Kind.Value);
        Assert.DoesNotContain(editor.KindChoices, k => k.Value == PolicyKinds.Dot1xServer); // one only: there is one
        Assert.True(editor.HasDevices);
        Assert.Null(editor.ErrorOf(nameof(PolicyEditorViewModel.Name))); // an untouched form shows no error
        Assert.False(editor.CanSave);

        editor.Name = "building b";
        editor.Vlan = "4095";
        Assert.Equal(PolicyTexts.NameTaken(PolicyKinds.Vlan), editor.ErrorOf(nameof(PolicyEditorViewModel.Name)));
        Assert.Equal(PolicyTexts.VlanRange, editor.ErrorOf(nameof(PolicyEditorViewModel.Vlan)));
        editor.Name = "PTZ";
        editor.Vlan = "40";
        Assert.Equal(PolicyTexts.ChooseDevices, editor.SaveBlockedReason);

        editor.Picker.Tags.Single(t => t.Name == "PTZ").IsChecked = true;
        Assert.True(editor.CanSave);
        Assert.False(await editor.SaveAsync());
        Assert.Equal("10.0.0.48 is in PTZ and Building B.", editor.Picker.ErrorText); // below the device choice

        editor.Kind = editor.KindChoices.Single(k => k.Value == PolicyKinds.Block);
        Assert.True(await editor.SaveAsync()); // another kind may hold it
        Assert.Contains(card.Rows, r => r.Name == "PTZ" && r.Kind == "Block");
    }

    [Fact]
    public async Task All_devices_hides_the_device_picker_and_its_overlap_shows_below_the_devices_choice()
    {
        await using var pki = await StartAsync();
        var card = new PoliciesViewModel(Page(pki));
        await card.LoadAsync(CancellationToken.None);
        var editor = card.NewEditor(null);
        Assert.Equal([PolicyTexts.AllDevices, PolicyTexts.ChosenDevices], PolicyEditorViewModel.DeviceScopes.Select(c => c.Label));
        Assert.True(editor.OffersAllDevices);
        Assert.True(editor.ShowsPicker); // a new policy starts with the picker

        editor.Name = "Everyone";
        editor.Vlan = "99";
        editor.Scope = PolicyEditorViewModel.DeviceScopes[0];
        Assert.False(editor.ShowsPicker);
        Assert.True(editor.CanSave); // nothing to pick
        Assert.Equal(PolicyDevices.Everything, editor.ToPolicy().Devices);
        Assert.False(await editor.SaveAsync());
        Assert.Equal("Everyone includes every device, also those of Building B.", editor.ErrorOf(nameof(PolicyEditorViewModel.Scope)));

        editor.Kind = editor.KindChoices.Single(k => k.Value == PolicyKinds.Revoke);
        Assert.False(editor.OffersAllDevices); // Revoke: devices and sites only
        Assert.True(editor.ShowsPicker);

        var expiry = card.NewEditor(card.Rows.Single(r => r.Kind == "Expiry warning").Source);
        Assert.True(expiry.IsAllDevices); // the default "All devices"
        Assert.False(expiry.ShowsPicker);
    }

    [Fact]
    public async Task Allow_ca_has_no_device_choice_and_the_same_ca_twice_is_refused_below_the_ca()
    {
        await using var pki = await StartAsync();
        var card = new PoliciesViewModel(Page(pki));
        await card.LoadAsync(CancellationToken.None);
        var editor = card.NewEditor(null);
        editor.Kind = editor.KindChoices.Single(k => k.Value == PolicyKinds.AllowCa);

        Assert.False(editor.HasDevices);
        Assert.Equal(["OADM Root CA SERVER01", "Axis device ID"], editor.CaChoices.Select(c => c.Label));
        Assert.True(editor.OnlyManaged);
        Assert.Equal(editor.CaChoices[0], editor.Ca); // both CAs have an Allow CA policy already: the first one
        editor.Name = "Again";
        Assert.True(editor.CanSave); // no devices needed
        Assert.False(await editor.SaveAsync());
        Assert.Equal(PolicyTexts.CaTaken, editor.ErrorOf(nameof(PolicyEditorViewModel.Ca)));

        // An existing policy keeps its kind.
        var existing = card.NewEditor(card.Rows.Single(r => r.Name == "OADM CA").Source);
        Assert.False(existing.IsNew);
        Assert.Equal([PolicyKinds.AllowCa], existing.KindChoices.Select(k => k.Value));
        Assert.Equal("Edit OADM CA", existing.Title);
    }

    [Fact]
    public async Task The_server_policy_lists_the_old_suites_only_below_tls_1_2_and_checks_its_fields()
    {
        await using var pki = await StartAsync();
        var card = new PoliciesViewModel(Page(pki));
        await card.LoadAsync(CancellationToken.None);
        var editor = card.NewEditor(card.Rows.Single(r => r.Kind == "802.1X server").Source);

        Assert.True(editor.IsServer);
        Assert.False(editor.HasDevices);
        Assert.Null(editor.TlsHint);
        Assert.Equal("1,020", editor.LargestPacket);
        Assert.Equal(PolicyTexts.SwitchDecides, editor.Reauth.Label);
        Assert.All(editor.CipherSuites.Where(s => s.IsLegacy), s => Assert.False(s.IsListed));

        editor.MinimumTls = PolicyEditorViewModel.TlsVersions[1]; // TLS 1.1
        Assert.Equal(PolicyEditorViewModel.OldTlsHint, editor.TlsHint);
        Assert.All(editor.CipherSuites.Where(s => s.IsLegacy), s => Assert.True(s.IsListed && s.IsChecked)); // checked when lowered
        Assert.Equal(5, editor.CipherSuites.Count(s => s.IsListed && s.HasKeyHint)); // the ECDHE_RSA suites and the server's ECDSA key

        editor.MinimumTls = PolicyEditorViewModel.TlsVersions[3]; // TLS 1.3
        foreach (var suite in editor.CipherSuites)
        {
            suite.IsChecked = false;
        }

        Assert.Equal(PolicyRules.CheckCipherSuite, editor.CipherSuitesError);
        editor.ResetCipherSuitesCommand.Execute(null);
        Assert.Null(editor.CipherSuitesError);
        editor.LargestPacket = "1500";
        Assert.Equal(PolicyRules.EapPacketRange, editor.ErrorOf(nameof(PolicyEditorViewModel.LargestPacket)));
        editor.LargestPacket = "1,200";
        editor.Reauth = PolicyEditorViewModel.ReauthChoices[1];
        editor.ReauthMinutes = "5";
        Assert.Equal(PolicyTexts.ReauthRange, editor.ErrorOf(nameof(PolicyEditorViewModel.ReauthMinutes)));
        editor.ReauthMinutes = "60";

        Assert.True(await editor.SaveAsync());
        var saved = pki.Policies.ServerPolicy!;
        Assert.Equal(("1.3", 1200, 60), (saved.MinimumTls, saved.LargestEapPacket!.Value, saved.ReauthMinutes!.Value));
    }

    [Fact]
    public async Task A_revoke_policy_says_so_below_the_device_choice_for_a_tag_and_asks_with_the_count()
    {
        await using var pki = await StartAsync();
        IDeviceCertificateAuthority authority = new Ca.PkiCertificateAuthority(pki.Service);
        await RevocationTests.SignAsync(authority, Camera.Id, Camera.Serial, DeviceCertificateUse.Client);
        var ctx = Page(pki);
        var card = new PoliciesViewModel(ctx);
        await card.LoadAsync(CancellationToken.None);
        var editor = card.NewEditor(null);
        editor.Kind = editor.KindChoices.Single(k => k.Value == PolicyKinds.Revoke);
        editor.Name = "Stolen";
        editor.Picker.Tags.Single(t => t.Name == "PTZ").IsChecked = true;
        Assert.Equal(PolicyTexts.RevokeNoTags, editor.Picker.ErrorText);
        editor.Picker.Tags.Single(t => t.Name == "PTZ").IsChecked = false;
        Assert.Null(editor.Picker.ErrorText);
        editor.Picker.Visible.Single(i => i.Id == Camera.Id).Chosen = true;

        string? question = null;
        editor.ConfirmRevoke = (_, message) =>
        {
            question = message;
            return Task.FromResult(true);
        };
        Assert.True(await editor.SaveAsync());

        Assert.Equal("Saving revokes 1 IEEE 802.1X certificate of these devices, and every new one while the policy exists. This cannot be undone.", question);
        Assert.Contains(card.Rows, r => r.Name == "Stolen" && r.Group == PolicyGroups.Revoke);
        Assert.NotEmpty(pki.Service.Issued.Revocations);
    }
}

/// <summary>The PKI page and the policy editor rendered offscreen at 1,700 and 1,000 px (OADM_SCREENSHOT_DIR writes PNGs).</summary>
[Collection(HeadlessSessions.Name)]
public sealed class PolicyHeadlessTests
{
    [Theory]
    [InlineData(1700, 2)]
    [InlineData(1000, 1)]
    public async Task The_page_shows_cas_and_policies_side_by_side_or_stacked_and_the_certificates_below(int width, int columns)
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        await using var pki = await PolicyPageTests.StartAsync();
        await pki.InvokeAsync<PkiReply>(PkiMethods.Generate, new GenerateRequest("OADM Root CA SERVER01", null, 10, true)); // a previous CA
        IDeviceCertificateAuthority authority = new Ca.PkiCertificateAuthority(pki.Service);
        for (var i = 0; i < 6; i++)
        {
            await RevocationTests.SignAsync(authority, Guid.NewGuid(), "ACCC8E00010" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), i % 2 == 0 ? DeviceCertificateUse.Client : DeviceCertificateUse.Server);
        }

        var ctx = new PluginPageContext(pki.Plugin, pki.Hub);
        var checks = await HeadlessEntry.Session.Dispatch(async () =>
        {
            var view = new PkiView();
            using var vm = new PkiViewModel(ctx, new FakeClientTrust());
            var window = HeadlessPageTests.Host(view, width, 1500);
            window.Show();
            view.DataContext = vm;
            await vm.LoadAsync();
            await vm.Policies.LoadAsync(CancellationToken.None);
            await vm.Certificates.LoadAsync();
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(window, outDir, $"pki-page-{width}.png");
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            var tabs = window.GetVisualDescendants().OfType<PageTab>().Count(t => t.IsEffectivelyVisible);
            var primaries = window.GetVisualDescendants().OfType<Button>().Count(b => b.IsEffectivelyVisible && b.Classes.Contains("primary"));
            var segments = window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Classes.Contains("segment")).Select(b => b.Content as string).ToList();
            var count = view.CardColumnCount;
            window.Close();
            return (texts, tabs, primaries, segments, count);
        }, CancellationToken.None);

        Assert.Equal(columns, checks.count);
        Assert.Equal(0, checks.tabs); // one page, no tabs
        foreach (var title in new[] { "Certificate authorities", "Policies", "Certificates", "Add policy", "Generate new CA", "Import CA", "802.1X server", "Building B", "Storage room" })
        {
            Assert.Contains(title, checks.texts);
        }

        Assert.DoesNotContain("Previous certificate authorities", checks.texts); // previous CAs once: rows of the list
        Assert.Contains("Previous OADM CA", checks.texts);
        Assert.DoesNotContain("Warn before expiry (days)", checks.texts); // the Expiry warning policy
        Assert.Equal(["All", "Certificates", "802.1X", "Revoke"], checks.segments);
        Assert.Equal(1, checks.primaries); // one accent button: the settings' Save
        Assert.All(checks.texts, t => Assert.False(t?.EndsWith("...", StringComparison.Ordinal) == true || t?.EndsWith('…') == true, t));
    }

    [Fact]
    public async Task The_editor_shows_the_device_picker_only_for_kinds_with_devices()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        await using var pki = await PolicyPageTests.StartAsync();
        var ctx = new PluginPageContext(pki.Plugin, pki.Hub) { DeviceList = [new FakeDevice { Serial = "ACCC8E000001", Address = "10.0.0.48", Tags = ["PTZ"] }, new FakeDevice { Serial = "ACCC8E000002", Address = "10.0.0.49" }] };
        var checks = await HeadlessEntry.Session.Dispatch(async () =>
        {
            var card = new PoliciesViewModel(ctx);
            await card.LoadAsync(CancellationToken.None);

            var vlan = card.NewEditor(card.Rows.Single(r => r.Name == "Building B").Source);
            var vlanWindow = PolicyFormView.CreateWindow(vlan);
            Oadm.Client.App.ApplyCrispText(vlanWindow);
            vlanWindow.Show();
            vlan.Vlan = "0";
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(vlanWindow, outDir, "pki-policy-vlan.png");
            var vlanTexts = Texts(vlanWindow);
            var vlanPicker = vlanWindow.IsPickerVisible;
            vlanWindow.Close();

            var allow = card.NewEditor(null);
            var allowWindow = PolicyFormView.CreateWindow(allow);
            Oadm.Client.App.ApplyCrispText(allowWindow);
            allowWindow.Show();
            allow.Kind = allow.KindChoices.Single(k => k.Value == PolicyKinds.AllowCa);
            allow.Name = "Contoso";
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(allowWindow, outDir, "pki-policy-allow-ca.png");
            var allowTexts = Texts(allowWindow);
            var allowPicker = allowWindow.IsPickerVisible;
            allowWindow.Close();

            var server = card.NewEditor(card.Rows.Single(r => r.Kind == "802.1X server").Source);
            var serverWindow = PolicyFormView.CreateWindow(server);
            Oadm.Client.App.ApplyCrispText(serverWindow);
            serverWindow.Show();
            server.MinimumTls = PolicyEditorViewModel.TlsVersions[1];
            server.Reauth = PolicyEditorViewModel.ReauthChoices[1];
            server.LargestPacket = "1500";
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(serverWindow, outDir, "pki-policy-server.png");
            var serverTexts = Texts(serverWindow);
            serverWindow.Close();

            var expiry = card.NewEditor(card.Rows.Single(r => r.Kind == "Expiry warning").Source);
            var expiryWindow = PolicyFormView.CreateWindow(expiry);
            Oadm.Client.App.ApplyCrispText(expiryWindow);
            expiryWindow.Show();
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(expiryWindow, outDir, "pki-policy-expiry.png");
            var expiryTexts = Texts(expiryWindow);
            var expiryPicker = expiryWindow.IsPickerVisible;
            expiryWindow.Close();

            var question = new MessageWindow { Heading = "Add policy", Message = "Saving revokes 7 IEEE 802.1X certificates of these devices, and every new one while the policy exists. This cannot be undone.", ConfirmText = "Revoke", CancelText = "Cancel", IsDanger = true };
            Oadm.Client.App.ApplyCrispText(question);
            question.Show();
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(question, outDir, "pki-policy-revoke-question.png");
            var red = question.ConfirmButton.Classes.Contains("danger");
            question.Close();
            return (vlanTexts, vlanPicker, allowTexts, allowPicker, serverTexts, expiryTexts, expiryPicker, red);
        }, CancellationToken.None);

        Assert.True(checks.vlanPicker);
        Assert.Contains("Edit Building B", checks.vlanTexts);
        Assert.Contains("VLAN ID", checks.vlanTexts);
        Assert.Contains("Sites", checks.vlanTexts);
        Assert.Contains(PolicyTexts.VlanRange, checks.vlanTexts); // below the VLAN ID
        Assert.False(checks.allowPicker);
        Assert.Contains("Only devices OADM manages", checks.allowTexts);
        Assert.Contains(PolicyKinds.Description(PolicyKinds.AllowCa), checks.allowTexts);
        Assert.DoesNotContain("Sites", checks.allowTexts);
        Assert.Contains(PolicyEditorViewModel.OldTlsHint, checks.serverTexts);
        Assert.Contains("Reset to default", checks.serverTexts);
        Assert.Contains(PolicyRules.EapPacketRange, checks.serverTexts);
        Assert.Contains("Minutes", checks.serverTexts);
        Assert.Contains("Days", checks.expiryTexts);
        Assert.Contains(PolicyTexts.AllDevices, checks.expiryTexts); // the Devices choice; no picker
        Assert.False(checks.expiryPicker);
        Assert.True(checks.red);
    }

    private static List<string?> Texts(Window window) =>
    [
        .. window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text),
        .. window.GetVisualDescendants().OfType<CheckBox>().Where(c => c.IsEffectivelyVisible).Select(c => c.Content as string),
    ];
}
