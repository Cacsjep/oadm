using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Client;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Radius.Tests.Client;

/// <summary>The page's view models against the fake server: tabs, forms with errors below the fields, lists, events.</summary>
public sealed class PageViewModelTests
{
    private readonly FakeRadiusBackend _server = new();
    private readonly FakePageContext _ctx;

    public PageViewModelTests()
    {
        _ctx = new FakePageContext(_server);
    }

    private sealed class FakeDialogs : ISwitchDialogs, ILogDialogs, IRevokeDialogs
    {
        public Func<SwitchDialogViewModel, Task<bool>>? Switch { get; set; }

        public List<CertificateDetails> Certificates { get; } = [];

        public IReadOnlyList<IssuedCertificateRow>? Chosen { get; set; }

        public RevokePickerViewModel? Picker { get; private set; }

        public RevocationReason? Reason { get; set; } = RevocationReason.KeyCompromised;

        public List<(string Title, string Message)> Asked { get; } = [];

        public Task<bool> ShowSwitchAsync(SwitchDialogViewModel dialog) => Switch?.Invoke(dialog) ?? Task.FromResult(false);

        public Task ShowCertificateAsync(CertificateDetails certificate)
        {
            Certificates.Add(certificate);
            return Task.CompletedTask;
        }

        public async Task<IReadOnlyList<IssuedCertificateRow>?> ChooseCertificatesAsync(RevokePickerViewModel picker)
        {
            Picker = picker;
            await picker.LoadAsync();
            return Chosen ?? (picker.Rows.Count > 0 ? [picker.Rows[0]] : null);
        }

        public Task<RevocationReason?> ConfirmRevokeAsync(string title, string message)
        {
            Asked.Add((title, message));
            return Task.FromResult(Reason);
        }
    }

    [Fact]
    public async Task The_server_tab_reads_the_state_the_switches_and_the_vlans_and_nothing_else()
    {
        using var vm = new RadiusViewModel(_ctx);
        await vm.LoadServerAsync(CancellationToken.None);

        Assert.True(vm.IsServerTab);
        Assert.Equal(("Running on port 1812", true), (vm.StatusText, vm.StatusOk));
        Assert.StartsWith("Server certificate valid until ", vm.CertificateText, StringComparison.Ordinal);
        Assert.Equal("eth0", vm.Settings.SelectedInterface?.Id);
        Assert.Equal(("1812", "5,000", "1000"), (vm.Settings.Port, vm.Settings.LogKept, vm.Settings.LargestFragment));
        Assert.Equal(4, vm.Switches.Rows.Count);
        Assert.Equal(["Core switch", "Floor 2", "Gate house", "Parking"], vm.Switches.Rows.Select(r => r.Name));
        Assert.Equal(("Wrong secret", true), (vm.Switches.Rows[1].State, vm.Switches.Rows[1].IsError));
        Assert.Equal("Building B", vm.Switches.Rows[2].Site);
        Assert.Equal("This server", vm.Switches.Rows[0].Site);
        Assert.Equal(2, vm.Vlans.Rules.Count);
        Assert.Equal(("Site", "Building B", "30"), (vm.Vlans.Rules[0].Kind.Label, vm.Vlans.Rules[0].Reference?.Label, vm.Vlans.Rules[0].Vlan));
        Assert.Equal("99", vm.Vlans.DefaultVlan);
        Assert.Equal(0, _server.CountOf(RadiusMethods.ListSessions)); // nothing of another tab is read
        Assert.Equal(0, _server.CountOf(RadiusMethods.ListLog));
        Assert.Equal(0, _server.CountOf(RadiusMethods.ListRevoked));
        Assert.Null(vm.Settings.ErrorOf(nameof(SettingsViewModel.Port))); // an untouched form shows no error
        Assert.True(vm.SaveCommand.CanExecute(null));
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
        vm.Settings.LargestFragment = "1500";
        Assert.Equal("Use 500 to 1,400 bytes.", vm.Settings.ErrorOf(nameof(SettingsViewModel.LargestFragment)));
        vm.Settings.LargestFragment = "1400";
        vm.Vlans.DefaultVlan = "4095";
        Assert.Equal(RadiusRules.VlanRange, vm.Vlans.ErrorOf(nameof(VlansViewModel.DefaultVlan)));
        Assert.False(vm.SaveCommand.CanExecute(null));
        vm.Vlans.DefaultVlan = string.Empty;
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Advanced_lists_the_old_suites_only_below_TLS_1_2_with_the_hint_and_keeps_one_suite_checked()
    {
        using var vm = new RadiusViewModel(_ctx);
        await vm.LoadServerAsync(CancellationToken.None);
        var settings = vm.Settings;
        Assert.False(settings.ShowAdvanced); // collapsed
        Assert.Null(settings.TlsHint);
        Assert.Equal(9, settings.CipherSuites.Count(s => s.IsListed));
        Assert.All(settings.CipherSuites.Where(s => s.IsLegacy), s => Assert.False(s.IsListed));

        settings.MinimumTls = SettingsViewModel.TlsVersions[1]; // TLS 1.1
        Assert.Equal(SettingsViewModel.OldTlsHint, settings.TlsHint);
        Assert.All(settings.CipherSuites.Where(s => s.IsLegacy), s => Assert.True(s.IsListed && s.IsChecked)); // checked when lowered

        settings.MinimumTls = SettingsViewModel.TlsVersions[3]; // TLS 1.3
        Assert.Null(settings.TlsHint);
        Assert.All(settings.CipherSuites.Where(s => s.IsLegacy), s => Assert.False(s.IsListed));

        foreach (var suite in settings.CipherSuites)
        {
            suite.IsChecked = false;
        }

        Assert.Equal(RadiusRules.CheckCipherSuite, settings.CipherSuitesError);
        Assert.False(vm.SaveCommand.CanExecute(null));
        settings.ResetCipherSuitesCommand.Execute(null);
        Assert.Null(settings.CipherSuitesError);
        Assert.Equal(9, settings.CipherSuites.Count(s => s.IsChecked));
        Assert.True(vm.SaveCommand.CanExecute(null));

        await vm.SaveCommand.ExecuteAsync(null);
        var sent = _server.LastPayload<RadiusSettings>(RadiusMethods.SaveSettings);
        Assert.Equal("1.3", sent.MinimumTls);
        Assert.Equal(9, sent.CipherSuites.Count); // only the listed suites
        Assert.DoesNotContain(0xC013, sent.CipherSuites);
    }

    [Fact]
    public async Task Save_sends_general_and_vlans_in_rule_order()
    {
        using var vm = new RadiusViewModel(_ctx);
        await vm.LoadServerAsync(CancellationToken.None);
        vm.Settings.Port = "11812";
        vm.Settings.LogKept = "20,000";
        vm.Vlans.MoveDownCommand.Execute(vm.Vlans.Rules[0]);
        vm.Vlans.AddRuleCommand.Execute(null);
        var added = vm.Vlans.Rules[^1];
        Assert.False(vm.SaveCommand.CanExecute(null)); // the new rule has no VLAN yet
        added.Vlan = "4095";
        Assert.Equal("Use 1 to 4094.", added.ErrorOf(nameof(VlanRuleViewModel.Vlan)));
        added.Kind = VlansViewModel.Kinds[1];
        added.Reference = added.References.Single(r => r.Value == "Outdoor");
        added.Vlan = "40";
        vm.Vlans.DefaultVlan = string.Empty;
        Assert.True(vm.SaveCommand.CanExecute(null));

        await vm.SaveCommand.ExecuteAsync(null);

        var settings = _server.LastPayload<RadiusSettings>(RadiusMethods.SaveSettings);
        Assert.Equal((11812, 20000, true, (int?)null), (settings.Port, settings.LogKept, settings.VlansEnabled, settings.DefaultVlan));
        var vlans = _server.LastPayload<VlanSettings>(RadiusMethods.SaveVlans);
        Assert.Equal(["PTZ", "Building B", "Outdoor"], vlans.Rules.Select(r => r.RefName));
        Assert.Equal([20, 30, 40], vlans.Rules.Select(r => r.Vlan));
        Assert.Equal(VlanRuleKinds.Site, vlans.Rules[1].Kind);
        Assert.Equal(FakeRadiusBackend.BuildingB.ToString(), vlans.Rules[1].RefId);
        Assert.Null(vm.ActionError);
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
        Assert.Equal(0, _server.CountOf(RadiusMethods.SaveVlans)); // nothing more after a refusal

        _server.Failures[RadiusMethods.SaveSettings] = new InvalidOperationException("OADM PRO is not active.");
        vm.Settings.Port = "1814"; // the server error of the port stays until the field is edited
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("OADM PRO is not active.", vm.ActionError);
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

        await vm.ShowSessionsTabCommand.ExecuteAsync(null);
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
        await vm.ShowLogTabCommand.ExecuteAsync(null);
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
        await vm.ShowLogTabCommand.ExecuteAsync(null);
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
    public async Task Revoked_lists_the_revocations_and_Revoke_chooses_from_the_certificate_list_with_a_reason()
    {
        var dialogs = new FakeDialogs { Reason = null };
        using var vm = new RadiusViewModel(_ctx);
        vm.Revoked.Dialogs = dialogs;
        await vm.ShowRevokedTabCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Revoked.Rows.Count);
        Assert.Equal(("10.0.0.49", "7D FD 05 49 A1", "Key compromised", "admin"), (vm.Revoked.Rows[0].Device, vm.Revoked.Rows[0].SerialNumber, vm.Revoked.Rows[0].Reason, vm.Revoked.Rows[0].RevokedBy));
        Assert.Equal("Device (removed)", vm.Revoked.Rows[1].Device);
        vm.Revoked.Search = "replaced";
        Assert.Single(vm.Revoked.Rows);
        vm.Revoked.Search = "7dfd05";
        Assert.Equal("10.0.0.49", Assert.Single(vm.Revoked.Rows).Device);
        vm.Revoked.Search = string.Empty;

        await vm.Revoked.RevokeCommand.ExecuteAsync(null); // Cancel at the reason
        Assert.NotNull(dialogs.Picker);
        Assert.Equal("IEEE 802.1X", dialogs.Picker.UseFilter.Label); // the choice starts with the 802.1X certificates
        Assert.Equal(12, dialogs.Picker.Total);
        Assert.Equal(("Revoke the certificate of 10.0.0.40?", IssuedCertificateTexts.ConfirmMessageOne), Assert.Single(dialogs.Asked));
        Assert.Equal(0, _server.CountOf(RadiusMethods.Revoke));

        dialogs.Reason = RevocationReason.DeviceReplaced;
        await vm.Revoked.RevokeCommand.ExecuteAsync(null);
        var sent = _server.LastPayload<RevokeRequest>(RadiusMethods.Revoke);
        Assert.Equal(["5A000001"], sent.Serials);
        Assert.Equal(RevokeReasons.DeviceReplaced, sent.Reason);
        Assert.Equal(3, vm.Revoked.Rows.Count); // read again

        await vm.Revoked.RevokeCommand.ExecuteAsync(null); // the same again: nothing new
        Assert.Equal(("Revoke", IssuedCertificateTexts.NothingRevoked), Assert.Single(_ctx.Messages));
    }

    [Fact]
    public async Task The_certificate_choice_takes_only_valid_certificates()
    {
        var picker = new RevokePickerViewModel(_ctx);
        await picker.LoadAsync();
        Assert.False(picker.ChooseCommand.CanExecute(null));
        Assert.Equal("Choose the certificates to revoke.", picker.ChoiceText);
        picker.SetSelection(picker.Rows.Where(r => r.Source.State == IssuedCertificateState.Revoked));
        Assert.False(picker.ChooseCommand.CanExecute(null));
        Assert.Equal("Choose a certificate that is still valid.", picker.RevokeTip);
        picker.SetSelection(picker.Rows.Take(4));
        Assert.Equal("4 certificates chosen", picker.ChoiceText);
        IReadOnlyList<IssuedCertificateRow>? chosen = null;
        picker.CloseRequested += (_, rows) => chosen = rows;
        picker.ChooseCommand.Execute(null);
        Assert.Equal(4, chosen?.Count);
        Assert.Contains(chosen!, r => r.IsWarning); // expires soon: still revocable
    }

    [Fact]
    public async Task A_group_user_sees_sessions_and_log_only_and_changes_nothing()
    {
        _ctx.AllDevices = false;
        using var vm = new RadiusViewModel(_ctx);
        Assert.Equal(RadiusTab.Sessions, vm.Tab);
        Assert.False(vm.HasAllDevices);
        Assert.False(vm.MayManage);
        await vm.ShowServerTabCommand.ExecuteAsync(null);
        await vm.ShowRevokedTabCommand.ExecuteAsync(null);
        Assert.Equal(RadiusTab.Sessions, vm.Tab);
        Assert.Equal(0, _server.CountOf(RadiusMethods.GetState));
        Assert.Equal(0, _server.CountOf(RadiusMethods.ListRevoked));

        await vm.ShowLogTabCommand.ExecuteAsync(null);
        Assert.Equal(RadiusTab.Log, vm.Tab);
        Assert.False(vm.Log.MayManage);
        Assert.False(vm.Log.DeleteAllCommand.CanExecute(null));
        Assert.False(vm.Revoked.RevokeCommand.CanExecute(null));
        Assert.False(vm.Switches.AddCommand.CanExecute(null));
        Assert.False(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_role_without_manage_reads_but_changes_nothing_and_a_lost_scope_leaves_the_server_tab()
    {
        _ctx.Manage = false;
        using var vm = new RadiusViewModel(_ctx);
        await vm.LoadServerAsync(CancellationToken.None);
        Assert.False(vm.MayManage);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.Switches.AddCommand.CanExecute(null));
        Assert.False(vm.Vlans.AddRuleCommand.CanExecute(null));
        Assert.Equal("No switches yet.", new SwitchesViewModel(_ctx, () => false, () => null, () => []).EmptyText);

        _ctx.Manage = true;
        _ctx.RaisePermissionsChanged();
        Assert.True(vm.MayManage);
        Assert.True(vm.SaveCommand.CanExecute(null));

        _ctx.AllDevices = false;
        _ctx.RaisePermissionsChanged();
        Assert.Equal(RadiusTab.Sessions, vm.Tab);
        Assert.False(vm.MayManage);
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
        await vm.ShowSessionsTabCommand.ExecuteAsync(null);
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
        Assert.Equal(["This server", "Building B"], vm.Vlans.SiteChoices.Select(c => c.Label));
        Assert.Equal(["Outdoor", "PTZ"], vm.Vlans.TagChoices.Select(c => c.Label));
    }

    [Fact]
    public void The_page_reads_the_contract_names_and_sends_the_reason_codes()
    {
        var json = RadiusJson.Serialize(new SwitchRow { State = SwitchStates.WrongSecret });
        Assert.Contains("\"state\":\"wrongSecret\"", json, StringComparison.Ordinal);
        Assert.Contains("\"minimumTls\":\"1.2\"", RadiusJson.Serialize(new RadiusSettings()), StringComparison.Ordinal);
        Assert.Equal("Wrong secret", new SwitchRowViewModel(RadiusJson.Deserialize<SwitchRow>(json)).State);
        Assert.Equal(RevokeReasons.KeyCompromised, RevokedViewModel.ReasonCode(RevocationReason.KeyCompromised));
        Assert.Equal(RevokeReasons.DeviceRemoved, RevokedViewModel.ReasonCode(RevocationReason.DeviceRemoved));
        Assert.Equal(["TLS 1.0", "TLS 1.1", "TLS 1.2", "TLS 1.3"], SettingsViewModel.TlsVersions.Select(v => v.Label));
    }
}
