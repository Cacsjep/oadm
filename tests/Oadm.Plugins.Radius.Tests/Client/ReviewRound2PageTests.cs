using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Client;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Radius.Tests.Client;

/// <summary>Review round 2 of the page: the log's time zone and Delete all during a read, the cards of group users.</summary>
public sealed class ReviewRound2PageTests
{
    private readonly FakeRadiusBackend _server = new();
    private readonly FakePageContext _ctx;

    public ReviewRound2PageTests()
    {
        _ctx = new FakePageContext(_server);
    }

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
    public async Task The_server_saying_the_user_is_limited_to_a_group_hides_the_cards()
    {
        _server.State = _server.State with { HasAllDevices = false };
        using var vm = new RadiusViewModel(_ctx);
        await vm.LoadServerAsync(CancellationToken.None);

        Assert.False(vm.HasAllDevices);
        Assert.Equal(0, _server.CountOf(RadiusMethods.ListSwitches));
        Assert.Empty(vm.Switches.Rows);
        Assert.Equal(string.Empty, vm.PoliciesText);
    }
}
