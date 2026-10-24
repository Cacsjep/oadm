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
    internal static readonly FakeDevice Camera = new FakeDevice { Serial = "ACCC8E000001", Address = "10.0.0.48", Tags = ["PTZ"] };
    internal static readonly FakeDevice Other = new FakeDevice { Serial = "ACCC8E000002", Address = "10.0.0.49" };

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
    public async Task System_policies_are_settings_in_place_and_user_policies_are_rules_of_their_card()
    {
        await using var pki = await StartAsync();
        var card = new PoliciesViewModel(Page(pki));
        await card.LoadAsync(CancellationToken.None);

        // Certificates for cameras: the system HTTPS, IEEE 802.1X and Expiry warning policies in place (decision 2026-10-25).
        Assert.Equal(
            [("HTTPS", PolicyKinds.HttpsCertificate), ("IEEE 802.1X", PolicyKinds.Dot1xCertificate), ("Expiry warning", PolicyKinds.ExpiryWarning), ("802.1X server", PolicyKinds.Dot1xServer), ("EST server", PolicyKinds.EstServer)],
            card.Settings.Select(s => (s.Heading, s.Kind.Value)));
        Assert.All(card.Settings, s => Assert.True(s.IsInPlace && !s.ShowsName && !s.ShowsPicker && !s.ShowsDeviceScope && !s.IsDirty && !s.CanSave && s.SaveStatus is null));
        Assert.Equal(("365", PolicyKeys.Ecc, "Enable HTTPS issues certificates from OADM Root CA SERVER01, valid 365 days, with an ECC key."),
            (card.Https!.ValidDays, card.Https.Key.Value, card.List.Rows.Single(r => r.Policy.Id == card.Https.PolicyId).Summary)); // the defaults of a new server
        Assert.Equal("30", card.Expiry!.Days);
        Assert.Null(card.Expiry.DaysHint); // All devices

        // The rules: one line each, the summary sentence, devices where the kind has them; System rows have no Delete.
        Assert.Empty(card.OtherIssuing.Rows);
        Assert.Empty(card.ExpiryRules.Rows);
        Assert.Equal(
            [("OADM CA", "The 802.1X server lets in devices OADM manages that have a certificate from OADM Root CA SERVER01."),
                ("Axis device ID", "The 802.1X server lets in devices OADM manages that have a certificate from Axis device ID.")],
            card.TrustedCas.Rows.Select(r => (r.Name, r.Summary)));
        Assert.Equal([("Building B", "This server, except 1", "The 802.1X server puts these devices in VLAN 30.")], card.Vlans.Rows.Select(r => (r.Name, r.Devices, r.Summary)));
        Assert.Equal([("Storage room", "1 device")], card.Blocked.Rows.Select(r => (r.Name, r.Devices)));
        Assert.All(card.Sections.SelectMany(s => s.Rows), r => Assert.Equal(!r.IsSystem, r.CanDelete));
        Assert.DoesNotContain(card.Sections.Where(s => !s.WithSystem).SelectMany(s => s.Rows), r => r.IsSystem); // in place, never twice
        Assert.Equal([false, true, false, true, true, true, false], card.Sections.Select(s => s.HasDevices));

        // Empty lists say in one line what to do first; every list adds its own kind.
        Assert.Equal("No other issuing policies. Add one to issue with other values, e.g. HTTPS 90 days.", card.OtherIssuing.EmptyText);
        Assert.Null(card.Vlans.EmptyText);
        Assert.Equal([PolicyKinds.HttpsCertificate, PolicyKinds.Dot1xCertificate], card.OtherIssuing.AddChoices.Select(c => c.Value));
        Assert.True(card.MayManage);
        Assert.All(card.Sections, s => Assert.True(s.AddCommand.CanExecute(null) && s.CanAddRule));
    }

    [Fact]
    public async Task Add_of_a_rule_list_opens_the_editor_with_its_kind_and_edit_and_delete_change_the_list()
    {
        await using var pki = await StartAsync();
        var ctx = Page(pki);
        var card = new PoliciesViewModel(ctx);
        var dialogs = new SavingDialogs();
        card.Dialogs = dialogs;
        await card.LoadAsync(CancellationToken.None);

        // Add: the kind of the list, no kind choice.
        dialogs.Fill = editor =>
        {
            editor.Name = "Lobby";
            editor.Vlan = "40";
            editor.Picker.Visible.Single(i => i.Id == Other.Id).Chosen = true; // the camera Building B leaves out
        };
        await card.Vlans.AddCommand.ExecuteAsync(null);
        var added = Assert.Single(dialogs.Shown);
        Assert.Equal((PolicyKinds.Vlan, 1, "Add VLAN policy", true), (added.Kind.Value, added.KindChoices.Count, added.Title, added.IsNew));
        Assert.Contains(card.Vlans.Rows, r => r.Name == "Lobby" && r.Devices == "1 device");

        // Several kinds: the menu's choice is the kind.
        dialogs.Fill = editor =>
        {
            editor.Name = "HTTPS 90 days";
            editor.ValidDays = "90";
        };
        await card.OtherIssuing.AddCommand.ExecuteAsync(PolicyKinds.HttpsCertificate);
        Assert.Equal(PolicyKinds.HttpsCertificate, dialogs.Shown[^1].Kind.Value);
        Assert.Equal(["HTTPS 90 days"], card.OtherIssuing.Rows.Select(r => r.Name));

        // Edit opens the same dialog with the policy; Delete asks, then the row goes.
        dialogs.Fill = editor => editor.Vlan = "41";
        await card.Vlans.EditRowCommand.ExecuteAsync(card.Vlans.Rows.Single(r => r.Name == "Lobby"));
        Assert.Equal("Edit Lobby", dialogs.Shown[^1].Title);
        Assert.Equal(41, pki.Policies.PoliciesOf(PolicyKinds.Vlan).Single(p => p.Name == "Lobby").Vlan);
        await card.Vlans.DeleteRowCommand.ExecuteAsync(card.Vlans.Rows.Single(r => r.Name == "Lobby"));
        Assert.Equal(("Delete policy", "Delete the policy Lobby?", "Delete"), ctx.Confirmations[^1]);
        Assert.DoesNotContain(card.Vlans.Rows, r => r.Name == "Lobby");
        Assert.Null(card.Error);
    }

    [Fact]
    public async Task A_setting_in_place_saves_only_with_changes_keeps_them_over_a_reload_and_says_saved()
    {
        await using var pki = await StartAsync();
        var card = new PoliciesViewModel(Page(pki));
        await card.LoadAsync(CancellationToken.None);
        var expiry = card.Expiry!;
        Assert.Equal(PolicyEditorViewModel.NoChangesText, expiry.SaveBlockedReason);

        // An error directly below the field, Save off; a valid value: Not saved left of Save.
        expiry.Days = "400";
        Assert.Equal(PolicyTexts.DaysRange, expiry.ErrorOf(nameof(PolicyEditorViewModel.Days)));
        Assert.False(expiry.CanSave);
        expiry.Days = "45";
        Assert.True(expiry.CanSave);
        Assert.Equal(PolicyEditorViewModel.NotSavedText, expiry.SaveStatus);
        Assert.False(expiry.IsSaveStatusOk);

        // A reload (another user's change) keeps the changes of a form not saved yet, and refreshes the others.
        var https = card.Https!;
        await card.LoadAsync(CancellationToken.None);
        Assert.Same(expiry, card.Expiry);
        Assert.NotSame(https, card.Https);

        // Save: the server stores it, the form shows the saved policy and says Saved; the devices stay as they were.
        await expiry.SaveInPlaceCommand.ExecuteAsync(null);
        var saved = pki.Policies.FirstOf(PolicyKinds.ExpiryWarning)!;
        Assert.Equal((45, true), (saved.Days, saved.Devices!.AllDevices));
        Assert.NotSame(expiry, card.Expiry);
        Assert.Equal(("45", PolicyEditorViewModel.SavedText, true, false), (card.Expiry!.Days, card.Expiry.SaveStatus, card.Expiry.IsSaveStatusOk, card.Expiry.CanSave));

        // A refusal of the server: below its field, nothing saved.
        var server = card.Dot1xServer!;
        server.Key = PolicyEditorViewModel.KeyChoices.Single(k => k.Value == PolicyKeys.Rsa);
        Assert.Equal(PolicyTexts.Dot1xServerNeedsEcc, server.ErrorOf(nameof(PolicyEditorViewModel.Key)));
        Assert.False(server.CanSave);
        server.Key = PolicyEditorViewModel.KeyChoices.Single(k => k.Value == PolicyKeys.Ecc);
        Assert.False(server.IsDirty); // back as it was
    }

    [Fact]
    public async Task A_system_expiry_warning_for_chosen_devices_names_them_and_keeps_them_on_save()
    {
        await using var pki = await StartAsync();
        var all = pki.Policies.FirstOf(PolicyKinds.ExpiryWarning)!;
        Assert.Null((await pki.InvokeAsync<PolicySaveReply>(PolicyMethods.SavePolicy, all with { Devices = new PolicyDevices { Tags = ["PTZ"] } })).Error);
        var card = new PoliciesViewModel(Page(pki));
        await card.LoadAsync(CancellationToken.None);

        Assert.Equal("For Tag PTZ", card.Expiry!.DaysHint);
        card.Expiry.Days = "60";
        Assert.True(await card.Expiry.SaveAsync());
        Assert.Equal((60, "PTZ"), (pki.Policies.FirstOf(PolicyKinds.ExpiryWarning)!.Days, pki.Policies.FirstOf(PolicyKinds.ExpiryWarning)!.Devices!.Tags.Single()));
    }

    [Fact]
    public async Task Check_a_device_says_which_policies_apply_and_follows_a_change()
    {
        await using var pki = await StartAsync();
        var ctx = Page(pki);
        var card = new PoliciesViewModel(ctx) { Dialogs = new SavingDialogs { Pick = Camera.Id } };
        await card.LoadAsync(CancellationToken.None);
        Assert.Equal(PoliciesViewModel.ChooseDeviceText, card.CheckButtonText);
        Assert.Empty(card.CheckLines); // nothing read before a choice

        await card.ChooseCheckDeviceCommand.ExecuteAsync(null);
        Assert.Equal("10.0.0.48 · P3265-V", card.CheckButtonText);
        Assert.Null(card.CheckError);
        Assert.Equal(
            [
                (PolicyCheckAreas.Https, "Enable HTTPS issues certificates from OADM Root CA SERVER01, valid 365 days, with an ECC key.", "By HTTPS", false),
                (PolicyCheckAreas.Dot1x, "Enable IEEE 802.1X issues certificates from OADM Root CA SERVER01, valid 365 days, with an ECC key. Cameras sign in with their MAC address.", "By IEEE 802.1X", false),
                (PolicyCheckAreas.Expiry, "Its certificates show a warning with 30 days or less left.", "By All devices", false),
                (PolicyCheckAreas.Network, "It passes the 802.1X server with a certificate from OADM Root CA SERVER01 or Axis device ID.", "By OADM CA and Axis device ID", false),
                (PolicyCheckAreas.Network, "It gets VLAN 30.", "By Building B", false),
                (PolicyCheckAreas.Est, PolicyCheckTexts.NoEnrollment, null, true),
            ],
            card.CheckLines.Select(l => (l.Area, l.Text, l.By, l.IsWarning)));

        // A change of the policies: the next read checks the device again.
        var vlan = pki.Policies.PoliciesOf(PolicyKinds.Vlan).Single();
        await pki.InvokeAsync<PolicySaveReply>(PolicyMethods.SavePolicy, vlan with { Vlan = 31 });
        await card.LoadAsync(CancellationToken.None);
        Assert.Contains(card.CheckLines, l => l.Text == "It gets VLAN 31.");
    }

    [Fact]
    public async Task Check_a_device_of_a_blocked_device_says_so_and_a_group_user_gets_nothing()
    {
        await using var pki = await StartAsync();
        var check = await pki.InvokeAsync<PolicyCheck>(PolicyMethods.CheckDevice, new PolicyCheckRequest(Other.Id));
        var network = Assert.Single(check.Lines, l => l.Area == PolicyCheckAreas.Network);
        Assert.Equal((PolicyCheckTexts.Blocked(false), "Storage room", true), (network.Text, network.Policies.Single(), network.IsWarning));
        Assert.Equal(DeviceMessages.Removed, (await pki.InvokeAsync<PolicyCheck>(PolicyMethods.CheckDevice, new PolicyCheckRequest(Guid.NewGuid()))).Error);

        // A user limited to a device group: the page reads nothing, the server answers nothing (server-wide).
        var calls = new List<string>();
        var group = Page(pki);
        group.HasAllDevices = false;
        group.Intercept = (method, payload) =>
        {
            calls.Add(method);
            return pki.Plugin.InvokeAsync(method, payload, CancellationToken.None);
        };
        var card = new PoliciesViewModel(group) { Dialogs = new SavingDialogs { Pick = Camera.Id } };
        await card.ChooseCheckDeviceCommand.ExecuteAsync(null);
        Assert.Empty(card.CheckLines);
        Assert.Empty(calls);

        var core = new FakeCoreContext();
        var inside = core.Add(new FakeDevice { Address = "10.0.0.48" });
        await using var plugin = new PkiPlugin(PkiHarness.Options(new FakeTrustStore()));
        await plugin.StartAsync(core, CancellationToken.None);
        var limited = FakeCallContext.WithGroup(core, [inside.Id], "tech2");
        var answer = PkiJson.Deserialize<PolicyCheck>(await core.InvokeAsync(plugin, limited, PolicyMethods.CheckDevice, PkiJson.Serialize(new PolicyCheckRequest(inside.Id))));
        Assert.Equal((0, null), (answer.Lines.Count, answer.Error));
        Assert.NotEmpty(PkiJson.Deserialize<PolicyCheck>(await core.InvokeAsync(plugin, core.CallAsAllDevices(), PolicyMethods.CheckDevice, PkiJson.Serialize(new PolicyCheckRequest(inside.Id)))).Lines);
        Assert.Equal(Oadm.Sdk.Plugins.UserRole.Operator, ((Oadm.Sdk.Plugins.ICorePlugin)plugin).RequiredRole(PolicyMethods.CheckDevice));
        Assert.False(plugin.IsAudited(PolicyMethods.CheckDevice));
    }

    /// <summary>The page's dialogs in a test: the editor is filled by <see cref="Fill"/> and saved; the picker answers <see cref="Pick"/>.</summary>
    private sealed class SavingDialogs : IPolicyDialogs
    {
        public List<PolicyEditorViewModel> Shown { get; } = [];

        public Action<PolicyEditorViewModel>? Fill { get; set; }

        public Guid? Pick { get; set; }

        public async Task<bool> ShowPolicyAsync(PolicyEditorViewModel editor)
        {
            Shown.Add(editor);
            Fill?.Invoke(editor);
            return await editor.SaveAsync();
        }

        public Task<Guid?> PickDeviceAsync(IReadOnlyList<IDeviceInfo> devices, Guid? selected) => Task.FromResult(Pick);
    }

    [Fact]
    public async Task Without_the_permission_the_card_is_read_only_and_a_group_user_reads_none()
    {
        await using var pki = await StartAsync();
        var ctx = Page(pki);
        ctx.Manage = false;
        var card = new PoliciesViewModel(ctx);
        await card.LoadAsync(CancellationToken.None);
        Assert.NotEmpty(card.Vlans.Rows);
        Assert.False(card.MayManage);
        Assert.All(card.Sections, s => Assert.False(s.AddCommand.CanExecute(null) || s.MayManage));
        Assert.All(card.Settings, s => Assert.True(s.ReadOnly && !s.IsEditable && !s.CanSave)); // read only in place
        Assert.Equal("No other issuing policies.", card.OtherIssuing.EmptyText); // no Add to point to

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
        Assert.All(hidden.Sections, s => Assert.Empty(s.Rows));
        Assert.Empty(hidden.Settings);
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
        Assert.Contains(card.Blocked.Rows, r => r.Name == "PTZ" && r.Kind == "Block");
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

        editor.Kind = editor.KindChoices.Single(k => k.Value == PolicyKinds.Block);
        Assert.True(editor.OffersAllDevices); // Block alone may hold every device
        editor.AlsoRevoke = true;
        Assert.False(editor.OffersAllDevices); // also revoking: devices and sites only
        Assert.True(editor.ShowsPicker);

        var expiry = card.NewEditor(card.List.Rows.Single(r => r.KindName == "Expiry warning").Policy);
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
        var existing = card.NewEditor(card.List.Rows.Single(r => r.Policy.Name == "OADM CA").Policy);
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
        var editor = card.NewEditor(card.List.Rows.Single(r => r.KindName == "802.1X server").Policy);

        Assert.True(editor.IsServer);
        Assert.False(editor.HasDevices);
        Assert.Null(editor.TlsHint);
        Assert.Equal("1,020", editor.LargestPacket);
        Assert.Equal(PolicyTexts.SwitchDecides, editor.Reauth.Label);
        Assert.All(editor.CipherSuites.Where(s => s.IsLegacy), s => Assert.False(s.IsListed));

        editor.MinimumTls = PolicyEditorViewModel.TlsVersions[1]; // TLS 1.1
        Assert.Equal(PolicyEditorViewModel.OldTlsHint, editor.TlsHint);
        Assert.All(editor.CipherSuites.Where(s => s.IsLegacy), s => Assert.True(s.IsListed && s.IsChecked == s.IsUsableWithKey)); // the usable ones checked when lowered
        Assert.Equal(5, editor.CipherSuites.Count(s => s.IsListed && s.HasKeyHint)); // the ECDHE_RSA suites and the server's ECDSA key

        editor.MinimumTls = PolicyEditorViewModel.TlsVersions[3]; // TLS 1.3
        foreach (var suite in editor.CipherSuites)
        {
            suite.IsChecked = false;
        }

        Assert.Equal(PolicyRules.CheckCipherSuite, editor.CipherSuitesError);
        editor.ResetCipherSuitesCommand.Execute(null);
        Assert.Null(editor.CipherSuitesError);
        Assert.Equal(RadiusPolicies.DefaultSuites(Radius.Protocol.Tls.TlsVersion.Tls13), editor.CipherSuites.Where(s => s.IsChecked).Select(s => s.Id).Order());
        Assert.DoesNotContain(editor.CipherSuites, s => s.IsChecked && s.HasKeyHint); // never one the server cannot use

        editor.MinimumTls = PolicyEditorViewModel.TlsVersions[2]; // TLS 1.2: Reset gives what a new server has
        editor.ResetCipherSuitesCommand.Execute(null);
        Assert.Equal(RadiusPolicies.DefaultServerPolicy().CipherSuites, editor.CipherSuites.Where(s => s.IsChecked).Select(s => s.Id).Order());
        editor.MinimumTls = PolicyEditorViewModel.TlsVersions[3];
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
        editor.Kind = editor.KindChoices.Single(k => k.Value == PolicyKinds.Block);
        Assert.True(editor.IsBlock);
        editor.AlsoRevoke = true;
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
        var stolen = Assert.Single(card.Blocked.Rows, r => r.Name == "Stolen");
        Assert.Equal(("Block", true), (stolen.Kind, stolen.Source.AlsoRevoke));
        Assert.Equal("The 802.1X server refuses these devices. Their IEEE 802.1X certificates are revoked, new ones too.", stolen.Summary);
        Assert.NotEmpty(pki.Service.Issued.Revocations);
    }
}

/// <summary>The PKI page and the policy editor rendered offscreen at 1,700 and 1,000 px (OADM_SCREENSHOT_DIR writes PNGs).</summary>
[Collection(HeadlessSessions.Name)]
public sealed class PolicyHeadlessTests
{
    [Theory]
    [InlineData(1700)]
    [InlineData(1000)]
    public async Task The_page_shows_the_cas_and_the_certificates_and_the_policies_on_their_own_tab(int width)
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
            // The page as the user sees it: with the server tabs (stand-ins; their tests render them on their tab).
            ctx.Tabs = [new Oadm.Sdk.Client.CorePluginTab("oadm.radius", "802.1X server", new TextBlock()), new Oadm.Sdk.Client.CorePluginTab("oadm.est", "EST server", new TextBlock())];
            var view = new PkiView();
            using var vm = new PkiViewModel(ctx, new FakeClientTrust());
            var window = HeadlessPageTests.Host(view, width, 1500);
            window.Show();
            view.DataContext = vm;
            await vm.LoadAsync();
            await vm.Policies.LoadAsync(CancellationToken.None);
            await vm.Authorities.LoadAsync();
            await vm.Certificates.LoadAsync();
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(window, outDir, $"pki-page-{width}.png");
            List<string?> Texts() => [.. window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text)];
            int Primaries() => window.GetVisualDescendants().OfType<Button>().Count(b => b.IsEffectivelyVisible && b.Classes.Contains("primary"));
            var certificates = (Texts: Texts(), Primaries: Primaries(), Policies: view.FindControl<Panel>("PoliciesPage")!.IsEffectivelyVisible);
            var tabs = window.GetVisualDescendants().OfType<PageTab>().Count(t => t.IsEffectivelyVisible);

            // The search boxes lie whole inside their lists (the filters wrap on a narrow card); the CAs have no filters.
            var searchFits = new[] { "AuthoritiesList", "CertificatesList" }.All(name =>
            {
                var list = view.FindControl<IssuedCertificateList>(name)!;
                var search = list.GetVisualDescendants().OfType<SearchBox>().Single(s => s.IsEffectivelyVisible);
                var searchRight = Avalonia.VisualExtensions.TranslatePoint(search, new Avalonia.Point(search.Bounds.Width, 0), list)!.Value.X;
                return searchRight <= list.Bounds.Width + 0.5;
            });
            var caCombos = view.FindControl<IssuedCertificateList>("AuthoritiesList")!.GetVisualDescendants().OfType<ComboBox>().Count(c => c.IsEffectivelyVisible);

            // The Policies tab: cards by area (its own test renders it in detail).
            vm.ShowTab(PkiViewModel.PoliciesTabId);
            HeadlessPageTests.Pump();
            var policies = (Texts: Texts(), Primaries: Primaries(),
                Lists: window.GetVisualDescendants().OfType<IssuedCertificateList>().Count(l => l.IsEffectivelyVisible),
                Groups: window.GetVisualDescendants().OfType<DataGridRowGroupHeader>().Count(h => h.IsEffectivelyVisible));
            window.Close();
            return (certificates, policies, tabs, searchFits, caCombos);
        }, CancellationToken.None);

        Assert.True(checks.searchFits);
        Assert.Equal(0, checks.caCombos); // no Group by and no filters on the CAs
        Assert.Equal(4, checks.tabs); // Certificates, Policies, 802.1X server, EST server

        // Certificates: the CAs in their card, the certificates below; no policies.
        foreach (var title in new[] { "Policies", "Certificate authorities", "Certificates", "New OADM CA", "Create", "Sign request", "Import", "OADM CA", "Previous OADM CA", "Root CA" })
        {
            Assert.Contains(title, checks.certificates.Texts);
        }

        Assert.False(checks.certificates.Policies);
        Assert.DoesNotContain("Add policy", checks.certificates.Texts);
        Assert.DoesNotContain(checks.certificates.Texts, t => t?.StartsWith("Certificate authorities · ", StringComparison.Ordinal) == true); // no group of the CAs any more
        Assert.DoesNotContain("Generate new CA", checks.certificates.Texts);
        Assert.DoesNotContain("Previous certificate authorities", checks.certificates.Texts); // previous CAs once: rows of the CA list
        Assert.DoesNotContain("Used by", checks.certificates.Texts);
        Assert.Equal(1, checks.certificates.Primaries); // Create

        // Policies: the cards by area, no certificate list, no grouped table.
        foreach (var title in new[] { "Check a device", "Certificates for cameras", "802.1X network access", "EST" })
        {
            Assert.Contains(title, checks.policies.Texts);
        }

        Assert.Equal(0, checks.policies.Lists);
        Assert.Equal(0, checks.policies.Groups);
        Assert.Equal(1, checks.policies.Primaries); // the device of Check a device
        Assert.DoesNotContain("Add policy", checks.policies.Texts);
        Assert.All(checks.certificates.Texts.Concat(checks.policies.Texts), t => Assert.False(t?.EndsWith("...", StringComparison.Ordinal) == true || t?.EndsWith('…') == true, t));
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

            var vlan = card.NewEditor(card.List.Rows.Single(r => r.Policy.Name == "Building B").Policy);
            var vlanWindow = PolicyFormView.CreateWindow(vlan);
            Oadm.Client.App.ApplyCrispText(vlanWindow);
            vlanWindow.Show();
            vlan.Vlan = "0";
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(vlanWindow, outDir, "pki-policy-vlan.png");
            var vlanTexts = Texts(vlanWindow);
            var vlanPicker = vlanWindow.IsPickerVisible;
            vlanWindow.Close();

            var allow = card.NewEditor(null, PolicyKinds.AllowCa); // Add of Trusted CAs: no kind choice
            var allowWindow = PolicyFormView.CreateWindow(allow);
            Oadm.Client.App.ApplyCrispText(allowWindow);
            allowWindow.Show();
            allow.Name = "Contoso";
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(allowWindow, outDir, "pki-policy-allow-ca.png");
            var allowTexts = Texts(allowWindow);
            var allowPicker = allowWindow.IsPickerVisible;
            allowWindow.Close();

            var server = card.NewEditor(card.List.Rows.Single(r => r.KindName == "802.1X server").Policy);
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

            var expiry = card.NewEditor(card.List.Rows.Single(r => r.KindName == "Expiry warning").Policy);
            var expiryWindow = PolicyFormView.CreateWindow(expiry);
            Oadm.Client.App.ApplyCrispText(expiryWindow);
            expiryWindow.Show();
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(expiryWindow, outDir, "pki-policy-expiry.png");
            var expiryTexts = Texts(expiryWindow);
            var expiryPicker = expiryWindow.IsPickerVisible;
            expiryWindow.Close();

            // Block with Also revoke (decision 2026-10-25): the check box, then the picker without All devices.
            var block = card.NewEditor(card.List.Rows.Single(r => r.Policy.Name == "Storage room").Policy);
            var blockWindow = PolicyFormView.CreateWindow(block);
            Oadm.Client.App.ApplyCrispText(blockWindow);
            blockWindow.Show();
            block.AlsoRevoke = true;
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(blockWindow, outDir, "pki-policy-block.png");
            var blockTexts = Texts(blockWindow);
            var blockPicker = blockWindow.IsPickerVisible;
            blockWindow.Close();

            var question = new MessageWindow { Heading = "Add policy", Message = "Saving revokes 7 IEEE 802.1X certificates of these devices, and every new one while the policy exists. This cannot be undone.", ConfirmText = "Revoke", CancelText = "Cancel", IsDanger = true };
            Oadm.Client.App.ApplyCrispText(question);
            question.Show();
            HeadlessPageTests.Pump();
            HeadlessPageTests.Capture(question, outDir, "pki-policy-revoke-question.png");
            var red = question.ConfirmButton.Classes.Contains("danger");
            question.Close();
            return (vlanTexts, vlanPicker, allowTexts, allowPicker, serverTexts, expiryTexts, expiryPicker, red, blockTexts, blockPicker);
        }, CancellationToken.None);

        Assert.Contains(PolicyTexts.AlsoRevoke, checks.blockTexts);
        Assert.DoesNotContain(PolicyTexts.AllDevices, checks.blockTexts); // also revoking: devices and sites only
        Assert.True(checks.blockPicker);
        Assert.Contains("Issued by", checks.serverTexts); // the 802.1X server policy holds its certificate
        Assert.Contains("Add 802.1X trusted CA policy", checks.allowTexts);
        Assert.DoesNotContain("Kind", checks.allowTexts);

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
        Assert.Equal([PolicyTexts.SwitchDecides, "Every"], PolicyEditorViewModel.ReauthChoices.Select(c => c.Label)); // no placeholder letter
        Assert.DoesNotContain(checks.serverTexts, t => t?.Contains(" N ", StringComparison.Ordinal) == true);
        Assert.Contains("Days before the end", checks.expiryTexts);
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
