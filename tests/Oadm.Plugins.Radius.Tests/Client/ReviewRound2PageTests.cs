using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Client;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Radius.Tests.Client;

/// <summary>Review round 2 of the page: the log's time zone and Delete all during a read, unusable suites, policies of group users.</summary>
public sealed class ReviewRound2PageTests
{
    private readonly FakeRadiusBackend _server = new();
    private readonly FakePageContext _ctx;

    public ReviewRound2PageTests()
    {
        _ctx = new FakePageContext(_server);
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
        await vm.Log.LoadAsync(CancellationToken.None);
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
        var advanced = vm.NewAdvanced(_server.State);

        var rsa = advanced.CipherSuites.First(s => s.Name == "TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256");
        var ecdsa = advanced.CipherSuites.First(s => s.Name == "TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256");
        var tls13 = advanced.CipherSuites.First(s => s.Name == "TLS_AES_128_GCM_SHA256");
        Assert.Equal((AdvancedViewModel.UnusableSuiteHint, true), (rsa.KeyHint, rsa.HasKeyHint));
        Assert.Equal((null, false), (ecdsa.KeyHint, ecdsa.HasKeyHint));
        Assert.Null(tls13.KeyHint);
        Assert.False(rsa.IsUsable("1.2"));
        Assert.False(rsa.IsUsable("1.0"));
        Assert.True(ecdsa.IsUsable("1.2"));

        Assert.False(advanced.IsFormValid);
        Assert.False(advanced.SaveCommand.CanExecute(null));
        Assert.Equal(RadiusRules.CheckCipherSuite, advanced.FormError);
        Assert.Null(advanced.CipherSuitesError); // an untouched form shows no error below the list
        Assert.True(vm.SaveCommand.CanExecute(null)); // the Server card's Save is not blocked by Advanced

        rsa.IsChecked = false;
        rsa.IsChecked = true; // edited: the error shows below the list
        Assert.Equal(RadiusRules.CheckCipherSuite, advanced.CipherSuitesError);

        ecdsa.IsChecked = true;
        Assert.True(advanced.IsFormValid);
        Assert.True(advanced.SaveCommand.CanExecute(null));
        Assert.Null(advanced.CipherSuitesError);
    }

    [Fact]
    public async Task A_group_user_reads_no_policies_and_no_trusted_cas()
    {
        _ctx.AllDevices = false;
        using var vm = new RadiusViewModel(_ctx);
        await vm.LoadServerAsync(CancellationToken.None);

        Assert.False(vm.HasAllDevices);
        Assert.Equal(0, _server.CountOf(RadiusMethods.GetPolicies));
        Assert.Equal(0, _server.CountOf(RadiusMethods.GetTrustedCas));
        Assert.Empty(vm.Policies.Rows);
        Assert.Empty(vm.TrustedCas.Cas);
        Assert.Equal(string.Empty, vm.Policies.DefaultVlan);
    }

    [Fact]
    public async Task The_server_saying_the_user_is_limited_to_a_group_hides_the_cards()
    {
        _server.State = _server.State with { HasAllDevices = false };
        using var vm = new RadiusViewModel(_ctx);
        await vm.LoadServerAsync(CancellationToken.None);

        Assert.False(vm.HasAllDevices);
        Assert.Equal(0, _server.CountOf(RadiusMethods.GetPolicies));
        Assert.Equal(0, _server.CountOf(RadiusMethods.ListSwitches));
        Assert.Empty(vm.Policies.Rows);
    }

    [Fact]
    public async Task Policies_and_tags_read_with_all_devices_leave_when_the_user_is_limited_to_a_group()
    {
        using var vm = new RadiusViewModel(_ctx);
        await vm.LoadServerAsync(CancellationToken.None);
        Assert.Equal(3, vm.Policies.Rows.Count);
        Assert.Contains(vm.Policies.NewEditor(null).Picker.Tags, t => t.Name == "Lobby"); // the server's tags too

        _ctx.AllDevices = false;
        _ctx.RaisePermissionsChanged();

        Assert.Empty(vm.Policies.Rows);
        Assert.DoesNotContain(vm.Policies.NewEditor(null).Picker.Tags, t => t.Name == "Lobby");
    }

    [Fact]
    public async Task Empty_policies_from_the_server_show_an_empty_card()
    {
        _server.Policies = new PolicySettings();
        using var vm = new RadiusViewModel(_ctx);
        await vm.LoadServerAsync(CancellationToken.None);

        Assert.Empty(vm.Policies.Rows);
        Assert.False(vm.Policies.HasVlanPolicies);
        Assert.True(vm.Policies.AddCommand.CanExecute(null));
        Assert.True(vm.SaveCommand.CanExecute(null));
    }
}
