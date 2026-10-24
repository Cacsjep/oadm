using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Client;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Radius.Tests.Client;

/// <summary>
/// The page's reads around the live events (stale lists, events during a read, the log's choice), the one Save of General
/// and VLANs, the Revoked tab's pages and the certificate choice's View certificate.
/// </summary>
public sealed class PageReadAndSaveTests
{
    private readonly FakeRadiusBackend _server = new();
    private readonly FakePageContext _ctx;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));

    public PageReadAndSaveTests()
    {
        _ctx = new FakePageContext(_server);
    }

    private sealed class CertificateDialogs : IIssuedCertificateListDialogs
    {
        public List<CertificateDetails> Shown { get; } = [];

        public Task ShowCertificateAsync(CertificateDetails certificate)
        {
            Shown.Add(certificate);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Sessions_and_log_are_read_again_when_the_page_comes_back()
    {
        using var vm = new RadiusViewModel(_ctx, _time);
        vm.Activate();
        await vm.ShowSessionsTabCommand.ExecuteAsync(null);
        await vm.ShowLogTabCommand.ExecuteAsync(null);
        Assert.Equal(1, _server.CountOf(RadiusMethods.ListSessions));
        Assert.Equal(1, _server.CountOf(RadiusMethods.ListLog));
        await vm.ShowSessionsTabCommand.ExecuteAsync(null);
        Assert.Equal(1, _server.CountOf(RadiusMethods.ListSessions)); // while shown, the events keep it

        // Left and back: no events came in meanwhile, the open tab is read again, the other one when it opens.
        vm.Deactivate();
        _server.Sessions.Add(FakeRadiusBackend.Session(_time.GetUtcNow().UtcDateTime, "AC:CC:8E:12:34:0A", "startingTls", "Starting TLS"));
        vm.Activate();
        await Eventually.TrueAsync(() => _server.CountOf(RadiusMethods.ListSessions) == 2 && !vm.Sessions.IsReloading, "the sessions read again");
        Assert.Contains(vm.Sessions.Rows, r => r.Mac == "AC:CC:8E:12:34:0A");
        Assert.Equal(1, _server.CountOf(RadiusMethods.ListLog));
        await vm.ShowLogTabCommand.ExecuteAsync(null);
        Assert.Equal(2, _server.CountOf(RadiusMethods.ListLog));
        vm.Deactivate();
    }

    [Fact]
    public async Task Session_events_during_the_first_read_are_applied_after_it()
    {
        var sessions = new SessionsViewModel(_ctx, _time);
        var existing = _server.Sessions[0];
        var hold = _server.Hold(RadiusMethods.ListSessions);
        var read = sessions.LoadAsync(CancellationToken.None);
        Assert.True(sessions.IsLoading);

        var started = FakeRadiusBackend.Session(_time.GetUtcNow().UtcDateTime, "AC:CC:8E:12:34:0B", "waitingForIdentity", "Waiting for identity");
        sessions.Apply([started, existing with { StateText = "older", DurationMs = existing.DurationMs - 1000 }]);
        var ended = FakeRadiusBackend.Session(_time.GetUtcNow().UtcDateTime, "AC:CC:8E:12:34:0C", "waitingForIdentity", "Waiting for identity");
        sessions.Apply([ended]);
        sessions.Apply([ended with { Removed = true }]); // started and left during the read: never shown
        hold.SetResult();
        await read;

        Assert.Contains(sessions.Rows, r => r.Id == started.Id);
        Assert.DoesNotContain(sessions.Rows, r => r.Id == ended.Id);
        Assert.Equal(existing.StateText, sessions.Rows.Single(r => r.Id == existing.Id).Source.StateText); // the read row is newer
        Assert.Equal(_server.Sessions.Count + 1, sessions.Rows.Count);
    }

    [Fact]
    public async Task Log_rows_during_a_read_are_added_once_after_it()
    {
        var log = new LogViewModel(_ctx, () => true, _time);
        var hold = _server.Hold(RadiusMethods.ListLog);
        var read = log.LoadAsync();
        var onPage = _server.Log[0];
        var fresh = FakeRadiusBackend.LogEntry(_time.GetUtcNow().UtcDateTime.AddMinutes(1), "AC:CC:8E:12:34:0D", LogResults.Authenticated, null, null, 20, "10.0.0.60");
        log.Add([fresh, onPage]); // the page holds one of them already
        hold.SetResult();
        await read;

        Assert.Equal(fresh.Id, log.Rows[0].Source.Id);
        Assert.Single(log.Rows, r => r.Source.Id == onPage.Id);
        Assert.Equal(_server.Log.Count + 1, log.Rows.Count);
    }

    [Fact]
    public async Task The_chosen_log_row_stays_chosen_when_live_rows_push_the_full_page()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        _server.Log = [.. Enumerable.Range(0, RadiusLimits.MaxLogPage).Select(i => FakeRadiusBackend.LogEntry(now.AddSeconds(-i), "AC:CC:8E:12:34:01", LogResults.Authenticated, null, null, null, "10.0.0.41"))];
        var log = new LogViewModel(_ctx, () => true, _time);
        await log.LoadAsync();
        var chosen = log.Rows[10];
        log.Selected = chosen;
        log.Rows.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                log.Selected = null; // what the grid does on a reset
            }
        };

        log.Add([FakeRadiusBackend.LogEntry(now.AddSeconds(1), "AC:CC:8E:12:34:02", LogResults.Failed, "Timed out", null, null, null)]);

        Assert.Equal(RadiusLimits.MaxLogPage, log.Rows.Count);
        Assert.Same(chosen, log.Selected);
        Assert.True(log.ViewCertificateCommand.CanExecute(null));
    }

    [Fact]
    public async Task Hidden_vlan_errors_never_block_save_and_the_saved_rules_are_kept()
    {
        using var vm = new RadiusViewModel(_ctx, _time);
        await vm.LoadServerAsync(CancellationToken.None);
        var saved = _server.Vlans;
        vm.Vlans.Enabled = true;
        vm.Vlans.Rules[0].Vlan = "0";
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.NotNull(vm.SaveTip);

        vm.Vlans.Enabled = false; // the card collapses
        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.Null(vm.SaveTip);
        await vm.SaveCommand.ExecuteAsync(null);

        var sent = _server.LastPayload<VlanSettings>(RadiusMethods.SaveVlans);
        Assert.False(sent.Enabled);
        Assert.Equal(saved.Rules, sent.Rules); // the edit that is not valid is not sent
        Assert.Equal(saved.DefaultVlan, sent.DefaultVlan);
        Assert.Null(vm.ActionError);
    }

    [Fact]
    public async Task Save_checks_both_parts_with_the_shared_rules_before_the_first_call()
    {
        using var vm = new RadiusViewModel(_ctx, _time);
        await vm.LoadServerAsync(CancellationToken.None);
        vm.Vlans.Apply(new VlanSettings
        {
            Enabled = true,
            Rules = [.. Enumerable.Range(0, RadiusLimits.MaxVlanRules + 1).Select(i => new VlanRule(VlanRuleKinds.Tag, "tag" + i, "tag" + i, 10))],
        });
        Assert.True(vm.SaveCommand.CanExecute(null));

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(RadiusRules.TooManyRules, vm.ActionError);
        Assert.Equal(0, _server.CountOf(RadiusMethods.SaveSettings)); // nothing half saved
        Assert.Equal(0, _server.CountOf(RadiusMethods.SaveVlans));
    }

    [Fact]
    public async Task A_refused_vlan_save_keeps_the_saved_general_settings_on_the_page()
    {
        using var vm = new RadiusViewModel(_ctx, _time);
        await vm.LoadServerAsync(CancellationToken.None);
        vm.Settings.Port = "1813";
        _server.Failures[RadiusMethods.SaveVlans] = new InvalidOperationException("The server is stopping.");

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(1813, _server.State.Settings.Port);
        Assert.Equal("1813", vm.Settings.Port);
        Assert.Equal("The server is stopping.", vm.ActionError);
    }

    [Fact]
    public async Task The_revoked_tab_reads_pages_of_500_from_the_server()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        _server.Revoked.AddRange(Enumerable.Range(0, 1_200).Select(i => new RevokedRow
        {
            SerialNumber = (0x100000 + i).ToString("X", System.Globalization.CultureInfo.InvariantCulture),
            RevokedUtc = now.AddMinutes(-i),
            Reason = "Other",
            UserName = "admin",
            NotAfterUtc = now.AddDays(100),
        }));
        var revoked = new RevokedViewModel(_ctx, () => true, _time);
        await revoked.LoadAsync();

        Assert.Equal(RevokedViewModel.PageSize, revoked.Rows.Count);
        Assert.Equal("1-500 of 1,202", revoked.PageText);
        Assert.True(revoked.HasPages);
        Assert.Equal("1,202 certificates revoked", revoked.CountText);
        Assert.Equal(IssuedCertificateTexts.UnknownDevice, revoked.Rows[0].Device); // a serial the list did not hold
        Assert.False(revoked.PreviousPageCommand.CanExecute(null));

        await revoked.NextPageCommand.ExecuteAsync(null);
        await revoked.NextPageCommand.ExecuteAsync(null);
        Assert.Equal(new RevokedQuery(1_000, 500, null), _server.LastPayload<RevokedQuery>(RadiusMethods.ListRevoked));
        Assert.Equal(202, revoked.Rows.Count);
        Assert.Equal("1,001-1,202 of 1,202", revoked.PageText);
        Assert.False(revoked.NextPageCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_server_part_without_pages_still_gives_pages_and_the_search()
    {
        _ctx.Answer = (method, json) => method == RadiusMethods.ListRevoked ? RadiusJson.Serialize(new { rows = _server.Revoked }) : null;
        var revoked = new RevokedViewModel(_ctx, () => true, _time);
        await revoked.LoadAsync();
        Assert.Equal(2, revoked.Total);
        Assert.Equal("10.0.0.49", revoked.Rows[0].Device);

        revoked.Search = "replaced";
        _time.Advance(revoked.SearchDelay);
        await Eventually.TrueAsync(() => revoked.Rows.Count == 1, "the search applied");
        Assert.Equal("1 certificate matches", revoked.CountText);
    }

    [Fact]
    public async Task View_certificate_of_the_choice_shows_the_revocation_and_the_rows_ending_soon()
    {
        var picker = new RevokePickerViewModel(_ctx, _time);
        var dialogs = new CertificateDialogs();
        picker.Dialogs = dialogs;
        await picker.LoadAsync();

        picker.SetSelection(picker.Rows.Where(r => r.Source.State == IssuedCertificateState.Revoked));
        await picker.ViewCertificateCommand.ExecuteAsync(null);
        var revoked = Assert.Single(dialogs.Shown);
        Assert.True(revoked.IsRevoked);
        Assert.StartsWith("Certificate revoked on ", revoked.ValidityText, StringComparison.Ordinal);

        // The window and the row agree on "Expires soon" (the list does not say PKI's warning days).
        foreach (var row in picker.Rows.Where(r => r.Source.State is IssuedCertificateState.ExpiresSoon or IssuedCertificateState.Valid))
        {
            var details = new CertificateDetails(
                CertificateDescriber.Describe(FakeRadiusBackend.CameraCertificate) with { NotAfterUtc = row.Source.NotAfterUtc, NotBeforeUtc = row.Source.NotBeforeUtc },
                "x", _time.GetUtcNow().UtcDateTime, null, picker.WarningDaysFor(row, _time.GetUtcNow().UtcDateTime));
            Assert.Equal(row.Source.State == IssuedCertificateState.ExpiresSoon, details.IsEndingSoon);
        }
    }
}
