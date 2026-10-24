using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Client;

namespace Oadm.Plugins.Radius.Tests.Client;

/// <summary>
/// The one Sessions table of the 802.1X page (docs/spec/radius-server.md, decision 2026-10-25): the authentications in
/// progress on top, then the log's page; an ended session leaves the top (the log holds it); the log's search also filters
/// the rows in progress; the choice of a log row is the log's (View certificate).
/// </summary>
public sealed class AuthenticationsTests
{
    private readonly FakeRadiusBackend _server = new();
    private readonly FakePageContext _ctx;

    public AuthenticationsTests()
    {
        _ctx = new FakePageContext(_server);
    }

    private async Task<RadiusViewModel> LoadedAsync()
    {
        var vm = new RadiusViewModel(_ctx);
        await vm.Sessions.LoadAsync(CancellationToken.None);
        await vm.Log.LoadAsync(CancellationToken.None);
        return vm;
    }

    [Fact]
    public async Task The_sessions_in_progress_come_first_then_the_log()
    {
        using var vm = await LoadedAsync();
        var table = vm.Authentications;
        var open = vm.Sessions.Rows.Where(r => !r.IsEnded).ToList();

        Assert.Equal(open.Count + vm.Log.Rows.Count, table.Rows.Count);
        Assert.Equal(open.Cast<IAuthenticationRow>(), table.Rows.Take(open.Count));
        Assert.Equal(vm.Log.Rows.Cast<IAuthenticationRow>(), table.Rows.Skip(open.Count));
        Assert.All(table.Rows.Take(open.Count), r => Assert.True(r.IsRunning));
        Assert.All(table.Rows.Skip(open.Count), r => Assert.False(r.IsRunning));
        Assert.Equal(vm.Sessions.CountText, table.CountText);
    }

    [Fact]
    public async Task A_session_that_ends_leaves_the_top_in_one_reset()
    {
        using var vm = await LoadedAsync();
        var table = vm.Authentications;
        var before = table.Rows.Count;
        var open = vm.Sessions.Rows.First(r => !r.IsEnded);
        var resets = 0;
        table.Rows.CollectionChanged += (_, _) => resets++;

        vm.Sessions.Apply([open.Source with { State = SessionStates.Authenticated, StateText = "Authenticated" }]);

        Assert.Equal(before - 1, table.Rows.Count);
        Assert.DoesNotContain(open, table.Rows);
        Assert.Equal(1, resets);
    }

    [Fact]
    public async Task The_log_search_filters_the_sessions_in_progress_too()
    {
        using var vm = await LoadedAsync();
        var open = vm.Sessions.Rows.First(r => !r.IsEnded);

        vm.Log.Search = open.Mac;

        var running = vm.Authentications.Rows.OfType<SessionRowViewModel>().ToList();
        Assert.Equal([open], running);
    }

    [Fact]
    public async Task The_choice_of_a_log_row_is_the_logs_and_a_session_row_chooses_no_log_row()
    {
        using var vm = await LoadedAsync();
        var table = vm.Authentications;
        var logRow = vm.Log.Rows[0];

        table.Selected = logRow;
        Assert.Same(logRow, vm.Log.Selected);

        table.Selected = table.Rows.OfType<SessionRowViewModel>().First();
        Assert.Null(vm.Log.Selected);

        vm.Log.Selected = vm.Log.Rows[1];
        Assert.Same(vm.Log.Rows[1], table.Selected);
    }

    [Fact]
    public void A_session_shows_its_vlan_as_the_number()
    {
        var row = new SessionRowViewModel(FakeRadiusBackend.Session(DateTime.UtcNow, "AC:CC:8E:00:00:01", SessionStates.Authenticated, "Authenticated", vlan: 30), TimeProvider.System);
        Assert.Equal("30", row.Vlan);
        Assert.Equal(row.Started, row.Time);
    }
}
