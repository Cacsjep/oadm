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
/// The Policies tab of the PKI page (docs/spec/pki.md "Policies", decision 2026-10-25) against the in-process plugin: one
/// table grouped by area with the built-in policies first, the search, the side panel (Create lists the types, Next shows
/// the form, a row opens its form, Save closes it, changes not saved ask first), read only without the permission, none
/// for a group user, the PRO groups only with OADM PRO; the form's fields, errors below them, the devices behind Choose
/// devices, the overlap below the device choice, the revoke question with the count.
/// </summary>
public sealed class PolicyPageTests
{
    internal static readonly FakeDevice Camera = new FakeDevice { Serial = "ACCC8E000001", Address = "10.0.0.48", Tags = ["PTZ"] };
    internal static readonly FakeDevice Other = new FakeDevice { Serial = "ACCC8E000002", Address = "10.0.0.49" };

    /// <summary>A PKI with the 802.1X defaults (the 802.1X server's conversion of a new server), one VLAN, one Block.</summary>
    internal static async Task<PkiHarness> StartAsync(params IDeviceInfo[] devices) => await StartAsync(true, devices);

    internal static async Task<PkiHarness> StartAsync(bool pro, params IDeviceInfo[] devices)
    {
        var pki = await PkiHarness.StartAsync(devices: devices.Length == 0 ? [Camera, Other] : devices, pro: pro);
        await pki.Policies.ConvertOnceAsync(Radius.RadiusPlugin.PluginId, ct => RadiusPolicies.ConvertAsync(new FakeSettings(), pki.Service.Authorities(), ct), CancellationToken.None);
        await pki.InvokeAsync<PolicySaveReply>(PolicyMethods.SavePolicy, new PkiPolicy { Name = "Building B", Kind = PolicyKinds.Vlan, Vlan = 30, Devices = new PolicyDevices { ThisServer = true, ExceptSerials = ["ACCC8E000002"] } });
        await pki.InvokeAsync<PolicySaveReply>(PolicyMethods.SavePolicy, new PkiPolicy { Name = "Storage room", Kind = PolicyKinds.Block, Devices = new PolicyDevices { Serials = ["ACCC8E000002"] } });
        return pki;
    }

    internal static PluginPageContext Page(PkiHarness pki) => new(pki.Plugin, pki.Hub) { DeviceList = [Camera, Other] };

    /// <summary>Opens the policy of the row named <paramref name="name"/> in the panel (a click on the row).</summary>
    internal static PolicyEditorViewModel Open(PoliciesViewModel page, string name)
    {
        page.SelectedRow = page.Rows.Single(r => r.Name == name);
        Assert.True(page.IsPanelOpen && page.IsEditing);
        return page.Editor!;
    }

    [Fact]
    public async Task The_table_groups_the_policies_by_area_with_the_built_in_ones_first()
    {
        await using var pki = await StartAsync();
        var page = new PoliciesViewModel(Page(pki));
        await page.LoadAsync(CancellationToken.None);

        // No EST enrollment policies here (EST not converted): their group shows no header.
        Assert.Equal([PolicyTableGroups.Issuing, PolicyTableGroups.Servers, PolicyTableGroups.Access, PolicyTableGroups.Expiry], page.Rows.Select(r => r.Group).Distinct());
        Assert.Equal(
            [
                (PolicyTableGroups.Issuing, "HTTPS", "HTTPS certificate", true),
                (PolicyTableGroups.Issuing, "IEEE 802.1X", "IEEE 802.1X certificate", true),
                (PolicyTableGroups.Servers, "EST server", "EST server", true), // in age order: PKI made it before the 802.1X server's conversion
                (PolicyTableGroups.Servers, "802.1X server", "802.1X server", true),
                (PolicyTableGroups.Access, "OADM CA", "802.1X trusted CA", false),
                (PolicyTableGroups.Access, "Axis device ID", "802.1X trusted CA", false),
                (PolicyTableGroups.Access, "Building B", "VLAN", false),
                (PolicyTableGroups.Access, "Storage room", "Block", false),
            ],
            page.Rows.Where(r => r.Group is PolicyTableGroups.Issuing or PolicyTableGroups.Servers or PolicyTableGroups.Access).Select(r => (r.Group, r.Name, r.Type, r.IsSystem)));
        Assert.Equal(("All devices", true), (page.Rows.Single(r => r.Group == PolicyTableGroups.Expiry).Name, page.Rows.Single(r => r.Group == PolicyTableGroups.Expiry).IsSystem));
        Assert.Equal("Enable HTTPS issues certificates from OADM Root CA SERVER01, valid 365 days, with an ECC P-256 key.", page.Rows.Single(r => r.Name == "HTTPS").Summary);
        Assert.Equal("The 802.1X server puts these devices in VLAN 30.", page.Rows.Single(r => r.Name == "Building B").Summary);
        Assert.DoesNotContain(page.Rows, r => r.HasProblem);
        Assert.False(page.IsPanelOpen);
        Assert.True(page.MayManage);
        Assert.True(page.CreateCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_search_filters_by_name_type_and_summary_and_says_when_nothing_matches()
    {
        await using var pki = await StartAsync();
        var page = new PoliciesViewModel(Page(pki));
        await page.LoadAsync(CancellationToken.None);

        page.SearchText = "vlan";
        Assert.Equal(["Building B"], page.Rows.Select(r => r.Name)); // its type, and its summary names VLAN 30
        page.SearchText = "storage";
        Assert.Equal(["Storage room"], page.Rows.Select(r => r.Name));
        page.SearchText = "nothing like this";
        Assert.Empty(page.Rows);
        Assert.True(page.HasNoMatch);
        page.SearchText = null;
        Assert.False(page.HasNoMatch);
    }

    [Fact]
    public async Task Create_lists_the_types_next_shows_the_form_and_save_adds_it_and_closes_the_panel()
    {
        await using var pki = await StartAsync();
        var page = new PoliciesViewModel(Page(pki));
        await page.LoadAsync(CancellationToken.None);

        await page.CreateCommand.ExecuteAsync(null);
        Assert.True(page.IsPanelOpen && page.IsChoosingKind && !page.IsEditing);
        Assert.Equal(PoliciesViewModel.CreateTitle, page.PanelTitle);
        Assert.DoesNotContain(page.KindChoices, k => k.Kind is PolicyKinds.Dot1xServer or PolicyKinds.EstServer); // one each, there are
        Assert.All(page.KindChoices, k => Assert.True(k.HasDescription, k.Kind));
        Assert.False(page.NextCommand.CanExecute(null)); // no type chosen yet

        page.SelectedKind = page.KindChoices.Single(k => k.Kind == PolicyKinds.Vlan);
        page.NextCommand.Execute(null);
        var editor = page.Editor!;
        Assert.True(page.IsEditing && editor.IsNew);
        Assert.Equal(("New VLAN policy", "VLAN", PoliciesViewModel.CreateTitle), (page.PanelTitle, page.PanelKind, PoliciesViewModel.CreateTitle));
        Assert.Equal(PolicyEditorViewModel.NotSavedText, page.SaveStatus);
        Assert.False(page.CanDelete);

        editor.Name = "Lobby";
        editor.Vlan = "40";
        var picker = editor.NewPicker();
        picker.Visible.Single(i => i.Id == Other.Id).Chosen = true; // the camera Building B leaves out
        editor.UsePicker(picker);
        Assert.Equal("1 device", editor.DevicesText);
        await page.SaveOpenCommand.ExecuteAsync(null);

        Assert.False(page.IsPanelOpen);
        Assert.Null(page.Editor);
        Assert.Contains(page.Rows, r => r.Name == "Lobby" && r.Type == "VLAN" && !r.IsSystem);
        Assert.Null(page.Error);
    }

    [Fact]
    public async Task A_row_opens_its_form_delete_asks_and_built_in_policies_have_no_delete()
    {
        await using var pki = await StartAsync();
        var ctx = Page(pki);
        var page = new PoliciesViewModel(ctx);
        await page.LoadAsync(CancellationToken.None);

        var https = Open(page, "HTTPS");
        Assert.True(https.IsInPlace && !https.ShowsName && page.ShowsSystemBadge);
        Assert.Equal(("HTTPS", null), (page.PanelTitle, page.PanelKind));
        Assert.False(page.CanDelete);
        Assert.Null(page.SaveStatus); // nothing changed yet
        Assert.False(page.SaveOpenCommand.CanExecute(null));

        Open(page, "Building B");
        Assert.Equal(("Building B", "VLAN"), (page.PanelTitle, page.PanelKind));
        Assert.True(page.CanDelete);
        await page.DeleteOpenCommand.ExecuteAsync(null);
        Assert.Equal(("Delete policy", "Delete the policy Building B?", "Delete"), ctx.Confirmations[^1]);
        Assert.False(page.IsPanelOpen);
        Assert.DoesNotContain(page.Rows, r => r.Name == "Building B");
    }

    [Fact]
    public async Task Leaving_a_form_with_changes_asks_first_and_cancel_keeps_it_open()
    {
        await using var pki = await StartAsync();
        var ctx = Page(pki);
        var page = new PoliciesViewModel(ctx);
        await page.LoadAsync(CancellationToken.None);
        var expiry = Open(page, "All devices");
        expiry.Days = "45";
        Assert.True(expiry.IsDirty);

        // Another row: the question; No keeps the form and its row.
        ctx.ConfirmAnswer = false;
        var before = ctx.Confirmations.Count;
        page.SelectedRow = page.Rows.Single(r => r.Name == "HTTPS");
        await Eventually.TrueAsync(() => ctx.Confirmations.Count > before, "the question about the changes");
        Assert.Equal(("Discard changes", "The changes to All devices are not saved.", "Discard"), ctx.Confirmations[^1]);
        await Eventually.TrueAsync(() => page.SelectedRow?.Name == "All devices", "the row of the open form selected again");
        Assert.Same(expiry, page.Editor);

        // Close with Discard: the panel closes, nothing saved.
        ctx.ConfirmAnswer = true;
        await page.CloseCommand.ExecuteAsync(null);
        Assert.False(page.IsPanelOpen);
        Assert.Equal(30, pki.Policies.FirstOf(PolicyKinds.ExpiryWarning)!.Days);
    }

    [Fact]
    public async Task A_built_in_policy_saves_only_with_changes_and_a_reload_keeps_a_form_with_changes()
    {
        await using var pki = await StartAsync();
        var page = new PoliciesViewModel(Page(pki));
        await page.LoadAsync(CancellationToken.None);
        var expiry = Open(page, "All devices");
        Assert.Equal(PolicyEditorViewModel.NoChangesText, page.SaveBlockedReason);

        // An error directly below the field, Save off; a valid value: Not saved left of Save.
        expiry.Days = "400";
        Assert.Equal(PolicyTexts.DaysRange, expiry.ErrorOf(nameof(PolicyEditorViewModel.Days)));
        Assert.False(page.SaveOpenCommand.CanExecute(null));
        expiry.Days = "45";
        Assert.True(page.SaveOpenCommand.CanExecute(null));
        Assert.Equal(PolicyEditorViewModel.NotSavedText, page.SaveStatus);

        // A reload (another user's change) keeps the changes of a form not saved yet.
        await page.LoadAsync(CancellationToken.None);
        Assert.Same(expiry, page.Editor);

        // Save: the server stores it, the panel closes; the devices stay as they were.
        await page.SaveOpenCommand.ExecuteAsync(null);
        var saved = pki.Policies.FirstOf(PolicyKinds.ExpiryWarning)!;
        Assert.Equal((45, true), (saved.Days, saved.Devices!.AllDevices));
        Assert.False(page.IsPanelOpen);
    }

    [Fact]
    public async Task A_built_in_expiry_warning_for_chosen_devices_names_them_and_keeps_them_on_save()
    {
        await using var pki = await StartAsync();
        var all = pki.Policies.FirstOf(PolicyKinds.ExpiryWarning)!;
        Assert.Null((await pki.InvokeAsync<PolicySaveReply>(PolicyMethods.SavePolicy, all with { Devices = new PolicyDevices { Tags = ["PTZ"] } })).Error);
        var page = new PoliciesViewModel(Page(pki));
        await page.LoadAsync(CancellationToken.None);

        var expiry = Open(page, "All devices");
        Assert.Equal("For Tag PTZ", expiry.DaysHint);
        Assert.False(expiry.ShowsDevicesField); // a built-in policy keeps its devices
        expiry.Days = "60";
        Assert.True(await expiry.SaveAsync());
        Assert.Equal((60, "PTZ"), (pki.Policies.FirstOf(PolicyKinds.ExpiryWarning)!.Days, pki.Policies.FirstOf(PolicyKinds.ExpiryWarning)!.Devices!.Tags.Single()));
    }

    [Fact]
    public async Task Without_the_permission_the_tab_is_read_only_and_a_group_user_reads_none()
    {
        await using var pki = await StartAsync();
        var ctx = Page(pki);
        ctx.Manage = false;
        var page = new PoliciesViewModel(ctx);
        await page.LoadAsync(CancellationToken.None);
        Assert.NotEmpty(page.Rows);
        Assert.False(page.MayManage);
        Assert.False(page.CreateCommand.CanExecute(null));
        var vlan = Open(page, "Building B");
        Assert.True(vlan.ReadOnly && !vlan.IsEditable && !vlan.CanSave);
        Assert.False(page.ShowsSave || page.CanDelete);

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
    public async Task A_role_change_keeps_the_open_form_unless_what_the_user_may_do_here_changed()
    {
        await using var pki = await StartAsync();
        var ctx = Page(pki);
        var page = new PoliciesViewModel(ctx);
        await page.LoadAsync(CancellationToken.None);
        var vlan = Open(page, "Building B");
        vlan.Name = "Building B, floor 2";
        Assert.True(vlan.IsDirty);

        // Another role changed (or a device group that leaves this user as before): the typed name stays.
        page.NotifyRole();
        Assert.Same(vlan, page.Editor);
        Assert.Equal("Building B, floor 2", vlan.Name);

        // Manage taken away: the same form, read only.
        ctx.Manage = false;
        page.NotifyRole();
        Assert.Same(vlan, page.Editor);
        Assert.True(vlan.ReadOnly);
        Assert.False(page.ShowsSave);
        Assert.Equal("Building B, floor 2", vlan.Name);

        // Limited to a device group: the tab hides, the form closes.
        ctx.HasAllDevices = false;
        page.NotifyRole();
        Assert.Null(page.Editor);
        Assert.False(page.IsPanelOpen);
        Assert.Empty(page.Rows);
    }

    [Fact]
    public async Task Without_oadm_pro_the_groups_of_the_pro_servers_and_their_types_are_hidden()
    {
        await using var pki = await StartAsync(pro: false);
        var page = new PoliciesViewModel(Page(pki));
        await page.LoadAsync(CancellationToken.None);

        Assert.False(page.List.Pro);
        Assert.Equal([PolicyTableGroups.Issuing, PolicyTableGroups.Expiry], page.Rows.Select(r => r.Group).Distinct());
        Assert.Equal([PolicyKinds.HttpsCertificate, PolicyKinds.Dot1xCertificate, PolicyKinds.ExpiryWarning], page.KindChoices.Select(k => k.Kind));
        Assert.Equal(2, pki.Policies.PoliciesOf(PolicyKinds.Vlan).Count + pki.Policies.PoliciesOf(PolicyKinds.Block).Count); // stored, only hidden
    }

    [Fact]
    public async Task A_new_vlan_policy_shows_errors_below_its_fields_and_the_overlap_below_the_device_choice()
    {
        await using var pki = await StartAsync();
        var page = new PoliciesViewModel(Page(pki));
        await page.LoadAsync(CancellationToken.None);
        var editor = page.NewEditor(PolicyKinds.Vlan);

        Assert.True(editor.IsNew);
        Assert.Equal([PolicyKinds.Vlan], editor.KindChoices.Select(k => k.Value)); // the type comes from Create: no choice
        Assert.True(editor.HasDevices && editor.ShowsDevicesField);
        Assert.Equal(PolicyEditorViewModel.NoDevicesText, editor.DevicesText);
        Assert.Null(editor.ErrorOf(nameof(PolicyEditorViewModel.Name))); // an untouched form shows no error
        Assert.False(editor.CanSave);

        editor.Name = "building b";
        editor.Vlan = "4095";
        Assert.Equal(PolicyTexts.NameTaken(PolicyKinds.Vlan), editor.ErrorOf(nameof(PolicyEditorViewModel.Name)));
        Assert.Equal(PolicyTexts.VlanRange, editor.ErrorOf(nameof(PolicyEditorViewModel.Vlan)));
        editor.Name = "PTZ";
        editor.Vlan = "40";
        Assert.Equal(PolicyTexts.ChooseDevices, editor.SaveBlockedReason);

        var picker = editor.NewPicker();
        picker.Tags.Single(t => t.Name == "PTZ").IsChecked = true;
        editor.UsePicker(picker);
        Assert.Equal("Tag PTZ", editor.DevicesText);
        Assert.True(editor.CanSave);
        Assert.False(await editor.SaveAsync());
        Assert.Equal("10.0.0.48 is in PTZ and Building B.", editor.DevicesError); // below the Devices field

        // Another type may hold it.
        var block = page.NewEditor(PolicyKinds.Block);
        block.Name = "PTZ";
        var blockPicker = block.NewPicker();
        blockPicker.Tags.Single(t => t.Name == "PTZ").IsChecked = true;
        block.UsePicker(blockPicker);
        Assert.True(await block.SaveAsync());
        Assert.Contains(page.Rows, r => r.Name == "PTZ" && r.Type == "Block");
    }

    [Fact]
    public async Task Cancel_of_choose_devices_changes_nothing()
    {
        await using var pki = await StartAsync();
        var page = new PoliciesViewModel(Page(pki)) { Dialogs = new PickerAnswers(false) };
        await page.LoadAsync(CancellationToken.None);
        var editor = Open(page, "Storage room");
        var before = editor.Picker;

        await editor.ChooseDevicesCommand.ExecuteAsync(null);
        Assert.Same(before, editor.Picker); // Cancel: the form keeps its choice
        Assert.False(editor.IsDirty);

        page.Dialogs = new PickerAnswers(true, p => p.Visible.Single(i => i.Id == Camera.Id).Chosen = true);
        await editor.ChooseDevicesCommand.ExecuteAsync(null);
        Assert.True(editor.IsDirty);
        Assert.Equal([Camera.Serial, Other.Serial], editor.ToPolicy().Devices!.Serials!.Order());
        Assert.Equal("2 devices", editor.DevicesText);
    }

    [Fact]
    public async Task All_devices_hides_the_device_choice_and_its_overlap_shows_below_the_devices_field()
    {
        await using var pki = await StartAsync();
        var page = new PoliciesViewModel(Page(pki));
        await page.LoadAsync(CancellationToken.None);
        var editor = page.NewEditor(PolicyKinds.Vlan);
        Assert.Equal([PolicyTexts.AllDevices, PolicyTexts.ChosenDevices], PolicyEditorViewModel.DeviceScopes.Select(c => c.Label));
        Assert.True(editor.OffersAllDevices);
        Assert.True(editor.ShowsPicker); // a new policy starts with chosen devices

        editor.Name = "Everyone";
        editor.Vlan = "99";
        editor.Scope = PolicyEditorViewModel.DeviceScopes[0];
        Assert.False(editor.ShowsPicker);
        Assert.True(editor.CanSave); // nothing to pick
        Assert.Equal(PolicyDevices.Everything, editor.ToPolicy().Devices);
        Assert.False(await editor.SaveAsync());
        Assert.Equal("Everyone includes every device, also those of Building B.", editor.ErrorOf(nameof(PolicyEditorViewModel.Scope)));

        var block = page.NewEditor(PolicyKinds.Block);
        Assert.True(block.OffersAllDevices); // Block alone may hold every device
    }

    [Fact]
    public async Task Allow_ca_has_no_device_choice_and_the_same_ca_twice_is_refused_below_the_ca()
    {
        await using var pki = await StartAsync();
        var page = new PoliciesViewModel(Page(pki));
        await page.LoadAsync(CancellationToken.None);
        var editor = page.NewEditor(PolicyKinds.AllowCa);

        Assert.False(editor.HasDevices || editor.ShowsDevicesField);
        Assert.Equal(["OADM Root CA SERVER01", "Axis device ID"], editor.CaChoices.Select(c => c.Label));
        Assert.True(editor.OnlyManaged);
        Assert.Equal(editor.CaChoices[0], editor.Ca); // both CAs have an Allow CA policy already: the first one
        editor.Name = "Again";
        Assert.True(editor.CanSave); // no devices needed
        Assert.False(await editor.SaveAsync());
        Assert.Equal(PolicyTexts.CaTaken, editor.ErrorOf(nameof(PolicyEditorViewModel.Ca)));

        // An existing policy keeps its type.
        var existing = Open(page, "OADM CA");
        Assert.False(existing.IsNew);
        Assert.Equal([PolicyKinds.AllowCa], existing.KindChoices.Select(k => k.Value));
        Assert.Equal("OADM CA", existing.Heading);
    }

    [Fact]
    public async Task The_server_policy_picks_its_certificate_and_checks_its_fields_without_a_cipher_suite_choice()
    {
        await using var pki = await StartAsync();
        await pki.Service.EnsureServerCertificatesAsync(null, CancellationToken.None); // OADM PRO: one certificate for both servers
        var page = new PoliciesViewModel(Page(pki));
        await page.LoadAsync(CancellationToken.None);
        var editor = Open(page, "802.1X server");

        Assert.True(editor.IsServer && editor.IsServerKind);
        Assert.False(editor.HasDevices);
        Assert.Equal(PkiService.SharedServerCertificateName, editor.Certificate!.Label.Split(" (")[0]);
        Assert.False(editor.HasNoCertificates);
        Assert.Null(editor.TlsHint);
        Assert.Equal("1,020", editor.LargestPacket);
        Assert.Equal(PolicyTexts.SwitchDecides, editor.Reauth.Label);

        editor.MinimumTls = PolicyEditorViewModel.TlsVersions[1]; // TLS 1.1
        Assert.Equal(PolicyEditorViewModel.OldTlsHint, editor.TlsHint);
        editor.MinimumTls = PolicyEditorViewModel.TlsVersions[3]; // TLS 1.3
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
        Assert.Equal(pki.Policies.EstServerPolicy!.CertificateId, saved.CertificateId); // one certificate for both servers
    }

    [Fact]
    public async Task The_ieee_8021x_policy_of_a_new_server_signs_in_with_the_ip_address_and_eapol_2()
    {
        await using var pki = await StartAsync();
        var page = new PoliciesViewModel(Page(pki));
        await page.LoadAsync(CancellationToken.None);
        var editor = Open(page, "IEEE 802.1X");

        Assert.Equal((2, Dot1xIdentity.IpAddress), (editor.Eapol.Value, editor.Identity.Value));
        Assert.Equal(["1", "2", "3"], PolicyEditorViewModel.EapolChoices.Select(c => c.Label));
        Assert.Contains(PolicyEditorViewModel.IdentityChoices, c => c.Value == Dot1xIdentity.IpLastOctet);
        Assert.Equal("OADM Root CA SERVER01", editor.RadiusCa!.Label); // a CA of the list
        Assert.Contains("Cameras sign in with their IP address.", page.Rows.Single(r => r.Name == "IEEE 802.1X").Summary, StringComparison.Ordinal);
    }

    /// <summary>The page's device picker in a test: OK or Cancel, with the choice <see cref="Choose"/> makes.</summary>
    internal sealed class PickerAnswers(bool ok, Action<DevicePickerViewModel>? choose = null) : IPolicyDialogs
    {
        public Task<bool> PickDevicesAsync(DevicePickerViewModel picker, string heading)
        {
            choose?.Invoke(picker);
            return Task.FromResult(ok);
        }
    }
}

/// <summary>The PKI page rendered offscreen at 1,700 and 1,000 px (OADM_SCREENSHOT_DIR writes PNGs).</summary>
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
            var certificates = (Texts: Texts(), Primaries: Primaries(), Policies: view.FindControl<Grid>("PoliciesPage")!.IsEffectivelyVisible);
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

            // The Policies tab: the grouped table (its own test renders it in detail).
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
        foreach (var title in new[] { "Policies", "Certificate authorities", "Certificates", "Create CA", "Create", "Sign request", "Import", "Root CA", "Intermediate CA" })
        {
            Assert.Contains(title, checks.certificates.Texts);
        }

        // Decision 2026-10-26: no New OADM CA, no OADM CA or Previous OADM CA badges, no revoke.
        foreach (var gone in new[] { "New OADM CA", "OADM CA", "Previous OADM CA", "Imported", "Can issue", "Revoke", "Revoked" })
        {
            Assert.DoesNotContain(gone, checks.certificates.Texts);
        }

        Assert.False(checks.certificates.Policies);
        Assert.DoesNotContain("Created", checks.certificates.Texts); // no badge of its own (decision 2026-10-25)
        Assert.DoesNotContain("Used by", checks.certificates.Texts);
        Assert.Equal(0, checks.certificates.Primaries); // toolbar buttons are flat (Fluent, decision 2026-10-25)

        // Policies: the grouped table, no certificate list.
        Assert.Equal(4, checks.policies.Groups); // Issuing, Server, 802.1X access, Expiry warnings (no EST enrollment policies here)
        Assert.Equal(0, checks.policies.Lists);
        Assert.Equal(0, checks.policies.Primaries); // Create is a flat toolbar button
        Assert.Contains("Search policies", checks.policies.Texts);
        Assert.All(checks.certificates.Texts.Concat(checks.policies.Texts), t => Assert.False(t?.EndsWith("...", StringComparison.Ordinal) == true || t?.EndsWith('…') == true, t));
    }
}
