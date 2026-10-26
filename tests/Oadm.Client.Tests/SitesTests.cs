using System.Diagnostics;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Dialogs;
using Oadm.Client.Infrastructure;
using Oadm.Client.Sites;
using Oadm.Contracts.V1;

using Xunit.Abstractions;
using Oadm.Contracts.Sites;

namespace Oadm.Client.Tests;

/// <summary>
/// The Sites page, its dialogs and the Site column of the device grid against the fake server. Set OADM_SCREENSHOT_DIR
/// for sites-page.png, sites-page-empty-off.png, sites-page-empty-on.png, sites-bind-wrong-code.png and
/// sites-devices-column.png.
/// </summary>
public sealed class SitesTests(ITestOutputHelper output) : IDisposable
{
    private readonly UiThread _ui = new();

    public void Dispose() => _ui.Dispose();

    private static (FakeOadmApi Api, IDialogService Dialogs, SitesViewModel Page) Create(bool isAdmin = true, IUiDispatcher? ui = null)
    {
        var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var dialogs = Substitute.For<IDialogService>();
        var session = new Shell.UserSession();
        session.SignIn(isAdmin
            ? new UserInfo { UserName = "admin", Role = UserRole.Admin, RoleId = FakeOadmApi.AdministratorRoleId, AllPermissions = true }
            : new UserInfo { UserName = "op", Role = UserRole.Operator, RoleId = FakeOadmApi.OperatorRoleId, Permissions = { "sites.view" } }, "fake");
        return (api, dialogs, new SitesViewModel(api, dialogs, ui ?? new ImmediateUiDispatcher(), session));
    }

    [Fact]
    public Task The_page_lists_sites_and_waiting_nodes_and_filters_them() => _ui.RunAsync(async () =>
    {
        var (api, _, page) = Create();
        page.Apply(await api.ListSitesAsync(CancellationToken.None));

        Assert.Equal(3, page.Rows.Count);
        var building = page.Rows.Single(r => r.Name == "Building B");
        Assert.True(building.IsOk);
        Assert.Equal("Online", building.StateText);
        Assert.Equal("4", building.ConnectionsText); // now open
        var waitingRow = new SiteRowViewModel(new Site { Name = "acs", State = SiteState.Waiting, NodeId = "n", Message = SiteTexts.WaitingToBeBound }, mayChange: true);
        Assert.Equal(string.Empty, waitingRow.Detail); // never "Waiting to be bound: Waiting to be bound"
        Assert.True(waitingRow.CanBind);
        Assert.Equal("Online", building.StateTip);
        var warehouse = page.Rows.Single(r => r.Name == "Warehouse");
        Assert.True(warehouse.IsError);
        Assert.Equal("3.6.9 · Update available", warehouse.VersionText);
        Assert.Equal("Offline: " + SiteTexts.NodeNotAnswering, warehouse.StateTip); // the whole reason as the tooltip
        var waiting = page.Rows.Single(r => !r.IsBound);
        Assert.Equal("Waiting to be bound", waiting.StateText);
        Assert.True(waitingRow.CanBind);
        Assert.False(waiting.CanManage);
        Assert.Equal("2 sites · 1 online · 1 waiting to be bound", page.StatusText);

        page.SearchText = "ware";
        Assert.Equal("Warehouse", Assert.Single(page.Rows).Name);
        Assert.EndsWith("1 shown", page.StatusText, StringComparison.Ordinal);
        page.SearchText = "nothing like it";
        Assert.True(page.ShowEmpty);
        Assert.Equal("No site matches the search.", page.EmptyText);
        api.Dispose();
    });

    [Fact]
    public Task Empty_page_says_what_to_do_with_accept_off_and_on() => _ui.RunAsync(async () =>
    {
        var (api, _, page) = Create();
        page.Apply(new SiteList { Accept = false });
        Assert.True(page.ShowEmpty);
        Assert.Equal("Turn on Accept site nodes, then enter this server's address in the OADM Site Node app.", page.EmptyText);
        page.Apply(new SiteList { Accept = true, ListenAddress = "10.0.0.5:5081" });
        Assert.Equal("Enter 10.0.0.5:5081 as the server address in the OADM Site Node app.", page.EmptyText);

        // Accept site nodes goes to the server.
        page.Apply(await api.ListSitesAsync(CancellationToken.None));
        await page.SetAcceptAsync(false);
        Assert.False(page.Accept);
        Assert.False((await api.ListSitesAsync(CancellationToken.None)).Accept);
        Assert.DoesNotContain(page.Rows, r => !r.IsBound); // the waiting node connected out: gone while off
        api.Dispose();
    });

    [Fact]
    public Task Off_and_empty_shows_accept_in_the_middle_and_on_shows_turn_off_confirmed() => _ui.RunAsync(async () =>
    {
        var (api, dialogs, page) = Create();
        page.Apply(new SiteList { Accept = false });
        Assert.True(page.ShowOffState);
        Assert.False(page.ShowList);

        // Accept site nodes from the empty page: the list shows, Turn off sits in the toolbar.
        page.Apply(await api.ListSitesAsync(CancellationToken.None));
        await page.SetAcceptAsync(false);
        await page.AcceptCommand.ExecuteAsync(null);
        Assert.True(page.Accept);
        Assert.False(page.ShowOffState);
        Assert.True(page.ShowTurnOff);
        Assert.False(page.ShowAcceptButton);

        // Turn off asks first; No keeps it on, Turn off turns it off.
        dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        await page.TurnOffCommand.ExecuteAsync(null);
        Assert.True(page.Accept);
        dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        await page.TurnOffCommand.ExecuteAsync(null);
        Assert.False(page.Accept);
        Assert.False((await api.ListSitesAsync(CancellationToken.None)).Accept);
        Assert.False(page.ShowTurnOff);

        // An operator sees neither button.
        var (_, _, operatorPage) = Create(isAdmin: false);
        operatorPage.Apply(new SiteList { Accept = true });
        Assert.False(operatorPage.ShowTurnOff);
        Assert.False(operatorPage.ShowAcceptButton);
        api.Dispose();
    });

    [Fact]
    public Task Bind_shows_a_wrong_code_below_the_code_and_binds_with_the_right_one() => _ui.RunAsync(async () =>
    {
        var (api, _, page) = Create();
        page.Apply(await api.ListSitesAsync(CancellationToken.None));
        var node = page.Rows.Single(r => r.CanBind).Site;
        var form = SiteFormViewModel.Bind(api, node);
        Assert.Equal("site-pc-3", form.Name); // the node's host name
        Assert.Empty(form.GetErrors(nameof(form.PairingCode)).Cast<string>()); // untouched: no error yet

        form.PairingCode = "abc";
        form.FieldLeft(nameof(form.PairingCode));
        Assert.Equal(SiteTexts.CodeFormat, form.ErrorOf(nameof(form.PairingCode)));
        Assert.False(form.SubmitCommand.CanExecute(null));

        form.PairingCode = "WXYZ-2345";
        Assert.True(form.SubmitCommand.CanExecute(null));
        await form.SubmitCommand.ExecuteAsync(null);
        Assert.Equal(SiteFormViewModel.WrongCode, form.ErrorOf(nameof(form.PairingCode)));
        Assert.Null(form.ErrorOf(nameof(form.Name)));

        form.Name = "Building B"; // taken: the error belongs to the name
        form.PairingCode = "abcd efgh";
        await form.SubmitCommand.ExecuteAsync(null);
        Assert.Equal(SiteTexts.NameTaken, form.ErrorOf(nameof(form.Name)));

        var closed = false;
        form.CloseRequested += (_, ok) => closed = ok;
        form.Name = "Building C";
        await form.SubmitCommand.ExecuteAsync(null);
        Assert.True(closed);
        page.Apply(await api.ListSitesAsync(CancellationToken.None));
        Assert.Contains(page.Rows, r => r.Name == "Building C" && r.IsBound && r.IsOk);
        Assert.DoesNotContain(page.Rows, r => !r.IsBound);
        api.Dispose();
    });

    [Theory]
    [InlineData("abcd-efgh", "ABCDEFGH")]
    [InlineData("ABCD EFGH", "ABCDEFGH")]
    [InlineData("o1il-0000", "0111" + "0000")]
    [InlineData("ABCD-EFG", null)]
    [InlineData("ABCD-EFGHJ", null)]
    [InlineData("ABCD-EFGU", null)]
    public void Pairing_codes_are_read_like_the_node_writes_them(string typed, string? expected) =>
        Assert.Equal(expected, SiteFormViewModel.NormalizeCode(typed));

    [Fact]
    public Task Remove_keeps_the_devices_or_removes_them() => _ui.RunAsync(async () =>
    {
        var (api, dialogs, page) = Create();
        page.Apply(await api.ListSitesAsync(CancellationToken.None));
        var warehouse = page.Rows.Single(r => r.Name == "Warehouse");
        dialogs.ShowSiteFormAsync(Arg.Any<SiteFormViewModel>()).Returns(async call =>
        {
            var form = call.Arg<SiteFormViewModel>();
            Assert.Equal(SiteFormMode.Remove, form.Mode);
            Assert.True(form.IsDestructive);
            Assert.True(form.KeepDevices); // default
            await form.SubmitCommand.ExecuteAsync(null);
            return true;
        });

        await page.RemoveCommand.ExecuteAsync(warehouse);
        var devices = await api.ListDevicesAsync(CancellationToken.None);
        Assert.Equal(2, devices.Count(d => d.SiteId == FakeOadmApi.FakeSiteOfflineId.ToString())); // kept, Site offline
        Assert.Equal(SiteTexts.NodeDidNotAnswer, page.ErrorText); // the offline node could not be told
        Assert.All(devices.Where(d => d.SiteId == FakeOadmApi.FakeSiteOfflineId.ToString()), d => Assert.Equal("Warehouse (removed)", d.SiteName));

        // The site stays listed as Removed while it has devices: Replace with or Remove devices, nothing else.
        var removed = page.Rows.Single(r => r.Name == "Warehouse");
        Assert.Equal((SiteTexts.RemovedState, true, true, false, "Remove devices"), (removed.StateText, removed.CanReplace, removed.CanRemove, removed.CanManage, removed.RemoveTip));
        Assert.True(removed.IsWarning);
        dialogs.ShowSiteFormAsync(Arg.Any<SiteFormViewModel>()).Returns(async call =>
        {
            var form = call.Arg<SiteFormViewModel>();
            Assert.Equal(("Remove devices", false, false), (form.Title, form.ShowKeepDevices, form.KeepDevices));
            await form.SubmitCommand.ExecuteAsync(null);
            return true;
        });
        await page.RemoveCommand.ExecuteAsync(removed);
        devices = await api.ListDevicesAsync(CancellationToken.None);
        Assert.DoesNotContain(devices, d => d.SiteId == FakeOadmApi.FakeSiteOfflineId.ToString());
        Assert.DoesNotContain(page.Rows, r => r.Name == "Warehouse");

        var building = page.Rows.Single(r => r.Name == "Building B");
        dialogs.ShowSiteFormAsync(Arg.Any<SiteFormViewModel>()).Returns(async call =>
        {
            var form = call.Arg<SiteFormViewModel>();
            form.KeepDevices = false;
            await form.SubmitCommand.ExecuteAsync(null);
            return true;
        });
        await page.RemoveCommand.ExecuteAsync(building);
        devices = await api.ListDevicesAsync(CancellationToken.None);
        Assert.DoesNotContain(devices, d => d.SiteId == FakeOadmApi.FakeSiteOnlineId.ToString());
        Assert.Null(page.ErrorText);
        api.Dispose();
    });

    [Fact]
    public Task Rename_and_replace_go_to_the_server() => _ui.RunAsync(async () =>
    {
        var (api, _, page) = Create();
        page.Apply(await api.ListSitesAsync(CancellationToken.None));
        var warehouse = page.Rows.Single(r => r.Name == "Warehouse").Site;

        var rename = SiteFormViewModel.Rename(api, warehouse);
        rename.Name = "";
        rename.FieldLeft(nameof(rename.Name));
        Assert.Equal("Enter a site name.", rename.ErrorOf(nameof(rename.Name)));
        rename.Name = "Warehouse North";
        await rename.SubmitCommand.ExecuteAsync(null);
        Assert.Contains((await api.ListDevicesAsync(CancellationToken.None)), d => d.SiteName == "Warehouse North");

        var others = page.Rows.Where(r => r.IsBound && r.Site.Id != warehouse.Id).Select(r => r.Site).ToList();
        var replace = SiteFormViewModel.Replace(api, warehouse, others);
        Assert.False(replace.SubmitCommand.CanExecute(null)); // choose a site first
        replace.NewSite = others[0];
        await replace.SubmitCommand.ExecuteAsync(null);
        Assert.Equal("2 devices moved.", SiteTexts.Replaced(replace.ReplaceReply!));

        api.Dispose();
    });

    [Fact]
    public Task Roles_without_administrator_rights_see_the_sites_but_no_actions() => _ui.RunAsync(async () =>
    {
        var (api, _, page) = Create(isAdmin: false);
        page.Apply(await api.ListSitesAsync(CancellationToken.None));

        Assert.False(page.MayChange);
        Assert.All(page.Rows, r => Assert.False(r.CanBind || r.CanManage || r.CanReplace || r.CanRemove));
        Assert.Equal("Accepting site nodes", page.AcceptText);
        await page.SetAcceptAsync(false); // refused locally
        Assert.True((await api.ListSitesAsync(CancellationToken.None)).Accept);
        api.Dispose();
    });

    [Fact]
    public async Task A_login_before_any_list_shows_the_ring_and_another_login_starts_empty()
    {
        var api = Substitute.For<IOadmApi>();
        api.WatchSitesAsync(Arg.Any<CancellationToken>()).Returns(call => Never(call.Arg<CancellationToken>()));
        var session = new Shell.UserSession();
        using var page = new SitesViewModel(api, Substitute.For<IDialogService>(), new ImmediateUiDispatcher(), session);

        // The login raises IsAdmin before the page ever saw a list: still loading, never "Site nodes are off".
        session.SignIn(new UserInfo { UserName = "admin", Role = UserRole.Admin, RoleId = FakeOadmApi.AdministratorRoleId, AllPermissions = true }, "server-a");
        page.Activate();
        Assert.True(page.IsLoading);
        Assert.False(page.ShowOffState);
        Assert.False(page.ShowEmpty);

        // Server A's sites and a message of the page.
        var only = new Site { Id = Guid.NewGuid().ToString(), Name = "Plant A", State = SiteState.Online, NodeId = "a" };
        page.Apply(new SiteList { Accept = true, Sites = { only } });
        await page.ReplaceCommand.ExecuteAsync(page.Rows.Single());
        Assert.NotNull(page.ErrorText);
        page.Deactivate();

        // Log out, log in to server B: nothing of server A stays.
        session.SignOut();
        session.SignIn(new UserInfo { UserName = "admin", Role = UserRole.Admin, RoleId = FakeOadmApi.AdministratorRoleId, AllPermissions = true }, "server-b");
        page.Activate();
        Assert.Empty(page.Rows);
        Assert.True(page.IsLoading);
        Assert.Null(page.ErrorText);
        Assert.Null(page.NoticeText);
        Assert.False(page.ShowOffState);

        // The page shown again in the same session keeps what it has.
        page.Apply(new SiteList { Accept = false });
        Assert.True(page.ShowOffState);
        page.Deactivate();
        page.Activate();
        Assert.True(page.ShowOffState);
        page.Deactivate();
    }

    private static async IAsyncEnumerable<SiteList> Never([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        yield break;
    }

    [Fact]
    public Task Replace_offers_only_online_bound_sites() => _ui.RunAsync(async () =>
    {
        var (api, _, page) = Create();
        Site Bound(string name, SiteState state) => new() { Id = Guid.NewGuid().ToString(), Name = name, State = state, NodeId = name };
        var list = new SiteList
        {
            Accept = true,
            Sites =
            {
                Bound("Old", SiteState.Offline), Bound("Online", SiteState.Online), Bound("Offline", SiteState.Offline),
                Bound("Reset", SiteState.NotBound), Bound("Gone", SiteState.Removed),
                new Site { Name = "node", State = SiteState.Waiting, NodeId = "w" },
            },
        };
        page.Apply(list);
        var old = page.Rows.Single(r => r.Name == "Old");
        Assert.Equal(["Online"], page.ReplaceTargets(old).Select(s => s.Name));

        // No other site online: the page says so instead of a dialog the server would refuse.
        list.Sites.Remove(list.Sites.Single(s => s.Name == "Online"));
        page.Apply(list);
        await page.ReplaceCommand.ExecuteAsync(page.Rows.Single(r => r.Name == "Old"));
        Assert.Equal("The site that replaces Old must be online.", page.ErrorText);
        api.Dispose();
    });

    [Fact]
    public void Bind_suggests_a_free_name_and_keeps_the_name_of_a_site_bound_again()
    {
        var reset = new Site { Id = Guid.NewGuid().ToString(), Name = "acs", State = SiteState.NotBound, NodeId = "old-node", NodeFingerprint = "aa" };
        var node = new Site { Name = "acs", State = SiteState.Waiting, NodeId = "new-node", NodeFingerprint = "bb", Message = SiteTexts.WaitingToBeBound };
        Assert.Equal("acs (2)", SiteFormViewModel.SuggestName(node, [reset, node]));

        var second = new Site { Id = Guid.NewGuid().ToString(), Name = "ACS (2)", State = SiteState.Online, NodeId = "n2" };
        Assert.Equal("acs (3)", SiteFormViewModel.SuggestName(node, [reset, second, node]));

        // A removed site no longer holds its name; a free host name stays as it is.
        var removed = new Site { Id = Guid.NewGuid().ToString(), Name = "acs", State = SiteState.Removed, NodeId = "x" };
        Assert.Equal("acs", SiteFormViewModel.SuggestName(node, [removed, node]));

        // Long names stay within the limit.
        var longName = new string('n', SiteTexts.MaxNameLength);
        var longSite = new Site { Id = Guid.NewGuid().ToString(), Name = longName, State = SiteState.Online, NodeId = "l" };
        var suggested = SiteFormViewModel.SuggestName(new Site { Name = longName, NodeId = "m", State = SiteState.Waiting }, [longSite]);
        Assert.EndsWith(" (2)", suggested, StringComparison.Ordinal);
        Assert.Null(SiteTexts.NameProblem(suggested));

        // The node of a site that accepted this server's new identity: Bind keeps the site's name (no silent rename).
        var site = new Site { Id = Guid.NewGuid().ToString(), Name = "Warehouse", State = SiteState.Offline, NodeId = "wh-node", NodeFingerprint = "cc" };
        var again = new Site { Name = "wh-pc", State = SiteState.Waiting, NodeId = "wh-node", NodeFingerprint = "cc", Message = SiteTexts.BindAgain("Warehouse") };
        var form = SiteFormViewModel.Bind(Substitute.For<IOadmApi>(), again, [site, again]);
        Assert.Equal("Warehouse", form.Name);
    }

    [Fact]
    public void Hundred_sites_and_fifty_waiting_nodes_apply_and_filter_fast()
    {
        var (api, _, page) = Create();
        var list = new SiteList { Accept = true };
        for (var i = 0; i < 100; i++)
        {
            list.Sites.Add(new Site { Id = Guid.NewGuid().ToString(), Name = $"Site {i:000}", State = i % 3 == 0 ? SiteState.Offline : SiteState.Online, NodeId = "n" + i, DeviceCount = 300, RoundTripMs = i });
        }

        for (var i = 0; i < 50; i++)
        {
            list.Sites.Add(new Site { Name = "node-" + i, State = SiteState.Waiting, NodeId = "w" + i, NodeAddress = "10.9.0." + i });
        }

        var watch = Stopwatch.StartNew();
        page.Apply(list);
        page.SearchText = "Site 05";
        var elapsed = watch.ElapsedMilliseconds;
        output.WriteLine($"150 rows applied and searched in {elapsed} ms");
        Assert.Equal(10, page.Rows.Count);
        page.SearchText = "";
        Assert.Equal(150, page.Rows.Count);
        Assert.Equal("100 sites · 66 online · 50 waiting to be bound", page.StatusText);
        api.Dispose();
    }

    [Fact]
    public async Task The_device_grid_has_a_Site_column_in_search_and_export()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
        var devices = await api.ListDevicesAsync(CancellationToken.None);
        var rows = devices.Select(d => new DeviceRowViewModel(d)).ToList();
        var atSite = rows.Single(r => r.Address == "192.168.0.20");
        Assert.Equal("Building B", atSite.SiteName);
        Assert.True(atSite.Matches("building b"));
        Assert.Null(rows[0].SiteName);

        var csv = DeviceListCsv.Write([atSite]);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var header = lines[0].Split(',');
        var cells = lines[1].Split(',');
        Assert.Equal("Building B", cells[Array.IndexOf(header, DeviceListCsv.SiteColumn)]);
        Assert.Contains(ColumnLayoutViewModel.DefaultColumns, c => c.Key == "site" && c.Header == "Site");
    }

    [Fact]
    public async Task Page_dialog_and_device_column_render_headless()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var (api, _, page) = Create(ui: new AvaloniaUiDispatcher());
            var window = new Window { Width = 1920, Height = 700, Content = new Avalonia.Controls.Border { Padding = new Avalonia.Thickness(20), Child = new SitesView { DataContext = page } } };
            Oadm.Client.App.ApplyCrispText(window);
            window.Show();
            await TestSupport.WaitUntilAsync(() =>
            {
                Dispatcher.UIThread.RunJobs();
                return page.Rows.Count == 3;
            });
            Pump();
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty).ToList();
            Assert.Contains("Building B", texts);
            Assert.Contains("Waiting to be bound", texts);
            Assert.DoesNotContain("Response time", texts);
            Assert.Contains("Waiting for approval", texts);
            Assert.Contains("Uploads and downloads", texts);
            Assert.DoesNotContain("Change address", texts); // site nodes always connect to the server
            Capture(window, outDir, "sites-page.png");

            // The empty page: off (what sites are, Accept site nodes) and on (what to type in the node app).
            page.Deactivate();
            page.Apply(new SiteList { Accept = false });
            Pump();
            texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty).ToList();
            Assert.Contains("Site nodes are off", texts);
            Assert.DoesNotContain(texts, t => t.Contains("waits for the server", StringComparison.Ordinal));
            Capture(window, outDir, "sites-page-empty-off.png");
            page.Apply(new SiteList { Accept = true, ListenAddress = "10.0.0.5:5081" });
            Pump();
            texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty).ToList();
            Assert.Contains("Enter 10.0.0.5:5081 as the server address in the OADM Site Node app.", texts);
            Assert.DoesNotContain("Search", window.GetVisualDescendants().OfType<Oadm.Sdk.Client.Controls.ToolbarButton>().Where(b => b.IsEffectivelyVisible).Select(b => b.Text));
            Capture(window, outDir, "sites-page-empty-on.png");
            window.Close();
            page.Dispose();

            var node = (await api.ListSitesAsync(CancellationToken.None)).Sites.Single(s => s.State == SiteState.Waiting);
            var form = SiteFormViewModel.Bind(api, node);
            var dialog = new SiteFormWindow();
            dialog.Attach(form);
            Oadm.Client.App.ApplyCrispText(dialog);
            dialog.Show();
            form.PairingCode = "WXYZ-2345";
            await form.SubmitCommand.ExecuteAsync(null);
            Pump();
            var dialogTexts = dialog.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? string.Empty).ToList();
            Assert.Contains(SiteFormViewModel.WrongCode, dialogTexts);
            Capture(dialog, outDir, "sites-bind-wrong-code.png");
            dialog.Close();

            using var fixture = new DevicesFixture(api);
            fixture.SeedDevices([.. await api.ListDevicesAsync(CancellationToken.None)]);
            fixture.Store.Sites.Apply(await api.ListSitesAsync(CancellationToken.None)); // the server has sites: the Site column shows
            var devices = new Window { Width = 1800, Height = 800, Content = new DevicesView { DataContext = fixture.Devices } };
            Oadm.Client.App.ApplyCrispText(devices);
            devices.Show();
            fixture.Devices.SearchText = "192.168."; // the devices at the sample sites
            Pump();
            Assert.Equal(3, fixture.Devices.FilteredDevices.Count);
            var grid = devices.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "DeviceGrid");
            Assert.Contains(grid.Columns, c => c.Tag as string == "site" && c.IsVisible);
            Capture(devices, outDir, "sites-devices-column.png");
            devices.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void Capture(Window window, string? outDir, string name)
    {
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            frame.Save(Path.Combine(outDir, name), PngBitmapEncoderOptions.Default);
        }
    }
}
