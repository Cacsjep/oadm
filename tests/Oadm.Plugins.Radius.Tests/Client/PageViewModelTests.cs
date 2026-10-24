using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Client;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Radius.Tests.Client;

/// <summary>The page's view models against the fake server: the cards, forms with errors below the fields, lists, events.</summary>
public sealed class PageViewModelTests
{
    private readonly FakeRadiusBackend _server = new();
    private readonly FakePageContext _ctx;

    public PageViewModelTests()
    {
        _ctx = new FakePageContext(_server);
    }

    private sealed class FakeDialogs : ISwitchDialogs, ILogDialogs, IAdvancedDialog, IPolicyDialogs
    {
        public Func<SwitchDialogViewModel, Task<bool>>? Switch { get; set; }

        public Func<AdvancedViewModel, Task<bool>>? Advanced { get; set; }

        public Func<PolicyEditorViewModel, Task<bool>>? Policy { get; set; }

        public List<CertificateDetails> Certificates { get; } = [];

        public Task<bool> ShowSwitchAsync(SwitchDialogViewModel dialog) => Switch?.Invoke(dialog) ?? Task.FromResult(false);

        public Task<bool> ShowAdvancedAsync(AdvancedViewModel dialog) => Advanced?.Invoke(dialog) ?? Task.FromResult(false);

        public Task<bool> ShowPolicyAsync(PolicyEditorViewModel editor) => Policy?.Invoke(editor) ?? Task.FromResult(false);

        public Task ShowCertificateAsync(CertificateDetails certificate)
        {
            Certificates.Add(certificate);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task The_cards_read_the_state_the_trusted_cas_the_switches_and_the_policies()
    {
        using var vm = new RadiusViewModel(_ctx);
        await vm.LoadServerAsync(CancellationToken.None);

        Assert.Equal(("Running on port 1812", true), (vm.StatusText, vm.StatusOk));
        Assert.StartsWith("Server certificate valid until ", vm.CertificateText, StringComparison.Ordinal);
        Assert.Equal("eth0", vm.Settings.SelectedInterface?.Id);
        Assert.Equal(("1812", "5,000"), (vm.Settings.Port, vm.Settings.LogKept));
        Assert.Equal(["Core switch", "Floor 2", "Gate house", "Parking"], vm.Switches.Rows.Select(r => r.Name));
        Assert.Equal(("Wrong secret", true), (vm.Switches.Rows[1].State, vm.Switches.Rows[1].IsError));
        Assert.Equal("Building B", vm.Switches.Rows[2].Site);
        Assert.Equal(["OADM CA", "Axis device ID", "Contoso Root"], vm.TrustedCas.Cas.Select(c => c.Name));
        Assert.Equal([true, true, false], vm.TrustedCas.Cas.Select(c => c.IsTrusted));
        Assert.StartsWith("With its previous CA, valid until ", vm.TrustedCas.Cas[0].Detail, StringComparison.Ordinal);
        Assert.Equal(["Building B cameras", "PTZ", "Storage room"], vm.Policies.Rows.Select(r => r.Name));
        Assert.Equal(["VLAN 30", "VLAN 20", "Block"], vm.Policies.Rows.Select(r => r.Kind));
        Assert.Equal(["Site Building B", "Tag PTZ, except 1", "2 devices"], vm.Policies.Rows.Select(r => r.Devices));
        Assert.True(vm.Policies.HasVlanPolicies);
        Assert.Equal("99", vm.Policies.DefaultVlan);
        Assert.Null(vm.Settings.ErrorOf(nameof(SettingsViewModel.Port))); // an untouched form shows no error
        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.TrustedCas.SaveCommand.CanExecute(null)); // nothing changed
        Assert.False(vm.Policies.SaveDefaultVlanCommand.CanExecute(null));
    }

    [Fact]
    public async Task Activate_reads_every_card_the_sessions_and_the_log_once()
    {
        using var vm = new RadiusViewModel(_ctx);
        await vm.LoadAllAsync(CancellationToken.None);
        Assert.Equal(1, _server.CountOf(RadiusMethods.GetState));
        Assert.Equal(1, _server.CountOf(RadiusMethods.GetTrustedCas));
        Assert.Equal(1, _server.CountOf(RadiusMethods.GetPolicies));
        Assert.Equal(1, _server.CountOf(RadiusMethods.ListSessions));
        Assert.Equal(1, _server.CountOf(RadiusMethods.ListLog));
        Assert.Equal(6, vm.Sessions.Rows.Count);
        Assert.Equal(5, vm.Log.Rows.Count);
    }

    [Fact]
    public async Task Errors_show_below_the_edited_field_and_Save_says_why_it_is_off()
    {
        using var vm = new RadiusViewModel(_ctx);
        await vm.LoadServerAsync(CancellationToken.None);

        vm.Settings.Port = "0";
        Assert.Equal("Use 1 to 65535.", vm.Settings.ErrorOf(nameof(SettingsViewModel.Port)));
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.Equal("Use 1 to 65535.", vm.SaveTip);
        vm.Settings.Port = "1812";
        vm.Settings.LogKept = "999";
        Assert.Equal("Keep from 1,000 to 50,000 entries.", vm.Settings.ErrorOf(nameof(SettingsViewModel.LogKept)));
        vm.Settings.LogKept = "50,000";
        Assert.True(vm.SaveCommand.CanExecute(null));
        vm.Policies.DefaultVlan = "4095";
        Assert.Equal(RadiusRules.VlanRange, vm.Policies.ErrorOf(nameof(PoliciesViewModel.DefaultVlan)));
        Assert.False(vm.Policies.SaveDefaultVlanCommand.CanExecute(null));
        Assert.True(vm.SaveCommand.CanExecute(null)); // another card's error never blocks Save
    }

    [Fact]
    public async Task Advanced_lists_the_old_suites_only_below_TLS_1_2_with_the_hint_and_keeps_one_suite_checked()
    {
        var dialogs = new FakeDialogs();
        using var vm = new RadiusViewModel(_ctx) { AdvancedDialog = dialogs };
        await vm.LoadServerAsync(CancellationToken.None);
        vm.Settings.Port = "1900"; // an unsaved edit of the card stays
        AdvancedViewModel? opened = null;
        dialogs.Advanced = async advanced =>
        {
            opened = advanced;
            Assert.Null(advanced.TlsHint);
            Assert.Equal("1,020", advanced.LargestPacket);
            Assert.Equal(9, advanced.CipherSuites.Count(s => s.IsListed));
            Assert.All(advanced.CipherSuites.Where(s => s.IsLegacy), s => Assert.False(s.IsListed));

            advanced.MinimumTls = AdvancedViewModel.TlsVersions[1]; // TLS 1.1
            Assert.Equal(AdvancedViewModel.OldTlsHint, advanced.TlsHint);
            Assert.All(advanced.CipherSuites.Where(s => s.IsLegacy), s => Assert.True(s.IsListed && s.IsChecked)); // checked when lowered

            advanced.MinimumTls = AdvancedViewModel.TlsVersions[3]; // TLS 1.3
            Assert.All(advanced.CipherSuites.Where(s => s.IsLegacy), s => Assert.False(s.IsListed));
            foreach (var suite in advanced.CipherSuites)
            {
                suite.IsChecked = false;
            }

            Assert.Equal(RadiusRules.CheckCipherSuite, advanced.CipherSuitesError);
            Assert.False(advanced.SaveCommand.CanExecute(null));
            advanced.ResetCipherSuitesCommand.Execute(null);
            Assert.Null(advanced.CipherSuitesError);
            advanced.LargestPacket = "1500";
            Assert.Equal(RadiusRules.FragmentRange, advanced.ErrorOf(nameof(AdvancedViewModel.LargestPacket)));
            Assert.False(advanced.SaveCommand.CanExecute(null));
            advanced.LargestPacket = "1,200";
            var closed = false;
            advanced.CloseRequested += (_, saved) => closed = saved;
            await advanced.SaveCommand.ExecuteAsync(null);
            return closed;
        };

        await vm.OpenAdvancedCommand.ExecuteAsync(null);

        Assert.NotNull(opened);
        var sent = _server.LastPayload<RadiusSettings>(RadiusMethods.SaveSettings);
        Assert.Equal(("1.3", 1200, 1812), (sent.MinimumTls, sent.LargestFragment, sent.Port)); // General as saved
        Assert.Equal(9, sent.CipherSuites.Count); // only the listed suites
        Assert.Equal("1900", vm.Settings.Port);

        await vm.SaveCommand.ExecuteAsync(null); // the card's Save keeps the saved Advanced settings
        sent = _server.LastPayload<RadiusSettings>(RadiusMethods.SaveSettings);
        Assert.Equal(("1.3", 1200, 1900), (sent.MinimumTls, sent.LargestFragment, sent.Port));
    }

    [Fact]
    public async Task A_refused_save_shows_the_error_below_its_field()
    {
        using var vm = new RadiusViewModel(_ctx);
        await vm.LoadServerAsync(CancellationToken.None);
        _server.Failures[RadiusMethods.SaveSettings] = new InvalidOperationException("Port 1812 is used by another program");
        vm.Settings.Port = "1813";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Port 1812 is used by another program", vm.Settings.ErrorOf(nameof(SettingsViewModel.Port)));
        Assert.Null(vm.ActionError);

        _server.Failures[RadiusMethods.SaveSettings] = new InvalidOperationException("OADM PRO is not active.");
        vm.Settings.Port = "1814"; // the server error of the port stays until the field is edited
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("OADM PRO is not active.", vm.ActionError);
    }

    [Fact]
    public async Task Trusted_cas_save_the_checked_ones_with_only_managed()
    {
        using var vm = new RadiusViewModel(_ctx);
        await vm.LoadServerAsync(CancellationToken.None);
        var contoso = vm.TrustedCas.Cas[2];
        contoso.IsTrusted = true;
        contoso.OnlyManaged = false;
        Assert.True(vm.TrustedCas.SaveCommand.CanExecute(null));
        vm.TrustedCas.Cas[1].IsTrusted = false;

        await vm.TrustedCas.SaveCommand.ExecuteAsync(null);

        var sent = _server.LastPayload<TrustedCaSettings>(RadiusMethods.SaveTrustedCas);
        Assert.Equal([new TrustedCaChoice("oadm", true), new TrustedCaChoice("imported:AB12", false)], sent.Cas);
        Assert.False(vm.TrustedCas.IsChanged);
        Assert.Equal([true, false, true], vm.TrustedCas.Cas.Select(c => c.IsTrusted));

        foreach (var ca in vm.TrustedCas.Cas)
        {
            ca.IsTrusted = false;
        }

        Assert.Equal("No CA is checked: no device passes.", vm.TrustedCas.SaveTip);
        _server.Failures[RadiusMethods.SaveTrustedCas] = new InvalidOperationException("Your role does not allow this.");
        await vm.TrustedCas.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Your role does not allow this.", vm.TrustedCas.Error);
    }

    [Fact]
    public async Task Add_policy_chooses_devices_with_the_group_picker_and_saves_the_whole_list()
    {
        var dialogs = new FakeDialogs();
        using var vm = new RadiusViewModel(_ctx);
        vm.Policies.Dialogs = dialogs;
        await vm.LoadServerAsync(CancellationToken.None);
        dialogs.Policy = async editor =>
        {
            Assert.Equal("Add policy", editor.Title);
            Assert.True(editor.Picker.IsGroup);
            Assert.Equal(["This server", "Building B"], editor.Picker.Sites.Select(s => s.Name));
            Assert.Contains(editor.Picker.Tags, t => t.Name == "Lobby"); // the server's tag choices
            Assert.False(editor.CanSave);
            editor.Name = "ptz";
            Assert.Equal(RadiusRules.PolicyNameTaken, editor.ErrorOf(nameof(PolicyEditorViewModel.Name)));
            editor.Name = "Lobby";
            editor.Vlan = "5000";
            Assert.Equal(RadiusRules.VlanRange, editor.ErrorOf(nameof(PolicyEditorViewModel.Vlan)));
            editor.Vlan = "40";
            Assert.Equal(RadiusRules.ChoosePolicyDevices, editor.SaveBlockedReason);
            editor.Picker.Tags.Single(t => t.Name == "Lobby").IsChecked = true;
            Assert.True(editor.CanSave);
            return await editor.SaveAsync();
        };

        await vm.Policies.AddCommand.ExecuteAsync(null);

        var sent = _server.LastPayload<PolicySettings>(RadiusMethods.SavePolicies);
        Assert.Equal(["Building B cameras", "PTZ", "Storage room", "Lobby"], sent.Policies.Select(p => p.Name));
        Assert.Equal((PolicyKinds.Vlan, (int?)40, "Lobby"), (sent.Policies[3].Kind, sent.Policies[3].Vlan, sent.Policies[3].Devices.Tags.Single()));
        Assert.Equal(99, sent.DefaultVlan);
        Assert.Equal(4, vm.Policies.Rows.Count);
        Assert.Equal("Lobby", vm.Policies.Selected?.Name);
        Assert.Equal(0, _server.CountOf(RadiusMethods.CountRevocations));
    }

    [Fact]
    public async Task A_revoke_policy_asks_with_the_count_and_a_block_policy_does_not()
    {
        var dialogs = new FakeDialogs();
        using var vm = new RadiusViewModel(_ctx);
        vm.Policies.Dialogs = dialogs;
        await vm.LoadServerAsync(CancellationToken.None);
        var asked = new List<string>();
        var answer = false;
        dialogs.Policy = async editor =>
        {
            Assert.Equal(["VLAN", "Block", "Revoke certificates"], editor.KindChoices.Select(k => k.Label));
            editor.Name = "Stolen";
            editor.Kind = editor.KindChoices[2];
            Assert.False(editor.IsVlan);
            Assert.Equal(PolicyEditorViewModel.RevokeHint, editor.KindHint);
            editor.Picker.Visible[0].IsChecked = true;
            editor.ConfirmRevoke = (_, message) =>
            {
                asked.Add(message);
                return Task.FromResult(answer);
            };
            return await editor.SaveAsync();
        };

        await vm.Policies.AddCommand.ExecuteAsync(null); // No at the question: nothing saved
        Assert.Equal("Saving revokes 7 IEEE 802.1X certificates of these devices in PKI, and every new one while the policy exists. This cannot be undone.", Assert.Single(asked));
        Assert.Equal(0, _server.CountOf(RadiusMethods.SavePolicies));

        answer = true;
        await vm.Policies.AddCommand.ExecuteAsync(null);
        Assert.Equal(PolicyKinds.Revoke, _server.LastPayload<PolicySettings>(RadiusMethods.SavePolicies).Policies[^1].Kind);
        Assert.Equal("Revoke certificates", vm.Policies.Rows[^1].Kind);

        // Editing the revoke policy without changing its devices revokes nothing new: no question.
        asked.Clear();
        vm.Policies.Selected = vm.Policies.Rows[^1];
        dialogs.Policy = editor =>
        {
            editor.Name = "Stolen cameras";
            return editor.SaveAsync();
        };
        await vm.Policies.EditCommand.ExecuteAsync(null);
        Assert.Empty(asked);
        Assert.Equal("Stolen cameras", vm.Policies.Rows[^1].Name);
    }

    [Fact]
    public async Task Without_PKI_permission_the_editor_offers_no_revoke_policy()
    {
        _ctx.Permissions = [PermissionKeys.Of(RadiusPluginInfo.PluginId, PermissionKeys.Manage)];
        using var vm = new RadiusViewModel(_ctx);
        await vm.LoadServerAsync(CancellationToken.None);
        Assert.Equal(["VLAN", "Block"], vm.Policies.NewEditor(null).KindChoices.Select(k => k.Label));
    }

    [Fact]
    public async Task Policies_move_and_delete_with_one_save_each_and_the_default_vlan_saves_alone()
    {
        using var vm = new RadiusViewModel(_ctx);
        await vm.LoadServerAsync(CancellationToken.None);
        var policies = vm.Policies;
        policies.Selected = policies.Rows[0];
        Assert.False(policies.MoveUpCommand.CanExecute(null));
        await policies.MoveDownCommand.ExecuteAsync(null);
        Assert.Equal(["PTZ", "Building B cameras", "Storage room"], _server.LastPayload<PolicySettings>(RadiusMethods.SavePolicies).Policies.Select(p => p.Name));
        Assert.Equal("Building B cameras", policies.Selected?.Name);

        policies.DefaultVlan = string.Empty;
        await policies.SaveDefaultVlanCommand.ExecuteAsync(null);
        Assert.Null(_server.LastPayload<PolicySettings>(RadiusMethods.SavePolicies).DefaultVlan);

        policies.Selected = policies.Rows[2];
        await policies.DeleteCommand.ExecuteAsync(null);
        Assert.StartsWith("Delete the policy Storage room?", _ctx.Confirms[^1], StringComparison.Ordinal);
        Assert.Equal(2, policies.Rows.Count);
        policies.Selected = policies.Rows[0];
        await policies.DeleteCommand.ExecuteAsync(null);
        policies.Selected = policies.Rows[0];
        await policies.DeleteCommand.ExecuteAsync(null);
        Assert.False(policies.HasVlanPolicies); // the Default VLAN hides without a VLAN policy
        Assert.Equal("No policies yet. Add one to give devices a VLAN or to block them.", policies.EmptyText);
    }

    [Fact]
    public async Task Add_switch_checks_the_fields_generates_a_secret_once_and_saves()
    {
        var dialogs = new FakeDialogs();
        using var vm = new RadiusViewModel(_ctx);
        vm.Switches.Dialogs = dialogs;
        await vm.LoadServerAsync(CancellationToken.None);
        SwitchDialogViewModel? shown = null;
        dialogs.Switch = async dialog =>
        {
            shown = dialog;
            Assert.Equal("Add switch", dialog.Title);
            Assert.False(dialog.SaveCommand.CanExecute(null));
            Assert.Null(dialog.ErrorOf(nameof(SwitchDialogViewModel.Name))); // untouched
            dialog.Name = "Floor 2";
            Assert.Equal("Another switch has this name.", dialog.ErrorOf(nameof(SwitchDialogViewModel.Name)));
            dialog.Name = "Floor 3";
            dialog.Address = "10.0.0.2";
            Assert.Equal("Another switch has this address.", dialog.ErrorOf(nameof(SwitchDialogViewModel.Address)));
            dialog.Address = "bad host";
            Assert.Equal("Enter an IP address or a host name.", dialog.ErrorOf(nameof(SwitchDialogViewModel.Address)));
            dialog.Address = "10.0.3.2";
            dialog.Secret = "short";
            Assert.Equal("Use 16 characters or more.", dialog.ErrorOf(nameof(SwitchDialogViewModel.Secret)));
            Assert.False(dialog.ShowSecret);
            await dialog.GenerateCommand.ExecuteAsync(null);
            Assert.Equal(32, dialog.Secret.Length);
            Assert.True(dialog.ShowSecret); // shown once to copy
            Assert.StartsWith("Copy it now", dialog.SecretHint, StringComparison.Ordinal);
            Assert.Equal("RADIUS server for this switch: 10.0.0.17, port 1812", dialog.RadiusServerHint);
            dialog.Site = dialog.SiteChoices[1];
            Assert.Equal("RADIUS server for this switch: the address of the site's OADM Site Node, port 1812", dialog.RadiusServerHint);
            dialog.Site = dialog.SiteChoices[0];
            Assert.True(dialog.SaveCommand.CanExecute(null));
            var closed = false;
            dialog.CloseRequested += (_, saved) => closed = saved;
            await dialog.SaveCommand.ExecuteAsync(null);
            return closed;
        };

        await vm.Switches.AddCommand.ExecuteAsync(null);

        Assert.NotNull(shown);
        var sent = _server.LastPayload<SaveSwitchRequest>(RadiusMethods.SaveSwitch);
        Assert.Equal((null, "Floor 3", "10.0.3.2", (Guid?)null, "k7Qw9zR2mX4pL8vN3bT6yH1cF5jD0sAe"), (sent.Id, sent.Name, sent.Address, sent.SiteId, sent.Secret));
        Assert.Equal(5, vm.Switches.Rows.Count); // read again
    }

    [Fact]
    public async Task Edit_keeps_the_secret_when_empty_and_shows_where_the_switch_sends_its_requests()
    {
        var dialogs = new FakeDialogs();
        using var vm = new RadiusViewModel(_ctx);
        vm.Switches.Dialogs = dialogs;
        await vm.LoadServerAsync(CancellationToken.None);
        vm.Switches.Selected = vm.Switches.Rows.Single(r => r.Name == "Gate house");
        dialogs.Switch = async dialog =>
        {
            Assert.Equal(("Edit switch", "Gate house", "192.168.1.2", string.Empty), (dialog.Title, dialog.Name, dialog.Address, dialog.Secret));
            Assert.Equal("Leave empty to keep the current secret.", dialog.SecretHint);
            Assert.Equal("Building B", dialog.Site.Label);
            Assert.Equal("RADIUS server for this switch: 192.168.1.5, port 1812", dialog.RadiusServerHint);
            Assert.True(dialog.SaveCommand.CanExecute(null));
            dialog.Name = "Gate";
            await dialog.SaveCommand.ExecuteAsync(null);
            return true;
        };

        await vm.Switches.EditCommand.ExecuteAsync(null);

        var sent = _server.LastPayload<SaveSwitchRequest>(RadiusMethods.SaveSwitch);
        Assert.Null(sent.Secret);
        Assert.Equal(FakeRadiusBackend.BuildingB, sent.SiteId);
        Assert.Contains(vm.Switches.Rows, r => r.Name == "Gate");
    }

    [Fact]
    public async Task A_refused_switch_shows_the_reason_below_its_field()
    {
        using var vm = new RadiusViewModel(_ctx);
        await vm.LoadServerAsync(CancellationToken.None);
        var dialog = vm.Switches.NewDialog(null);
        dialog.Name = "New";
        dialog.Address = "switch-new.example.com";
        dialog.Secret = new string('s', 20);
        _server.Failures[RadiusMethods.SaveSwitch] = new InvalidOperationException("The host name switch-new.example.com cannot be resolved.");
        await dialog.SaveCommand.ExecuteAsync(null);
        Assert.Equal("The host name switch-new.example.com cannot be resolved.", dialog.ErrorOf(nameof(SwitchDialogViewModel.Address)));
        _server.Failures[RadiusMethods.SaveSwitch] = new InvalidOperationException("At most 500 switches.");
        dialog.Address = "10.0.9.2";
        await dialog.SaveCommand.ExecuteAsync(null);
        Assert.Equal(("Add switch", "At most 500 switches."), Assert.Single(_ctx.Messages));
    }

    [Fact]
    public async Task Delete_switch_asks_first()
    {
        using var vm = new RadiusViewModel(_ctx);
        await vm.LoadServerAsync(CancellationToken.None);
        vm.Switches.Selected = vm.Switches.Rows[0];
        _ctx.Confirm = false;
        await vm.Switches.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(0, _server.CountOf(RadiusMethods.DeleteSwitch));
        Assert.Contains("Core switch", Assert.Single(_ctx.Confirms), StringComparison.Ordinal);
        _ctx.Confirm = true;
        await vm.Switches.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.Switches.Rows.Count);
        Assert.False(vm.Switches.EditCommand.CanExecute(null));
    }

    [Fact]
    public async Task Sessions_follow_the_events_in_place_and_ended_ones_leave_after_10_seconds()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var vm = new RadiusViewModel(_ctx, time);
        var sessions = vm.Sessions;
        sessions.Apply([FakeRadiusBackend.Session(DateTime.UtcNow, "AC:CC:8E:00:00:01", "startingTls", "Starting TLS")]);
        Assert.Empty(sessions.Rows); // events before the tab opened are read with it

        await sessions.LoadAsync(CancellationToken.None);
        Assert.Equal(6, sessions.Rows.Count);
        Assert.Equal("4 sessions in progress", sessions.CountText); // two of the six ended
        var guess = sessions.Rows.First(r => r.Mac == "AC:CC:8E:12:34:02");
        Assert.True(guess.IsGuess);
        Assert.Equal("Sending server certificate: fragment 2 of 4", guess.State);
        Assert.Equal("GigabitEthernet1/0/2 (Ethernet)", guess.Port);
        Assert.True(guess.IsRunning);

        // The same session moves on (updated in place), a new one comes on top.
        var next = _server.Sessions[1] with { State = SessionStates.Authenticated, StateText = "Authenticated with VLAN 20", DeviceGuess = false, Vlan = 20 };
        var fresh = FakeRadiusBackend.Session(DateTime.UtcNow, "AC:CC:8E:00:00:09", "waitingForIdentity", "Waiting for identity");
        sessions.Apply([next, fresh]);
        Assert.Same(guess, sessions.Rows.Single(r => r.Id == next.Id));
        Assert.Equal(("Authenticated with VLAN 20", true, false), (guess.State, guess.IsOk, guess.IsGuess));
        Assert.Equal(fresh.Id, sessions.Rows[0].Id);
        Assert.Equal("4 sessions in progress", sessions.CountText);

        time.Advance(TimeSpan.FromSeconds(9));
        sessions.Tick();
        Assert.Equal(7, sessions.Rows.Count);
        time.Advance(TimeSpan.FromSeconds(2));
        sessions.Tick();
        Assert.Equal(4, sessions.Rows.Count); // the three ended rows left in one step
        Assert.All(sessions.Rows, r => Assert.False(r.IsEnded));
    }

    [Fact]
    public async Task Log_reads_on_open_searches_after_typing_stops_and_takes_live_rows()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var vm = new RadiusViewModel(_ctx, time);
        var log = vm.Log;
        await vm.Log.LoadAsync(CancellationToken.None);
        Assert.Equal(5, log.Rows.Count);
        Assert.Equal("1-5 of 5", log.PageText);
        var warned = log.Rows[1];
        Assert.Equal(("Authenticated", true, false), (warned.Result, warned.IsWarning, warned.IsOk));
        Assert.StartsWith("MAC address AC:CC:8E:65:43:21 does not match", warned.Reason, StringComparison.Ordinal);
        Assert.True(log.Rows[2].IsError);

        log.Search = "e";
        log.Search = "expired";
        Assert.Equal(1, _server.CountOf(RadiusMethods.ListLog));
        time.Advance(TimeSpan.FromMilliseconds(300));
        await Eventually.TrueAsync(() => log.Rows.Count == 1 && !log.IsReloading, "the searched page");
        Assert.Equal(2, _server.CountOf(RadiusMethods.ListLog));
        Assert.Equal("expired", _server.LastPayload<LogQuery>(RadiusMethods.ListLog).Search);

        // Live rows: only those that match the search join the page.
        vm.OnEvent(new Oadm.Sdk.Plugins.PluginEvent(RadiusTopics.Log, RadiusJson.Serialize(new[]
        {
            FakeRadiusBackend.LogEntry(DateTime.UtcNow, "AC:CC:8E:00:00:10", LogResults.Failed, "Client certificate expired on 2026-10-01", null, null, null),
            FakeRadiusBackend.LogEntry(DateTime.UtcNow, "AC:CC:8E:00:00:11", LogResults.Authenticated, null, null, 20, "10.0.0.11"),
        })));
        Assert.Equal(2, log.Rows.Count);
        Assert.Equal("AC:CC:8E:00:00:10", log.Rows[0].Mac);

        vm.OnEvent(new Oadm.Sdk.Plugins.PluginEvent(RadiusTopics.LogCleared, "{}"));
        Assert.Empty(log.Rows);
        Assert.Equal("No entry matches the search.", log.EmptyText);
    }

    [Fact]
    public async Task View_certificate_and_Delete_all_of_the_log()
    {
        var dialogs = new FakeDialogs();
        using var vm = new RadiusViewModel(_ctx);
        vm.Log.Dialogs = dialogs;
        await vm.Log.LoadAsync(CancellationToken.None);
        Assert.False(vm.Log.ViewCertificateCommand.CanExecute(null));
        vm.Log.Selected = vm.Log.Rows[0];
        await vm.Log.ViewCertificateCommand.ExecuteAsync(null);
        var shown = Assert.Single(dialogs.Certificates);
        Assert.Equal("Certificate · 10.0.0.45 · IEEE 802.1X", shown.Title);
        Assert.Contains("Client authentication", shown.Uses, StringComparison.Ordinal);
        Assert.Equal("DNS axis-accc8e123405.local", shown.Names);

        Assert.True(vm.Log.DeleteAllCommand.CanExecute(null));
        _ctx.Confirm = false;
        await vm.Log.DeleteAllCommand.ExecuteAsync(null);
        Assert.Equal(0, _server.CountOf(RadiusMethods.DeleteLog));
        _ctx.Confirm = true;
        await vm.Log.DeleteAllCommand.ExecuteAsync(null);
        Assert.Equal(1, _server.CountOf(RadiusMethods.DeleteLog));
        Assert.Empty(vm.Log.Rows);
        Assert.Equal("No authentications yet. Add a switch and turn on 802.1X on its ports.", vm.Log.EmptyText);
        Assert.False(vm.Log.DeleteAllCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_group_user_sees_sessions_and_log_only_and_changes_nothing()
    {
        _ctx.AllDevices = false;
        using var vm = new RadiusViewModel(_ctx);
        Assert.False(vm.HasAllDevices);
        Assert.False(vm.MayManage);
        await vm.LoadAllAsync(CancellationToken.None);
        Assert.Equal(0, _server.CountOf(RadiusMethods.GetState));
        Assert.Equal(0, _server.CountOf(RadiusMethods.GetPolicies));
        Assert.Equal(0, _server.CountOf(RadiusMethods.GetTrustedCas));
        Assert.Equal(1, _server.CountOf(RadiusMethods.ListLog));
        Assert.False(vm.Log.MayManage);
        Assert.False(vm.Log.DeleteAllCommand.CanExecute(null));
        Assert.False(vm.Switches.AddCommand.CanExecute(null));
        Assert.False(vm.Policies.AddCommand.CanExecute(null));
        Assert.False(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_role_without_manage_reads_but_changes_nothing_and_a_lost_scope_empties_the_cards()
    {
        _ctx.Manage = false;
        using var vm = new RadiusViewModel(_ctx);
        await vm.LoadServerAsync(CancellationToken.None);
        Assert.False(vm.MayManage);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.Switches.AddCommand.CanExecute(null));
        Assert.False(vm.Policies.AddCommand.CanExecute(null));
        Assert.False(vm.Policies.EditCommand.CanExecute(null));
        vm.TrustedCas.Cas[2].IsTrusted = true;
        Assert.False(vm.TrustedCas.SaveCommand.CanExecute(null));
        Assert.Equal("No switches yet.", new SwitchesViewModel(_ctx, () => false, () => null, () => []).EmptyText);

        _ctx.Manage = true;
        _ctx.RaisePermissionsChanged();
        Assert.True(vm.MayManage);
        Assert.True(vm.SaveCommand.CanExecute(null));

        _ctx.AllDevices = false;
        _ctx.RaisePermissionsChanged();
        Assert.False(vm.MayManage);
        Assert.False(vm.HasAllDevices);
        Assert.Empty(vm.Policies.Rows); // nothing read with all devices stays
        Assert.Empty(vm.TrustedCas.Cas);
        Assert.Empty(vm.Switches.Rows);
    }

    [Fact]
    public async Task Live_state_and_switch_events_update_the_server_tab()
    {
        using var vm = new RadiusViewModel(_ctx);
        await vm.LoadServerAsync(CancellationToken.None);
        vm.Settings.Port = "1900"; // an unsaved edit stays
        vm.OnEvent(new Oadm.Sdk.Plugins.PluginEvent(RadiusTopics.State, RadiusJson.Serialize(_server.State with
        {
            Running = false,
            StatusText = "Port 1812 is used by another program",
            StatusKind = Oadm.Sdk.Network.ServiceStatus.Error,
            SitesNotAllowed = [new SiteRef(FakeRadiusBackend.BuildingB, "Building B")],
        })));
        Assert.Equal(("Port 1812 is used by another program", false), (vm.StatusText, vm.StatusOk));
        Assert.Equal("Building B: allow the 802.1X server in the OADM Site Node app", vm.SitesText);
        Assert.Equal("1900", vm.Settings.Port);

        _server.Switches.RemoveAt(0);
        vm.OnEvent(new Oadm.Sdk.Plugins.PluginEvent(RadiusTopics.Switches, RadiusJson.Serialize(new SwitchList(_server.Switches))));
        Assert.Equal(3, vm.Switches.Rows.Count); // the event carries the list
    }

    [Fact]
    public async Task A_removed_session_leaves_at_once_and_sites_come_from_the_devices_without_listSites()
    {
        using var vm = new RadiusViewModel(_ctx);
        await vm.Sessions.LoadAsync(CancellationToken.None);
        var open = _server.Sessions[0];
        vm.Sessions.Apply([open with { Removed = true }]);
        Assert.Equal(5, vm.Sessions.Rows.Count);
        Assert.DoesNotContain(vm.Sessions.Rows, r => r.Id == open.Id);
        Assert.Equal("3 sessions in progress", vm.Sessions.CountText);
        vm.Sessions.Apply([open with { Removed = true }]); // again: nothing changes
        Assert.Equal(5, vm.Sessions.Rows.Count);

        _server.Failures[RadiusPageMethods.ListSites] = new ArgumentException("Unknown method 'listSites'.");
        await vm.LoadServerAsync(CancellationToken.None);
        var dialog = vm.Switches.NewDialog(null);
        Assert.Equal(["This server", "Building B"], dialog.SiteChoices.Select(c => c.Label));
        Assert.Equal(["This server", "Building B"], vm.Policies.NewEditor(null).Picker.Sites.Select(c => c.Name));
    }

    [Fact]
    public void The_page_reads_the_contract_names_and_sends_the_reason_codes()
    {
        var json = RadiusJson.Serialize(new SwitchRow { State = SwitchStates.WrongSecret });
        Assert.Contains("\"state\":\"wrongSecret\"", json, StringComparison.Ordinal);
        Assert.Contains("\"minimumTls\":\"1.2\"", RadiusJson.Serialize(new RadiusSettings()), StringComparison.Ordinal);
        Assert.Equal("Wrong secret", new SwitchRowViewModel(RadiusJson.Deserialize<SwitchRow>(json)).State);
        Assert.Contains("\"kind\":\"block\"", RadiusJson.Serialize(new RadiusPolicy { Kind = PolicyKinds.Block }), StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"axisDeviceId\"", RadiusJson.Serialize(new TrustedCaRow { Kind = CertificateAuthorityKind.AxisDeviceId }), StringComparison.Ordinal);
        Assert.Equal(["TLS 1.0", "TLS 1.1", "TLS 1.2", "TLS 1.3"], AdvancedViewModel.TlsVersions.Select(v => v.Label));
    }
}
