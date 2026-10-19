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
/// for sites-page.png, sites-bind-wrong-code.png and sites-devices-column.png.
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
        Assert.Equal("12 ms", building.RoundTripText);
        Assert.Equal("3 / 1", building.ConnectionsText);
        Assert.Equal("1", building.DeviceCountText);
        var warehouse = page.Rows.Single(r => r.Name == "Warehouse");
        Assert.True(warehouse.IsError);
        Assert.Equal("3.6.9 · Update available", warehouse.VersionText);
        Assert.True(warehouse.CanChangeAddress); // inbound
        Assert.False(building.CanChangeAddress);
        var waiting = page.Rows.Single(r => !r.IsBound);
        Assert.Equal("Waiting to be bound", waiting.StateText);
        Assert.True(waiting.CanBind);
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
    public Task Rename_replace_change_address_and_add_manually_go_to_the_server() => _ui.RunAsync(async () =>
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

        var address = SiteFormViewModel.ChangeAddress(api, warehouse);
        Assert.Equal("10.20.0.5", address.Address);
        address.PortText = "70000";
        address.FieldLeft(nameof(address.PortText));
        Assert.Equal("Enter a port from 1 to 65535.", address.ErrorOf(nameof(address.PortText)));
        address.PortText = "";
        address.Address = "10.20.0.6";
        await address.SubmitCommand.ExecuteAsync(null);
        Assert.Contains((await api.ListSitesAsync(CancellationToken.None)).Sites, s => s.NodeAddress == "10.20.0.6" && s.NodePort == 5081);

        var others = page.Rows.Where(r => r.IsBound && r.Site.Id != warehouse.Id).Select(r => r.Site).ToList();
        var replace = SiteFormViewModel.Replace(api, warehouse, others);
        Assert.False(replace.SubmitCommand.CanExecute(null)); // choose a site first
        replace.NewSite = others[0];
        await replace.SubmitCommand.ExecuteAsync(null);
        Assert.Equal("2 devices moved.", SiteTexts.Replaced(replace.ReplaceReply!));

        var manual = SiteFormViewModel.AddManually(api);
        manual.Address = "10.30.0.9";
        await manual.SubmitCommand.ExecuteAsync(null);
        Assert.Equal("10.30.0.9", manual.FoundNode!.NodeAddress);
        api.Dispose();
    });

    [Fact]
    public Task Roles_without_administrator_rights_see_the_sites_but_no_actions() => _ui.RunAsync(async () =>
    {
        var (api, _, page) = Create(isAdmin: false);
        page.Apply(await api.ListSitesAsync(CancellationToken.None));

        Assert.False(page.MayChange);
        Assert.All(page.Rows, r => Assert.False(r.CanBind || r.CanManage || r.CanChangeAddress));
        Assert.False(page.AddManuallyCommand.CanExecute(null));
        Assert.False(page.SearchNodesCommand.CanExecute(null));
        Assert.Equal("Accepting site nodes", page.AcceptText);
        await page.SetAcceptAsync(false); // refused locally
        Assert.True((await api.ListSitesAsync(CancellationToken.None)).Accept);
        api.Dispose();
    });

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
            list.Sites.Add(new Site { Name = "node-" + i, State = SiteState.Waiting, NodeId = "w" + i, NodeAddress = "10.9.0." + i, NodePort = 50000 + i });
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
            var window = new Window { Width = 1600, Height = 700, Content = new Avalonia.Controls.Border { Padding = new Avalonia.Thickness(20), Child = new SitesView { DataContext = page } } };
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
            Capture(window, outDir, "sites-page.png");
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
