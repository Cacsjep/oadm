using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Client.Api;
using Oadm.Plugins.SnapshotReport.Client;
using Oadm.Plugins.SnapshotReport.Report;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.SnapshotReport.Tests;

/// <summary>
/// HARD RULE "scale to thousands of devices" for the snapshot report: a page with 5,000 tiles loads snapshots only for
/// shown rows with bounded requests, cancels loads of rows scrolled away and keeps a bounded number of pictures; the
/// server lists and snapshots 5,000 devices with bounded parallelism.
/// </summary>
public sealed class ScaleTests
{
    private const int Count = 5000;

    private static readonly byte[] TinyJpeg = FakeOadmApi.TestPicture(160, 90, "camera 1", "TEST", DateTimeOffset.UtcNow, 1);

    [Fact]
    [Trait("Category", "Perf")]
    public async Task Page_with_5000_tiles_loads_only_shown_rows_cancels_scrolled_away_loads_and_bounds_memory()
    {
        var ctx = new ManyTilesContext(Count);
        var ui = new TrackingUi();
        using var vm = new SnapshotReportViewModel(ctx, ui, new ExportSettingsStore(TempFiles.NewSettingsPath()));

        var watch = Stopwatch.StartNew();
        await vm.RefreshAllAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var open = watch.ElapsedMilliseconds;

        Assert.Equal(Count, vm.Tiles.Count);
        Assert.Equal(SnapshotReportViewModel.InitialLoadCount, ctx.Snapshots); // not one request per tile
        Assert.Equal(TileState.Waiting, vm.Tiles[Count - 1].State);
        Assert.Contains("5,000 pictures from 5,000 cameras · 5,000 selected", vm.StatusLine, StringComparison.Ordinal);

        // The view: 4 tiles per row, 1,250 rows of which about 6 are shown at a time.
        vm.AvailableWidth = 4 * (vm.TileWidth + SnapshotReportViewModel.TileSpacing) + 20;
        Assert.Equal(4, vm.Columns);
        Assert.Equal(Count / 4, vm.Rows.Count);

        // Fast scroll from top to bottom: rows come and go before their snapshots arrive.
        watch.Restart();
        var shown = new Queue<SnapshotTileRow>();
        for (var r = 0; r < vm.Rows.Count; r++)
        {
            vm.SetRowRealized(vm.Rows[r], true);
            shown.Enqueue(vm.Rows[r]);
            if (shown.Count > 6)
            {
                vm.SetRowRealized(shown.Dequeue(), false);
            }
        }

        var scroll = watch.ElapsedMilliseconds;
        await vm.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.All(shown.SelectMany(r => r.Tiles), t => Assert.Equal(TileState.Ok, t.State));
        Assert.True(ctx.Cancelled > 0, "loads of rows scrolled away are cancelled");
        Assert.True(ctx.Snapshots < Count / 2, $"{ctx.Snapshots} snapshot requests for a fast scroll");

        // Slow scroll back up over 150 rows (600 tiles), every row loads: pictures of hidden tiles are dropped.
        foreach (var row in shown)
        {
            vm.SetRowRealized(row, false);
        }

        for (var r = 0; r < 150; r++)
        {
            vm.SetRowRealized(vm.Rows[r], true);
            if (r >= 6)
            {
                vm.SetRowRealized(vm.Rows[r - 6], false);
            }

            await vm.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }

        var bound = SnapshotReportViewModel.MaxCachedImages + SnapshotReportViewModel.InitialLoadCount + (6 * 4);
        Assert.True(vm.CachedImageCount <= bound, $"{vm.CachedImageCount} cached pictures");
        Assert.True(ui.Alive <= bound, $"{ui.Alive} decoded pictures alive");
        Assert.True(ctx.MaxInFlight <= SnapshotReportViewModel.LoadParallelism, $"{ctx.MaxInFlight} snapshot requests in flight");
        Assert.Equal(TileState.Waiting, vm.Rows[0].Tiles[0].State); // dropped, reloads when shown again
        Assert.Null(vm.Rows[0].Tiles[0].Jpeg);

        // Select all, search and the status line over 5,000 tiles.
        watch.Restart();
        vm.IsAllSelected = false;
        vm.IsAllSelected = true;
        vm.SearchText = "10.0.1";
        vm.SearchText = null;
        _ = vm.StatusLine;
        var bulk = watch.ElapsedMilliseconds;

        Assert.True(open < 5000, $"Refresh took {open} ms");
        Assert.True(scroll < 1000, $"Scrolling over 1,250 rows took {scroll} ms");
        Assert.True(bulk < 1000, $"Select all, search and status took {bulk} ms");
    }

    [Fact]
    [Trait("Category", "Timing")] // time budget
    public async Task Page_with_5000_tiles_selects_and_filters_in_one_pass()
    {
        var ctx = new ManyTilesContext(Count);
        using var vm = new SnapshotReportViewModel(ctx, new TrackingUi(), new ExportSettingsStore(TempFiles.NewSettingsPath()));
        await vm.RefreshAllAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var notifications = 0;
        vm.PropertyChanged += (_, e) => notifications += e.PropertyName == nameof(vm.StatusLine) ? 1 : 0;

        var watch = Stopwatch.StartNew();
        vm.IsAllSelected = false;
        Assert.Contains("0 selected", vm.StatusLine, StringComparison.Ordinal);
        vm.SearchText = "10.0.2";
        Assert.All(vm.FilteredTiles, t => Assert.StartsWith("10.0.2", t.Title, StringComparison.Ordinal));
        vm.IsAllSelected = true;
        watch.Stop();

        Assert.True(notifications < 10, $"{notifications} status line notifications for two bulk selections"); // not one per tile
        Assert.Equal(vm.FilteredTiles.Count, vm.Tiles.Count(t => t.IsSelected));
        Assert.True(watch.ElapsedMilliseconds < 1000, $"took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task Server_lists_5000_devices_with_one_device_list_and_bounded_requests()
    {
        var devices = Devices();
        var repository = new CountingRepository(devices);
        var factory = new SharedCameraFactory();
        using var service = new SnapshotService(repository, factory);

        var watch = Stopwatch.StartNew();
        var all = await service.ListSourcesAsync(new ListSourcesRequest(), CancellationToken.None);
        var some = await service.ListSourcesAsync(new ListSourcesRequest { DeviceIds = [.. devices.Select(d => d.Id)] }, CancellationToken.None, refreshSources: false);
        watch.Stop();

        Assert.Equal(Count, all.Tiles.Count);
        Assert.Equal(Count, some.Tiles.Count);
        Assert.Equal(0, repository.Finds); // 5,000 ids: one list call, not 5,000 lookups
        Assert.True(factory.MaxConcurrent <= SnapshotReportPluginInfo.Parallelism, $"{factory.MaxConcurrent} device requests at once");
        Assert.Equal(Count, factory.SourceReads); // the second list reused the sources of the first
        Assert.True(watch.ElapsedMilliseconds < 5000, $"took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task Report_of_5000_devices_takes_snapshots_with_bounded_workers_at_a_reduced_size()
    {
        var devices = Devices();
        var repository = new CountingRepository(devices);
        var factory = new SharedCameraFactory();
        using var service = new SnapshotService(repository, factory);
        using var jobs = new ReportJobs(service);
        var request = new ReportRequest { Site = "Site", Items = [.. devices.Select(d => new ReportItem { DeviceId = d.Id, Camera = 1 })] };

        var status = jobs.Start(request, "1.0"); // 5,000 snapshots fit in one report
        jobs.Delete(status.JobId);

        var taken = 0;
        var watch = Stopwatch.StartNew();
        var entries = await jobs.CollectAsync(request, (done, _) => Volatile.Write(ref taken, done), CancellationToken.None);
        watch.Stop();

        Assert.Equal(Count, entries.Length);
        Assert.All(entries, e => Assert.True(e.Snapshot.IsOk, e.Snapshot.Error));
        Assert.Equal(Count, taken);
        Assert.Equal(0, repository.Finds);
        Assert.True(factory.MaxConcurrent <= SnapshotReportPluginInfo.Parallelism, $"{factory.MaxConcurrent} device requests at once");
        Assert.Equal((ReportJobs.HugeReportMaxWidth, 540), ReportJobs.SnapshotSize(request));
        Assert.All(factory.Camera.Requests.Take(10), r => Assert.Contains("resolution=640x360", r, StringComparison.Ordinal));
        Assert.True(watch.ElapsedMilliseconds < 10000, $"took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Large_reports_take_smaller_snapshots()
    {
        static ReportRequest Request(int items) => new() { Items = [.. Enumerable.Range(0, items).Select(_ => new ReportItem { DeviceId = Guid.NewGuid() })] };

        Assert.Equal((1920, 1080), ReportJobs.SnapshotSize(Request(ReportJobs.LargeReportItems)));
        Assert.Equal((1280, 720), ReportJobs.SnapshotSize(Request(ReportJobs.LargeReportItems + 1)));
        Assert.Equal((960, 540), ReportJobs.SnapshotSize(Request(ReportJobs.HugeReportItems + 1)));
    }

    private static List<IDeviceInfo> Devices() =>
        [.. Enumerable.Range(0, Count).Select(i => (IDeviceInfo)new FakeDevice
        {
            Address = string.Create(CultureInfo.InvariantCulture, $"10.{i / 65536}.{i / 256 % 256}.{i % 256}"),
            Serial = string.Create(CultureInfo.InvariantCulture, $"B8A44F{i:X6}"),
        })];

    /// <summary>Page backend with <c>count</c> single-source cameras; snapshots answer after a short delay.</summary>
    internal sealed class ManyTilesContext(int count) : ICorePluginClientContext
    {
        private readonly string _list = SnapshotReportJson.Serialize(new ListSourcesResult
        {
            Tiles = [.. Enumerable.Range(0, count).Select(i =>
            {
                var address = string.Create(CultureInfo.InvariantCulture, $"10.0.{i / 256}.{i % 256}");
                return new SnapshotTile
                {
                    Device = new DeviceFacts { DeviceId = Guid.NewGuid(), Address = address, Model = "AXIS P3265-V", Serial = "B8A44F631339", Status = "Ok" },
                    Title = address,
                };
            })],
        });

        private readonly string _snapshot = SnapshotReportJson.Serialize(new SnapshotResult { JpegBase64 = Convert.ToBase64String(TinyJpeg), Width = 160, Height = 90, CapturedUtc = DateTimeOffset.UtcNow });
        private int _snapshots;
        private int _cancelled;
        private int _inFlight;
        private int _maxInFlight;

        public int Snapshots => Volatile.Read(ref _snapshots);

        public int Cancelled => Volatile.Read(ref _cancelled);

        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public async Task<string?> InvokeAsync(string method, string? payloadJson, CancellationToken ct)
        {
            if (method == SnapshotReportMethods.ListSources)
            {
                return _list;
            }

            Interlocked.Increment(ref _snapshots);
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxInFlight)) && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen)
            {
            }

            try
            {
                await Task.Delay(2, ct);
                return _snapshot;
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _cancelled);
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    /// <summary>Decodes into small images that count how many are alive (not disposed).</summary>
    private sealed class TrackingUi : ISnapshotReportUi
    {
        private int _alive;

        public int Alive => Volatile.Read(ref _alive);

        public IImage? Decode(byte[] jpeg, int decodeWidth)
        {
            Interlocked.Increment(ref _alive);
            return new TrackedImage(this);
        }

        public void ShowPreview(SnapshotTileViewModel tile)
        {
        }

        public Task ShowExportAsync(ExportReportViewModel dialog) => Task.CompletedTask;

        private sealed class TrackedImage(TrackingUi owner) : IImage, IDisposable
        {
            private int _disposed;

            public Size Size => new(160, 90);

            public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
            {
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    Interlocked.Decrement(ref owner._alive);
                }
            }
        }
    }

    private sealed class CountingRepository(IReadOnlyList<IDeviceInfo> devices) : IDeviceRepository
    {
        private int _finds;

        public int Finds => Volatile.Read(ref _finds);

        public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) => Task.FromResult(devices);

        public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct)
        {
            Interlocked.Increment(ref _finds);
            return Task.FromResult(devices.FirstOrDefault(d => d.Id == id));
        }
    }

    /// <summary>Every device is the same fake camera (tiny JPEG); counts concurrent device sessions and source reads.</summary>
    private sealed class SharedCameraFactory : Oadm.Sdk.Vapix.IVapixClientFactory
    {
        public SharedCameraFactory()
        {
            Camera.Handler = (_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(TinyJpeg) };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                return Task.FromResult(response);
            };
        }

        public CountingCamera Camera { get; } = new();

        public int MaxConcurrent => Math.Max(Camera.MaxConcurrent, Camera.MaxConcurrentSources);

        public int SourceReads => Camera.SourceReads;

        public Task<Oadm.Sdk.Vapix.IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct) => Task.FromResult<Oadm.Sdk.Vapix.IVapixClient>(Camera);
    }

    /// <summary><see cref="FakeCamera"/> that also counts concurrent source reads.</summary>
    private sealed class CountingCamera : Oadm.Sdk.Vapix.IVapixClient
    {
        private readonly FakeCamera _inner = new();
        private int _sourcesRunning;
        private int _maxSources;
        private int _sourceReads;

        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Handler
        {
            get => _inner.Handler;
            set => _inner.Handler = value;
        }

        public Uri BaseAddress => _inner.BaseAddress;

        public List<string> Requests => _inner.Requests;

        public int MaxConcurrent => _inner.MaxConcurrent;

        public int MaxConcurrentSources => Volatile.Read(ref _maxSources);

        public int SourceReads => Volatile.Read(ref _sourceReads);

        public Task<Oadm.Sdk.Vapix.BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct) => _inner.GetBasicDeviceInfoAsync(ct);

        public Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct) => _inner.ListParametersAsync(groups, ct);

        public Task RestartAsync(CancellationToken ct) => _inner.RestartAsync(ct);

        public Task<IReadOnlyList<Oadm.Sdk.Vapix.DeviceApi>> GetApiListAsync(CancellationToken ct) => _inner.GetApiListAsync(ct);

        public async Task<IReadOnlyList<Oadm.Sdk.Vapix.VideoSource>> GetVideoSourcesAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _sourceReads);
            var now = Interlocked.Increment(ref _sourcesRunning);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxSources)) && Interlocked.CompareExchange(ref _maxSources, now, seen) != seen)
            {
            }

            try
            {
                await Task.Yield();
                return await _inner.GetVideoSourcesAsync(ct);
            }
            finally
            {
                Interlocked.Decrement(ref _sourcesRunning);
            }
        }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => _inner.SendAsync(request, ct);
    }
}

/// <summary>The page rendered offscreen with 5,000 tiles (one headless session at a time: see <see cref="HeadlessSessions"/>).</summary>
[Collection(HeadlessSessions.Name)]
public sealed class HeadlessScaleTests
{
    private const int Count = 5000;

    [Fact]
    [Trait("Category", "Perf")]
    public async Task Rendered_page_with_5000_tiles_creates_controls_and_requests_only_for_shown_rows()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var ctx = new ScaleTests.ManyTilesContext(Count);
        var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
        var (tileControls, requests, rows, columns) = await session.Dispatch(async () =>
        {
            var view = new SnapshotReportView();
            var vm = new SnapshotReportViewModel(ctx, view, new ExportSettingsStore(TempFiles.NewSettingsPath()));
            view.DataContext = vm;
            var window = new Window { Width = 1500, Height = 980, Content = new Border { Padding = new Thickness(16), Child = view } };
            window.Show();
            await vm.RefreshAllAsync();
            Pump(); // layout: the panel realizes the shown rows, they start loading
            await vm.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Pump();
            var frame = window.CaptureRenderedFrame();
            if (!string.IsNullOrEmpty(outDir) && frame is not null)
            {
                Directory.CreateDirectory(outDir);
                frame.Save(Path.Combine(outDir, "snapshot-report-5000-tiles.png"));
            }

            frame?.Dispose();
            var tiles = window.GetVisualDescendants().OfType<Border>().Count(b => b.Classes.Contains("tile"));
            var result = (tiles, ctx.Snapshots, vm.Rows.Count, vm.Columns);
            window.Close();
            vm.Dispose();
            return result;
        }, CancellationToken.None);

        Assert.True(columns > 1, $"{columns} columns");
        Assert.Equal((Count + columns - 1) / columns, rows);
        Assert.True(tileControls < 200, $"{tileControls} tile controls for {Count} tiles"); // virtualized: only shown rows exist
        Assert.True(requests < 200, $"{requests} snapshot requests for {Count} tiles");
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
}
