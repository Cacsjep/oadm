using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Client;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Radius.Tests.Client;

/// <summary>The page's reads around the live events (stale lists, events during a read, the log's choice).</summary>
public sealed class PageReadAndSaveTests
{
    private readonly FakeRadiusBackend _server = new();
    private readonly FakePageContext _ctx;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));

    public PageReadAndSaveTests()
    {
        _ctx = new FakePageContext(_server);
    }

    [Fact]
    public async Task Sessions_and_log_are_read_again_when_the_page_comes_back()
    {
        using var vm = new RadiusViewModel(_ctx, _time);
        vm.Activate();
        await Eventually.TrueAsync(() => _server.CountOf(RadiusMethods.ListLog) == 1 && vm.Log.IsLoaded && !vm.Sessions.IsLoading, "the first read");
        Assert.Equal(1, _server.CountOf(RadiusMethods.ListSessions));

        // Left and back: no events came in meanwhile, both are read again.
        vm.Deactivate();
        _server.Sessions.Add(FakeRadiusBackend.Session(_time.GetUtcNow().UtcDateTime, "AC:CC:8E:12:34:0A", "startingTls", "Starting TLS"));
        vm.Activate();
        await Eventually.TrueAsync(() => _server.CountOf(RadiusMethods.ListSessions) == 2 && _server.CountOf(RadiusMethods.ListLog) == 2
            && !vm.Sessions.IsReloading && !vm.Log.IsReloading, "the sessions and the log read again");
        Assert.Contains(vm.Sessions.Rows, r => r.Mac == "AC:CC:8E:12:34:0A");
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
}
