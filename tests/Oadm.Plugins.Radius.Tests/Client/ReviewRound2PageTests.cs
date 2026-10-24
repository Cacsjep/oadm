using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Client;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Radius.Tests.Client;

/// <summary>Review round 2 of the page: the log's time zone and Delete all during a read, unusable suites, Revoke's progress, VLANs of group users.</summary>
public sealed class ReviewRound2PageTests
{
    private readonly FakeRadiusBackend _server = new();
    private readonly FakePageContext _ctx;

    public ReviewRound2PageTests()
    {
        _ctx = new FakePageContext(_server);
    }

    internal sealed class RevokeDialogs : IRevokeDialogs
    {
        public async Task<IReadOnlyList<IssuedCertificateRow>?> ChooseCertificatesAsync(RevokePickerViewModel picker)
        {
            await picker.LoadAsync();
            return [picker.Rows[0]];
        }

        public Task<RevocationReason?> ConfirmRevokeAsync(string title, string message) => Task.FromResult<RevocationReason?>(RevocationReason.KeyCompromised);
    }

    /// <summary>The fake's suites with the server's ECDSA key: the ECDHE_RSA suites cannot be negotiated.</summary>
    internal static IReadOnlyList<CipherSuiteOption> EcdsaKeySuites { get; } =
        [.. FakeRadiusBackend.Suites.Select(s => s.Name.Contains("_RSA_", StringComparison.Ordinal) ? s with { Usable = false } : s)];

    internal static RadiusState WithOnlyRsaSuitesChecked(RadiusState state) => state with
    {
        CipherSuitesListed = EcdsaKeySuites,
        Settings = state.Settings with
        {
            MinimumTls = "1.2",
            CipherSuites = [.. EcdsaKeySuites.Where(s => !s.Usable && !s.Legacy).Select(s => s.Id)],
        },
    };

    [Fact]
    public async Task Every_log_read_sends_the_time_zone_the_page_shows()
    {
        using var vm = new RadiusViewModel(_ctx);
        await vm.ShowLogTabCommand.ExecuteAsync(null);
        Assert.Equal(TimeZoneInfo.Local.Id, _server.LastPayload<LogQuery>(RadiusMethods.ListLog).TimeZoneId);

        vm.Log.Offset = 0;
        await vm.Log.LoadAsync(CancellationToken.None);
        Assert.Equal(TimeZoneInfo.Local.Id, _server.LastPayload<LogQuery>(RadiusMethods.ListLog).TimeZoneId);
    }

    [Fact]
    public async Task Delete_all_during_a_read_drops_the_read_and_the_rows_that_waited_for_it()
    {
        var log = new LogViewModel(_ctx, () => true);
        await log.LoadAsync(CancellationToken.None);
        Assert.Equal(5, log.Rows.Count);

        var hold = _server.Hold(RadiusMethods.ListLog);
        var reading = log.LoadAsync(CancellationToken.None);
        Assert.True(log.IsReloading);
        log.Add([FakeRadiusBackend.LogEntry(DateTime.UtcNow, "AC:CC:8E:12:34:99", LogResults.Authenticated, null, null, null, "10.0.0.99")]); // waits for the read
        log.Cleared(); // logCleared from another client
        Assert.False(log.IsReloading);
        Assert.Empty(log.Rows);

        hold.SetResult(); // the read answers with the rows from before the delete
        await reading;

        Assert.Empty(log.Rows);
        Assert.Equal(0, log.Total);
        Assert.False(log.IsReloading);
        Assert.False(log.IsLoading);
        Assert.Equal("No authentications yet. Add a switch and turn on 802.1X on its ports.", log.EmptyText);

        // Live rows after the delete show at once (nothing waits for the dropped read).
        log.Add([FakeRadiusBackend.LogEntry(DateTime.UtcNow, "AC:CC:8E:12:34:98", LogResults.Failed, "Timed out", null, null, null)]);
        Assert.Equal(["AC:CC:8E:12:34:98"], log.Rows.Select(r => r.Mac));
        Assert.Equal(1, log.Total);
    }

    [Fact]
    public async Task Delete_all_during_the_first_read_ends_the_overlay_with_the_empty_log()
    {
        var log = new LogViewModel(_ctx, () => true);
        var hold = _server.Hold(RadiusMethods.ListLog);
        var reading = log.LoadAsync(CancellationToken.None);
        Assert.True(log.IsLoading);

        log.Cleared();
        hold.SetResult();
        await reading;

        Assert.False(log.IsLoading);
        Assert.Empty(log.Rows);
        Assert.True(log.IsLoaded);
        Assert.False(log.NeedsLoad);
    }

    [Fact]
    public async Task Only_suites_the_server_certificate_cannot_use_keep_save_off_with_the_reason()
    {
        _server.State = WithOnlyRsaSuitesChecked(_server.State);
        using var vm = new RadiusViewModel(_ctx);
        await vm.LoadServerAsync(CancellationToken.None);

        var rsa = vm.Settings.CipherSuites.First(s => s.Name == "TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256");
        var ecdsa = vm.Settings.CipherSuites.First(s => s.Name == "TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256");
        var tls13 = vm.Settings.CipherSuites.First(s => s.Name == "TLS_AES_128_GCM_SHA256");
        Assert.Equal((SettingsViewModel.UnusableSuiteHint, true), (rsa.KeyHint, rsa.HasKeyHint));
        Assert.Equal((null, false), (ecdsa.KeyHint, ecdsa.HasKeyHint));
        Assert.Null(tls13.KeyHint);
        Assert.False(rsa.IsUsable("1.2"));
        Assert.False(rsa.IsUsable("1.0"));
        Assert.True(ecdsa.IsUsable("1.2"));

        Assert.False(vm.Settings.IsFormValid);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.Equal(RadiusRules.CheckCipherSuite, vm.SaveTip);
        Assert.Null(vm.Settings.CipherSuitesError); // an untouched form shows no error below the list

        rsa.IsChecked = false;
        rsa.IsChecked = true; // edited: the error shows below the list
        Assert.Equal(RadiusRules.CheckCipherSuite, vm.Settings.CipherSuitesError);

        ecdsa.IsChecked = true;
        Assert.True(vm.Settings.IsFormValid);
        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.Null(vm.SaveTip);
        Assert.Null(vm.Settings.CipherSuitesError);
    }

    [Fact]
    public async Task Revoke_shows_its_progress_and_stays_off_until_the_server_answered()
    {
        using var vm = new RadiusViewModel(_ctx);
        vm.Revoked.Dialogs = new RevokeDialogs();
        await vm.ShowRevokedTabCommand.ExecuteAsync(null);
        Assert.Equal(RevokedViewModel.ReadingText, vm.Revoked.ProgressText);
        Assert.False(vm.Revoked.ShowsProgress);

        var hold = _server.Hold(RadiusMethods.Revoke);
        var revoking = vm.Revoked.RevokeCommand.ExecuteAsync(null);
        await Eventually.TrueAsync(() => vm.Revoked.IsRevoking, "Revoke is running");

        Assert.True(vm.Revoked.ShowsProgress);
        Assert.Equal(RevokedViewModel.RevokingText, vm.Revoked.ProgressText);
        Assert.False(vm.Revoked.RevokeCommand.CanExecute(null));
        var readsBefore = _server.CountOf(RadiusMethods.ListRevoked);

        hold.SetResult();
        await revoking;

        Assert.False(vm.Revoked.IsRevoking);
        Assert.False(vm.Revoked.ShowsProgress);
        Assert.True(vm.Revoked.RevokeCommand.CanExecute(null));
        Assert.Equal(readsBefore + 1, _server.CountOf(RadiusMethods.ListRevoked)); // the page is read again
        Assert.Equal(3, vm.Revoked.Rows.Count);
        Assert.Empty(_ctx.Messages);
    }

    [Fact]
    public async Task A_refused_revoke_ends_the_progress_before_the_message()
    {
        using var vm = new RadiusViewModel(_ctx);
        vm.Revoked.Dialogs = new RevokeDialogs();
        await vm.ShowRevokedTabCommand.ExecuteAsync(null);
        _server.Failures[RadiusMethods.Revoke] = new InvalidOperationException("Only IEEE 802.1X certificates can be revoked here.");
        bool? messageBeforeTheEnd = null;
        vm.Revoked.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(RevokedViewModel.IsRevoking) && !vm.Revoked.IsRevoking)
            {
                messageBeforeTheEnd = _ctx.Messages.Count > 0;
            }
        };

        await vm.Revoked.RevokeCommand.ExecuteAsync(null);

        Assert.False(messageBeforeTheEnd); // the progress ended before the message showed
        Assert.Single(_ctx.Messages);
        Assert.False(vm.Revoked.IsRevoking);
    }

    [Fact]
    public async Task A_group_user_reads_no_vlans_and_gets_no_tag_choices()
    {
        _ctx.AllDevices = false;
        using var vm = new RadiusViewModel(_ctx);
        await vm.LoadServerAsync(CancellationToken.None);

        Assert.False(vm.SeesVlans);
        Assert.Equal(0, _server.CountOf(RadiusMethods.GetVlans));
        Assert.Empty(vm.Vlans.Rules);
        Assert.Empty(vm.Vlans.TagChoices); // the devices of the group have tags: none is a choice
        Assert.Empty(vm.Vlans.SiteChoices);
        Assert.False(vm.Vlans.Enabled);
        Assert.Equal(string.Empty, vm.Vlans.DefaultVlan);
    }

    [Fact]
    public async Task The_server_saying_the_user_is_limited_to_a_group_hides_the_vlans()
    {
        _server.State = _server.State with { HasAllDevices = false };
        _server.Vlans = new VlanSettings(); // the server's answer to a group user
        using var vm = new RadiusViewModel(_ctx);
        await vm.LoadServerAsync(CancellationToken.None);

        Assert.False(vm.SeesVlans);
        Assert.Equal(0, _server.CountOf(RadiusMethods.GetVlans));
        Assert.Empty(vm.Vlans.Rules);
        Assert.Empty(vm.Vlans.TagChoices);
        Assert.True(vm.Vlans.IsValid);
    }

    [Fact]
    public async Task Rules_and_tags_read_with_all_devices_leave_when_the_user_is_limited_to_a_group()
    {
        _server.Vlans = _server.Vlans with { TagChoices = [new TagChoice("Lobby", "Lobby")] };
        using var vm = new RadiusViewModel(_ctx);
        await vm.LoadServerAsync(CancellationToken.None);
        Assert.True(vm.SeesVlans);
        Assert.Equal(2, vm.Vlans.Rules.Count);
        Assert.Equal(["Lobby", "Outdoor", "PTZ"], vm.Vlans.TagChoices.Select(c => c.Label)); // the server's tags too

        _ctx.AllDevices = false;
        _ctx.RaisePermissionsChanged();

        Assert.False(vm.SeesVlans);
        Assert.Empty(vm.Vlans.Rules);
        Assert.Empty(vm.Vlans.TagChoices);
        Assert.False(vm.Vlans.Enabled);
    }

    [Fact]
    public async Task Empty_vlan_settings_from_the_server_show_an_empty_card()
    {
        _server.Vlans = new VlanSettings();
        using var vm = new RadiusViewModel(_ctx);
        await vm.LoadServerAsync(CancellationToken.None);

        Assert.True(vm.SeesVlans);
        Assert.Empty(vm.Vlans.Rules);
        Assert.False(vm.Vlans.Enabled);
        Assert.Equal(["Outdoor", "PTZ"], vm.Vlans.TagChoices.Select(c => c.Label));
        Assert.True(vm.SaveCommand.CanExecute(null));
    }
}
