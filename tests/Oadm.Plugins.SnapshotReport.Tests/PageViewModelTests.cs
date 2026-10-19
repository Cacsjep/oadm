using Oadm.Client.Api;
using Oadm.Plugins.SnapshotReport.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.SnapshotReport.Tests;

public sealed class PageViewModelTests
{
    [Fact]
    public async Task Fake_api_answers_with_the_plugin_payload_models()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5)) { SnapshotDelay = TimeSpan.Zero };
        var ctx = new FakeApiContext(api);

        var list = SnapshotReportJson.Deserialize<ListSourcesResult>(await ctx.InvokeAsync(SnapshotReportMethods.ListSources, "{}", CancellationToken.None));
        Assert.Contains(list.Tiles, t => t.Title == "10.0.0.48 - View Area 2" && t.Device.Model == "AXIS P3265-V" && t.Device.CertTrust == "SelfSigned");
        Assert.Contains(list.Tiles, t => t.Title == "10.0.0.32 - Sensor 4" && t.SourceCount == 5);
        Assert.Contains(list.Tiles, t => t.Title == "10.0.0.32 - Quad view");
        Assert.Contains(list.Tiles, t => t.Device.Status == "CredentialsRequired" && t.Error is not null);
        Assert.DoesNotContain(list.Tiles, t => t.Device.Model is "AXIS C1310-E Mk II" or "AXIS A9188");

        var tile = list.Tiles.First(t => t.Error is null);
        var snapshot = SnapshotReportJson.Deserialize<SnapshotResult>(await ctx.InvokeAsync(SnapshotReportMethods.Snapshot,
            SnapshotReportJson.Serialize(new SnapshotRequest { DeviceId = tile.Device.DeviceId, Camera = tile.Camera }), CancellationToken.None));
        Assert.Null(snapshot.Error);
        Assert.True(SnapshotRequests.TryGetJpegSize(Convert.FromBase64String(snapshot.JpegBase64!), out var w, out var h));
        Assert.Equal((snapshot.Width, snapshot.Height), (w, h));
    }

    [Fact]
    public async Task Nothing_is_read_until_Create_snapshots_is_pressed()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5)) { SnapshotDelay = TimeSpan.Zero };
        using var vm = new SnapshotReportViewModel(new FakeApiContext(api), new FakeUi(), new ExportSettingsStore(TempFiles.NewSettingsPath())).AllCameras();

        Assert.True(vm.IsWaitingForCreate);
        Assert.Empty(vm.Tiles);
        Assert.False(vm.IsEmpty);
        Assert.Equal(string.Empty, vm.StatusLine);
        Assert.True(vm.CreateSnapshotsCommand.CanExecute(null));
        Assert.False(vm.RefreshAllCommand.CanExecute(null));

        await vm.CreateSnapshotsCommand.ExecuteAsync(null);

        Assert.False(vm.IsWaitingForCreate);
        Assert.NotEmpty(vm.Tiles);
        Assert.False(vm.CreateSnapshotsCommand.CanExecute(null));
        Assert.True(vm.RefreshAllCommand.CanExecute(null));
    }

    [Fact]
    public async Task Refresh_lists_tiles_loads_every_snapshot_and_reports_progress()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5)) { SnapshotDelay = TimeSpan.Zero };
        var ui = new FakeUi();
        using var vm = new SnapshotReportViewModel(new FakeApiContext(api), ui, new ExportSettingsStore(TempFiles.NewSettingsPath())).AllCameras();

        await vm.RefreshAllAsync();

        Assert.False(vm.IsLoading);
        Assert.False(vm.IsProgressVisible);
        Assert.Equal(100, vm.Progress);
        var errors = vm.Tiles.Where(t => t.IsError).ToList();
        Assert.Equal(["CertificateChanged", "CredentialsRequired", "PasswordNotSet", "SiteOffline"], errors.Select(t => t.Tile.Device.Status).Order(StringComparer.Ordinal).Distinct().ToArray());
        Assert.All(vm.Tiles.Where(t => !t.IsDeviceError), t => Assert.Equal(TileState.Ok, t.State));
        Assert.Equal(vm.Tiles.Count(t => !t.IsDeviceError), ui.Decoded);
        Assert.All(vm.Tiles, t => Assert.True(t.IsSelected));
        Assert.True(vm.IsAllSelected);
        Assert.StartsWith($"{vm.Tiles.Count} pictures from ", vm.StatusLine, StringComparison.Ordinal);
        Assert.Contains($"{errors.Count} failed", vm.StatusLine, StringComparison.Ordinal);
        Assert.Equal("Credentials required - the device rejects the stored credentials", errors.Single(t => t.Tile.Device.Status == "CredentialsRequired").StatusText);
    }

    [Fact]
    public async Task Search_select_all_and_tile_selection_drive_the_export_command()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5)) { SnapshotDelay = TimeSpan.Zero };
        using var vm = new SnapshotReportViewModel(new FakeApiContext(api), new FakeUi(), new ExportSettingsStore(TempFiles.NewSettingsPath())).AllCameras();
        await vm.RefreshAllAsync();

        vm.SearchText = "P3727";
        Assert.Equal(5, vm.FilteredTiles.Count);
        Assert.Contains("5 shown", vm.StatusLine, StringComparison.Ordinal);

        vm.IsAllSelected = false;
        Assert.All(vm.FilteredTiles, t => Assert.False(t.IsSelected));
        Assert.Contains(vm.Tiles, t => t.IsSelected); // tiles hidden by the search keep their selection

        vm.SearchText = null;
        Assert.False(vm.IsAllSelected); // partly selected
        vm.IsAllSelected = true;
        Assert.All(vm.Tiles, t => Assert.True(t.IsSelected));
        vm.IsAllSelected = false;
        Assert.False(vm.ExportPdfCommand.CanExecute(null));
        vm.Tiles[0].ToggleSelectedCommand.Execute(null);
        Assert.True(vm.ExportPdfCommand.CanExecute(null));
        Assert.Contains("1 selected", vm.StatusLine, StringComparison.Ordinal);
        Assert.False(vm.IsAllSelected);

        vm.TileWidth = 5000;
        Assert.Equal(SnapshotReportViewModel.MaxTileWidth, vm.TileWidth);
        vm.TileWidth = 10;
        Assert.Equal(SnapshotReportViewModel.MinTileWidth, vm.TileWidth);
    }

    [Fact]
    public async Task The_page_reads_only_the_chosen_cameras_and_nothing_before_a_choice()
    {
        var a = new FakeDevice { Address = "10.0.0.48", Serial = "B8A44F000001", Tags = ["Building A"] };
        var b = new FakeDevice { Address = "10.0.0.49", Serial = "B8A44F000002", Tags = ["Building A"] };
        var c = new FakeDevice { Address = "10.0.0.50", Serial = "B8A44F000003" };
        var factory = new FakeVapixFactory();
        factory.Add(a.Id);
        factory.Add(b.Id);
        var other = factory.Add(c.Id);
        using var plugin = await StartPluginAsync(new FakeRepository(a, b, c), factory);
        var ctx = new PluginContext(plugin, a, b, c); // a new computer: no choice yet, nothing selected on the Devices page
        var vm = new SnapshotReportViewModel(ctx, new FakeUi(), new ExportSettingsStore(TempFiles.NewSettingsPath()));
        try
        {
            Assert.Equal("Choose cameras", vm.DeviceChoice.ButtonText);
            Assert.Equal("Choose cameras, then press Create snapshots.", vm.WaitingText);
            Assert.False(vm.CreateSnapshotsCommand.CanExecute(null));
            await vm.RefreshAllAsync();
            Assert.Empty(vm.Tiles);
            Assert.Empty(ctx.Payloads);

            vm.PickDevices = () =>
            {
                vm.DeviceChoice.Set(Oadm.Sdk.Client.Controls.DeviceChoice.Devices([], ["Building A"]));
                return Task.FromResult(true);
            };
            await vm.ChooseDevicesCommand.ExecuteAsync(null);
            Assert.Empty(ctx.Payloads); // choosing reads nothing before Create snapshots
            Assert.Equal("Selected cameras: Tag Building A", vm.DeviceChoice.ButtonText);
            Assert.True(vm.CreateSnapshotsCommand.CanExecute(null));

            await vm.CreateSnapshotsCommand.ExecuteAsync(null);
            Assert.Equal([a.Id, b.Id], vm.Tiles.Select(t => t.DeviceId).ToArray());
            Assert.Empty(other.Requests); // the other camera was not asked
            Assert.Contains("2 pictures from 2 cameras", vm.StatusLine, StringComparison.Ordinal);

            // A new choice after the first load reads at once.
            vm.PickDevices = () =>
            {
                vm.DeviceChoice.Set(Oadm.Sdk.Client.Controls.DeviceChoice.Devices([c.Id]));
                return Task.FromResult(true);
            };
            await vm.ChooseDevicesCommand.ExecuteAsync(null);
            Assert.Equal(c.Id, Assert.Single(vm.Tiles).DeviceId);
            Assert.Equal("Selected cameras: 10.0.0.50", vm.DeviceChoice.ButtonText);
        }
        finally
        {
            vm.Dispose();
        }

        // The next page on this computer starts with the last choice.
        using var again = new SnapshotReportViewModel(ctx, new FakeUi(), new ExportSettingsStore(TempFiles.NewSettingsPath()));
        Assert.Equal("Selected cameras: 10.0.0.50", again.DeviceChoice.ButtonText);
    }

    [Fact]
    public async Task Tile_refresh_and_preview()
    {
        var device = new FakeDevice();
        var factory = new FakeVapixFactory();
        var camera = factory.Add(device.Id);
        using var plugin = await StartPluginAsync(new FakeRepository(device), factory);
        var ui = new FakeUi();
        using var vm = new SnapshotReportViewModel(new PluginContext(plugin), ui, new ExportSettingsStore(TempFiles.NewSettingsPath())).AllCameras();
        await vm.RefreshAllAsync();
        var tile = Assert.Single(vm.Tiles);
        Assert.Equal(TileState.Ok, tile.State);
        Assert.Equal("1280x720", tile.Resolution);

        camera.Handler = (_, _) => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized));
        await tile.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(TileState.Error, tile.State);
        Assert.Equal("Unauthorized - HTTP 401 (check the credentials)", tile.StatusText);
        Assert.NotNull(tile.Jpeg); // the older picture stays

        tile.PreviewCommand.Execute(null);
        Assert.Same(tile, Assert.Single(ui.Previews));
        using var preview = new SnapshotPreviewViewModel(tile, ui);
        Assert.Contains(preview.Facts, f => f.Label == "MAC address" && f.Value == "B8:A4:4F:63:13:39");
    }

    [Fact]
    public async Task Export_remembers_site_and_technician_and_saves_the_pdf()
    {
        var device = new FakeDevice();
        var multi = new FakeDevice { Address = "10.0.0.32", Serial = "ACCC8E77E3A1", Model = "AXIS P3727-PLE" };
        var factory = new FakeVapixFactory();
        factory.Add(device.Id);
        factory.Add(multi.Id).Sources = [new VideoSource(1, "Camera 1", 0, []), new VideoSource(2, "Camera 2", 1, [])];
        using var plugin = await StartPluginAsync(new FakeRepository(device, multi), factory);
        var settingsPath = TempFiles.NewSettingsPath();
        var picker = new MemorySavePicker();
        var ui = new FakeUi
        {
            OnExport = async export =>
            {
                Assert.Equal(string.Empty, export.Site);
                Assert.False(export.ExportCommand.CanExecute(null));
                Assert.NotNull(export.SiteError);
                export.Site = "Plant 7 / North";
                export.Technician = "Max";
                export.SavePicker = picker;
                // The report date starts as this computer's date (a midnight between opening and here gives yesterday).
                Assert.InRange(export.Date!.Value, DateOnly.FromDateTime(DateTime.Today).AddDays(-1), DateOnly.FromDateTime(DateTime.Today));
                Assert.Equal("Maintenance report - Plant 7 _ North - " + export.DateText + ".pdf", export.SuggestedFileName);
                string? closed = null;
                export.CloseRequested += (_, path) => closed = path;
                await export.ExportCommand.ExecuteAsync(null);
                Assert.Null(export.ErrorMessage);
                Assert.Equal("C:/reports/" + picker.Suggested, closed);
            },
        };
        using var vm = new SnapshotReportViewModel(new PluginContext(plugin), ui, new ExportSettingsStore(settingsPath)).AllCameras();
        await vm.RefreshAllAsync();
        vm.Tiles[^1].IsSelected = false; // 2 of 3 tiles

        await vm.ExportPdfCommand.ExecuteAsync(null);

        var pdf = PdfInspector.Open(picker.Written.ToArray());
        Assert.Equal(2, pdf.PageCount);
        Assert.Contains("Plant 7 / North", pdf.PageText(0), StringComparison.Ordinal);
        Assert.Equal(2, pdf.Images().Count);
        var remembered = new ExportSettingsStore(settingsPath).Load();
        Assert.Equal(("Plant 7 / North", "Max", "C:/reports"), (remembered.Site, remembered.Technician, remembered.LastFolder));

        var again = new ExportReportViewModel(new PluginContext(plugin), [vm.Tiles[0]], new ExportSettingsStore(settingsPath), new FixedLocalTime(new DateTimeOffset(2026, 10, 8, 23, 30, 0, TimeSpan.FromHours(2))));
        Assert.Equal(("Plant 7 / North", "Max"), (again.Site, again.Technician));
        Assert.Equal(new DateOnly(2026, 10, 8), again.Date); // this computer's date, whatever the time zone of the test machine
        again.DateText = "7.10.2026";
        Assert.Null(again.Date);
        Assert.NotNull(again.DateError);
        Assert.False(again.ExportCommand.CanExecute(null));
        again.DateText = "2026-10-07";
        Assert.Equal(new DateOnly(2026, 10, 7), again.Date);
        Assert.EndsWith("2026-10-07.pdf", again.SuggestedFileName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_cancelled_in_the_save_dialog_does_nothing()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5)) { SnapshotDelay = TimeSpan.Zero };
        var ctx = new FakeApiContext(api);
        using var vm = new SnapshotReportViewModel(ctx, new FakeUi(), new ExportSettingsStore(TempFiles.NewSettingsPath())).AllCameras();
        await vm.RefreshAllAsync();
        var export = new ExportReportViewModel(ctx, [.. vm.Tiles.Take(3)], new ExportSettingsStore(TempFiles.NewSettingsPath())) { SavePicker = new MemorySavePicker { Cancel = true } };
        export.Site = "x";

        await export.ExportCommand.ExecuteAsync(null);

        Assert.DoesNotContain(SnapshotReportMethods.GenerateReport, ctx.Calls);
        Assert.False(export.IsBusy);
    }

    [Fact]
    public async Task Export_through_the_fake_api_saves_its_placeholder_pdf()
    {
        using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5)) { SnapshotDelay = TimeSpan.Zero };
        var ctx = new FakeApiContext(api);
        using var vm = new SnapshotReportViewModel(ctx, new FakeUi(), new ExportSettingsStore(TempFiles.NewSettingsPath())).AllCameras();
        await vm.RefreshAllAsync();
        var picker = new MemorySavePicker();
        var export = new ExportReportViewModel(ctx, [.. vm.Tiles.Take(2)], new ExportSettingsStore(TempFiles.NewSettingsPath())) { SavePicker = picker, Site = "Fake" };

        await export.ExportCommand.ExecuteAsync(null);

        Assert.Null(export.ErrorMessage);
        Assert.Equal(1, PdfInspector.Open(picker.Written.ToArray()).PageCount);
        Assert.Equal([SnapshotReportMethods.GenerateReport, SnapshotReportMethods.ReportStatus, SnapshotReportMethods.ReadReport, SnapshotReportMethods.DeleteReport],
            ctx.Calls.SkipWhile(c => c != SnapshotReportMethods.GenerateReport).Distinct().ToArray());
    }

    private static async Task<SnapshotReportPlugin> StartPluginAsync(FakeRepository devices, FakeVapixFactory vapix)
    {
        var plugin = new SnapshotReportPlugin();
        await plugin.StartAsync(new CoreContext(devices, vapix), CancellationToken.None);
        return plugin;
    }

    private sealed class CoreContext(IDeviceRepository devices, IVapixClientFactory vapix) : Oadm.Sdk.Plugins.ICorePluginContext
    {
        public IDeviceRepository Devices { get; } = devices;
        public IVapixClientFactory Vapix { get; } = vapix;
        public Oadm.Sdk.Tasks.ITaskRunner Tasks => throw new NotSupportedException();
        public Oadm.Sdk.Plugins.IPluginSettings Settings => throw new NotSupportedException();
        public Microsoft.Extensions.Logging.ILogger Logger { get; } = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }
}

/// <summary>A clock at a fixed moment in a fixed time zone (UTC+2), so date checks do not depend on the test machine.</summary>
internal sealed class FixedLocalTime(DateTimeOffset now) : TimeProvider
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("OADM test +2", TimeSpan.FromHours(2), "OADM test +2", "OADM test +2");

    public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

    public override TimeZoneInfo LocalTimeZone => Zone;
}
