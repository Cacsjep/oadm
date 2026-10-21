using System.Diagnostics;

using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Client.Dialogs;
using Oadm.Client.Discovery;
using Oadm.Client.Infrastructure;
using Oadm.Contracts.V1;

using Xunit.Abstractions;

namespace Oadm.Client.Tests;

/// <summary>
/// The add page and the waiting list with sites (docs/spec/sites.md, Client): the Site column in every mode, the import's Site
/// column, an address that answers with different devices through several sites, the waiting list per (site, address).
/// Against the fake server: "Building B" online, "Warehouse" offline.
/// </summary>
public sealed class SiteDiscoveryTests(ITestOutputHelper output) : IDisposable
{
    private readonly FakeOadmApi _api = new(TimeSpan.FromMilliseconds(5));
    private readonly UiThread _ui = new();

    public void Dispose()
    {
        _ui.Dispose();
        _api.Dispose();
    }

    private AddDevicesViewModel Create(AddDevicesMode mode) => new(_api, _ui, NullLogger<AddDevicesViewModel>.Instance, mode);

    private static DiscoveredRowViewModel Row(AddDevicesViewModel page, string address) => page.Rows.Single(r => r.Address == address);

    [Fact]
    public void The_import_reads_the_site_column_and_a_duplicate_is_the_same_site_and_address()
    {
        DeviceImportFile file = DeviceImportFile.Parse("sites.csv", string.Join('\n',
            "Address,Site,Tags",
            "192.168.0.20,Building B,PTZ",
            "192.168.0.20,Warehouse,",
            "192.168.0.20,,",
            "192.168.0.20,building b,",
            "192.168.0.21," + new string('x', 65) + ","));

        Assert.Equal(["Building B", "Warehouse", null, "building b", new string('x', 65)], file.Lines.Select(l => l.Site));
        Assert.Equal([null, null, null, "Listed before in line 2.", "The site name is longer than 64 characters."], file.Lines.Select(l => l.Problem));

        // The device export writes the Site column; the import reads it back.
        Assert.Contains(DeviceListCsv.SiteColumn, DeviceListCsv.Columns);
    }

    [Fact]
    public Task A_scan_shows_the_site_each_device_answered_through() => _ui.RunAsync(async () =>
    {
        await using AddDevicesViewModel page = Create(AddDevicesMode.Scan);
        await page.OpenAsync();
        await TestSupport.WaitUntilAsync(() => page.Rows.Count == 11 && page.Rows.All(r => r.AuthState != AuthState.Pending));

        Assert.True(page.ShowSiteColumn);
        Assert.Equal("Building B", Row(page, "10.0.0.94").SiteName);
        Assert.Equal("", Row(page, "10.0.0.92").SiteName); // the server's own network
        Assert.Equal("Also answers through this server.", Row(page, "10.0.0.91").SiteTip);
        page.SearchText = "building";
        Assert.Equal(2, page.FilteredRows.Count);
    });

    [Fact]
    public Task An_address_with_different_devices_through_several_routes_lists_all_unchecked() => _ui.RunAsync(async () =>
    {
        await using AddDevicesViewModel page = Create(AddDevicesMode.Manual);
        await page.OpenAsync();
        page.ManualAddress = "192.168.0.20";
        await page.ProbeAddressCommand.ExecuteAsync(null);
        await TestSupport.WaitUntilAsync(() => page.Rows.Count == 2 && page.Rows.All(r => r.AuthState != AuthState.Pending) && !page.IsScanning);

        Assert.Equal(["Building B", ""], page.Rows.Select(r => r.SiteName));
        Assert.All(page.Rows, r =>
        {
            Assert.True(r.CanAdd);
            Assert.False(r.IsSelected); // which one is meant is not known: none is checked by itself
            Assert.Equal("Answers through Building B and this server.", r.StatusDetail);
        });
        Assert.False(page.AddCommand.CanExecute(null));

        // The technician picks the one: it is added at its site.
        page.Rows[0].IsSelected = true;
        await page.AddCommand.ExecuteAsync(null);
        Device added = (await _api.ListDevicesAsync(CancellationToken.None)).Single(d => d.Serial == page.Rows[0].Serial);
        Assert.Equal(("192.168.0.20", "Building B"), (added.Address, added.SiteName));
    });

    [Fact]
    public Task Import_lines_go_through_their_site_and_wait_per_site_and_address() => _ui.RunAsync(async () =>
    {
        string csv = string.Join('\n',
            "Address,Site",
            "192.168.0.21,Building B",
            "192.168.0.20,",
            "192.168.0.22,Warehouse",
            "192.168.0.23,Nowhere",
            "192.168.0.99,Building B");

        await using (AddDevicesViewModel page = await OpenImportAsync(csv))
        {
            Assert.True(page.ShowSiteColumn); // the file names sites
            Assert.Equal("Building B", Row(page, "192.168.0.21").SiteName);

            // Without a site, 192.168.0.20 answers through Building B and this server with different cameras: both, unchecked.
            DiscoveredRowViewModel[] both = [.. page.Rows.Where(r => r.Address == "192.168.0.20")];
            Assert.Equal(2, both.Length);
            Assert.All(both, r => Assert.Equal(("Answers through Building B and this server.", false), (r.StatusDetail, r.IsSelected)));
            Assert.Equal(page.Rows.IndexOf(both[0]) + 1, page.Rows.IndexOf(both[1])); // the second right below the first

            DiscoveredRowViewModel offline = Row(page, "192.168.0.22");
            Assert.Equal((DiscoveredRowViewModel.NotFoundChip, "Site offline: Warehouse"), (offline.ChipText, offline.StatusDetail));
            Assert.True(offline.CanWait);
            DiscoveredRowViewModel unknown = Row(page, "192.168.0.23");
            Assert.Equal(("Not added", "There is no site named Nowhere."), (unknown.ChipText, unknown.StatusDetail));
            Assert.False(unknown.CanWait);
            Assert.Equal((DiscoveredRowViewModel.NotFoundChip, "No Axis device answered at 192.168.0.99."), (Row(page, "192.168.0.99").ChipText, Row(page, "192.168.0.99").StatusDetail));

            await page.AddWithWaitingCommand.ExecuteAsync(null);
        }

        WaitingLine kept = _api.KeptWaitingLines.Single(l => l.Address == "192.168.0.22");
        Assert.Equal(FakeOadmApi.FakeSiteOfflineId.ToString(), kept.SiteId);
        WaitingList waiting = await _api.ListWaitingAsync(null, 0, CancellationToken.None);
        Assert.Equal("Warehouse", waiting.Devices.Single(d => d.Address == "192.168.0.22").SiteName);

        // "Already waiting" compares (site, address): 192.168.0.99 waits for Building B; without a site it is another line.
        await using AddDevicesViewModel again = await OpenImportAsync("Address,Site\n192.168.0.22,Warehouse\n192.168.0.99,Building B\n192.168.0.99,");
        Assert.Equal(DiscoveredRowViewModel.AlreadyWaitingChip, again.Rows.Single(r => r.ImportLine!.Site == "Warehouse").ChipText);
        Assert.Equal(DiscoveredRowViewModel.AlreadyWaitingChip, again.Rows.Single(r => r.ImportLine!.Site == "Building B").ChipText);
        Assert.NotEqual(DiscoveredRowViewModel.AlreadyWaitingChip, again.Rows.Single(r => r.ImportLine!.Site is null).ChipText);
    });

    [Fact]
    [Trait("Category", "Perf")]
    public Task An_import_of_thirty_thousand_lines_with_sites_lists_filters_and_selects_fast() => _ui.RunAsync(async () =>
    {
        DeviceImportFile file = DeviceImportFile.Parse("sites.csv", "Address,Site\n" + string.Join('\n',
            Enumerable.Range(0, DeviceImportFile.MaxLines).Select(i => $"192.168.{i / 256 % 100}.{i % 256},{(i % 3 == 0 ? "" : i % 3 == 1 ? "Building B" : "Warehouse")}")));
        await using AddDevicesViewModel page = Create(AddDevicesMode.Import);

        var watch = Stopwatch.StartNew();
        page.SetImport(file);
        long listed = watch.ElapsedMilliseconds;
        page.SearchText = "192.168.3.";
        long filtered = watch.ElapsedMilliseconds - listed;
        page.AllSelected = true;
        long all = watch.ElapsedMilliseconds;

        output.WriteLine($"30,000 lines with sites: parsed and listed in {listed} ms, filtered in {filtered} ms, all in {all} ms");
        Assert.Equal(DeviceImportFile.MaxLines, page.Rows.Count);
        Assert.Equal(DeviceImportFile.MaxLines, file.Lines.Count(l => l.Problem is null)); // (site, address): no line is a duplicate of another site's
        Assert.True(all < 10_000, $"too slow: {all} ms");
        await Task.CompletedTask;
    });

    [Fact]
    [Trait("Category", "Perf")]
    public Task Thirty_thousand_import_lines_that_each_answer_through_two_sites_are_listed_fast() => _ui.RunAsync(async () =>
    {
        int count = DeviceImportFile.MaxLines;
        DeviceImportFile file = DeviceImportFile.Parse("two-routes.csv", "Address\n" + string.Join('\n',
            Enumerable.Range(0, count).Select(i => $"10.{i / 65536}.{i / 256 % 256}.{i % 256}")));
        await using AddDevicesViewModel page = Create(AddDevicesMode.Import);
        page.SetImport(file);
        DiscoveredRowViewModel[] lines = [.. page.Rows];

        var watch = Stopwatch.StartNew();
        for (int i = 0; i < count; i++)
        {
            string session = "s" + i;
            page.TrackImportSession(lines[i], session, "");
            page.OnDiscovered(session, Found(i, 'a', "Building B", lines[i].Address));
            page.OnDiscovered(session, Found(i, 'b', "", lines[i].Address));
        }

        long elapsed = watch.ElapsedMilliseconds;
        output.WriteLine($"30,000 lines answering through two sites: listed in {elapsed} ms");
        Assert.Equal(2 * count, page.Rows.Count);
        Assert.Equal(2 * count, page.FilteredRows.Count);
        for (int i = 0; i < 2 * count; i += 2)
        {
            Assert.Equal(page.Rows[i].Address, page.Rows[i + 1].Address); // the second device right below the first
            Assert.Same(page.Rows[i], page.FilteredRows[i]);
        }

        Assert.True(elapsed < 15_000, $"too slow: {elapsed} ms");
        await Task.CompletedTask;
    });

    private static DiscoveredDevice Found(int i, char route, string site, string address) => new()
    {
        DiscoveredId = $"d{i}{route}",
        Serial = $"ACCC{(route == 'a' ? 0 : 1)}{i:X7}",
        Address = address,
        Model = "P3265-V",
        SiteName = site,
        AuthState = AuthState.Authenticated,
        AuthUserName = "root",
    };

    [Fact]
    public async Task The_add_page_with_sites_and_the_waiting_list_render_headless()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        bool done = await HeadlessSession.Shared.Dispatch(async () =>
        {
            var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
            var ui = new AvaloniaUiDispatcher();

            // Discovery: two sample cameras answer through Building B.
            var scan = new AddDevicesViewModel(api, ui, NullLogger<AddDevicesViewModel>.Instance, AddDevicesMode.Scan);
            var scanWindow = new AddDevicesWindow { DataContext = scan, Width = 1180 };
            scanWindow.Show();
            await PumpUntilAsync(() => scan.Rows.Count == 11 && scan.Rows.All(r => r.AuthState != AuthState.Pending));
            DataGrid grid = scanWindow.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "DeviceList");
            Assert.True(grid.Columns.Single(c => Equals(c.Header, "Site")).IsVisible);
            Capture(scanWindow, outDir, "client-add-sites-scan.png");
            await scan.DisposeAsync();
            scanWindow.Close();

            // Import with a Site column: a line per site, an address with two devices, an offline site, an unknown one.
            var import = new AddDevicesViewModel(api, ui, NullLogger<AddDevicesViewModel>.Instance, AddDevicesMode.Import);
            import.SetImport(DeviceImportFile.Parse("sites.csv", string.Join('\n',
                "Address,Site,Tags",
                "192.168.0.21,Building B,Lobby",
                "192.168.0.20,,",
                "192.168.0.22,Warehouse,",
                "192.168.0.23,Nowhere,",
                "10.0.0.93,,")));
            var importWindow = new AddDevicesWindow { DataContext = import, Width = 1180 };
            importWindow.Show();
            await PumpUntilAsync(() => import.ImportCompletion is { IsCompleted: true } && !import.IsScanning
                && import.Rows.All(r => r.IsImportPlaceholder || r.AuthState != AuthState.Pending));
            Capture(importWindow, outDir, "client-add-sites-import.png");
            await import.AddWithWaitingCommand.ExecuteAsync(null);
            await import.DisposeAsync();
            importWindow.Close();

            // The waiting list window with its Site column.
            var vm = new WaitingDevicesViewModel(api, Substitute.For<IDialogService>(), new WaitingDevicesState());
            var window = new WaitingDevicesWindow();
            window.Attach(vm);
            window.Show();
            await vm.LoadAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Warehouse", vm.Rows.Single(r => r.Address == "192.168.0.22").SiteName);
            DataGrid waitingGrid = window.GetVisualDescendants().OfType<DataGrid>().Single(g => g.Name == "WaitingGrid");
            Assert.Contains(waitingGrid.Columns, c => Equals(c.Header, "Site"));
            Capture(window, outDir, "client-waiting-sites.png");
            window.Close();
            api.Dispose();
            return true;
        }, CancellationToken.None);
        Assert.True(done);
    }

    private async Task<AddDevicesViewModel> OpenImportAsync(string csv)
    {
        AddDevicesViewModel page = Create(AddDevicesMode.Import);
        page.SetImport(DeviceImportFile.Parse("sites.csv", csv));
        await page.OpenAsync();
        await page.ImportCompletion!;
        await TestSupport.WaitUntilAsync(() => !page.IsScanning && page.Rows.All(r => r.IsImportPlaceholder ? r.HasImportProblem : r.AuthState != AuthState.Pending));
        return page;
    }

    private static async Task PumpUntilAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            if (watch.Elapsed > TimeSpan.FromSeconds(30))
            {
                throw new TimeoutException("The page did not get there in time.");
            }

            await Task.Delay(10);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void Capture(Window window, string? outDir, string name)
    {
        Dispatcher.UIThread.RunJobs();
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            frame.Save(Path.Combine(outDir, name), PngBitmapEncoderOptions.Default);
        }
    }
}
