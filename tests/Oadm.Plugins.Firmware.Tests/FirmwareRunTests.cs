using System.Diagnostics;

using Oadm.Plugins.Firmware.Online;
using Oadm.Sdk.Devices;
using StepRun = Oadm.Sdk.Testing.StepRun;

namespace Oadm.Plugins.Firmware.Tests;

/// <summary>
/// What the device tasks of one run share (README "Payload"): the payload parsed and indexed once, one read of axis.com per
/// version (its failure too), and the MyAxis sign-in only for devices that install a version from axis.com.
/// </summary>
public sealed class FirmwareRunTests : IDisposable
{
    private static readonly FirmwareTaskTimings Fast = new()
    {
        PollInterval = TimeSpan.FromMilliseconds(2),
        RestartTimeout = TimeSpan.FromSeconds(30),
        ProbeTimeout = TimeSpan.FromSeconds(30),
        CommitRetryDelay = TimeSpan.FromMilliseconds(1),
    };

    private const string VersionFolder = "/ftp/pub_soft/MPQT/P3265-V/12_11_147/";

    private readonly FakeAxisDownloads _downloads = new();
    private readonly FakeAxisSite _site = new();

    public void Dispose() => _downloads.Dispose();

    private static string Online() => new FirmwarePayload { Online = [new FirmwareOnline("MPQT", "P3265-V", "12.11.147", 11)] }.ToJson();

    private async Task<(RecordingContext Ctx, FakeAxisDevice Device, Exception? Error)> RunAsync(FirmwareTaskPlugin plugin, string payload, IDeviceInfo? device = null, FakeFiles? files = null, string model = "P3265-V")
    {
        var fake = new FakeAxisDevice { ProdNbr = model, NewVersion = model == "P3265-V" ? "12.11.147" : "12.11.77" };
        var ctx = new RecordingContext(fake, files ?? new FakeFiles()) { AxisDownloads = _downloads };
        try
        {
            await StepRun.RunAsync(ctx.Steps, () => plugin.ExecuteAsync(ctx, device ?? new FakeDevice(model: model), payload, CancellationToken.None));
            return (ctx, fake, null);
        }
        catch (InvalidOperationException ex)
        {
            return (ctx, fake, ex);
        }
    }

    [Fact]
    public async Task The_devices_of_a_run_read_each_version_on_axis_com_once()
    {
        var plugin = new FirmwareTaskPlugin(Fast, TimeProvider.System, _site);
        var payload = Online();

        var runs = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => RunAsync(plugin, payload)));

        Assert.All(runs, r => Assert.Null(r.Error));
        Assert.All(runs, r => Assert.Equal(_downloads.Content, r.Device.UploadedBytes));
        Assert.Single(_site.Requests, p => p == VersionFolder);
    }

    [Fact]
    public async Task A_failed_read_of_axis_com_is_shared_by_the_run()
    {
        var plugin = new FirmwareTaskPlugin(Fast, TimeProvider.System, _site);
        var payload = Online();
        _site.Failing.Add(VersionFolder);

        var first = await RunAsync(plugin, payload);
        _site.Failing.Clear();
        var second = await RunAsync(plugin, payload);

        Assert.Equal("axis.com could not be reached. Nothing was changed.", first.Error?.Message);
        Assert.Equal(first.Error?.Message, second.Error?.Message);
        Assert.Single(_site.Requests, p => p == VersionFolder);
        Assert.DoesNotContain("upgrade", second.Device.Methods);

        // Another run (another payload) reads again.
        var other = await RunAsync(plugin, new FirmwarePayload { Online = [new FirmwareOnline("MPQT", "P3265-V", "12.11.147", 11)], Direction = FirmwareDirection.Upgrade }.ToJson());
        Assert.Null(other.Error);
        Assert.Equal(2, _site.Requests.Count(p => p == VersionFolder));
    }

    [Fact]
    public async Task A_device_that_installs_a_chosen_file_needs_no_MyAxis_sign_in()
    {
        var plugin = new FirmwareTaskPlugin(Fast, TimeProvider.System, _site);
        var files = new FakeFiles();
        var q = files.Add("Q6135-LE_12_11_77.bin", Fixture.Image(seed: 3));
        var payload = new FirmwarePayload { FileId = q.Id, FileName = q.Name, Online = [new FirmwareOnline("MPQT", "P3265-V", "12.11.147", 11)] }.ToJson();
        _downloads.HasSession = false; // the sign-in expired

        var file = await RunAsync(plugin, payload, files: files, model: "Q6135-LE");
        var online = await RunAsync(plugin, payload, files: files);

        Assert.Null(file.Error);
        Assert.Equal(Fixture.Image(seed: 3), file.Device.UploadedBytes);
        Assert.Equal("Sign in to MyAxis to download from axis.com. Nothing was changed.", online.Error?.Message);
        Assert.Equal("Validate file: Failed", StepRun.Lines(online.Ctx.Steps)[2]);
        Assert.DoesNotContain("upgrade", online.Device.Methods);
        Assert.Empty(_downloads.Calls);
        Assert.DoesNotContain(_site.Requests, p => p == VersionFolder); // refused before axis.com is asked
    }

    [Fact]
    public void A_run_is_kept_ten_minutes_and_at_most_sixteen_runs_at_a_time()
    {
        var time = new ManualTime();
        var cache = new FirmwareRunCache(time);
        var payload = Online();

        var run = cache.For(payload);
        Assert.Same(run, cache.For(payload));
        time.Now += FirmwareRunCache.KeepFor - TimeSpan.FromSeconds(1);
        Assert.Same(run, cache.For(payload));
        time.Now += TimeSpan.FromSeconds(1);
        Assert.NotSame(run, cache.For(payload)); // parsed and read again

        for (var i = 0; i < 40; i++)
        {
            cache.For(new FirmwarePayload { Online = [new FirmwareOnline("MPQT", "P3265-V", "12.11." + (100 + i), 11)] }.ToJson());
        }

        Assert.True(cache.Count <= FirmwareRunCache.MaxRuns, $"{cache.Count} runs kept");
        Assert.Throws<ArgumentException>(() => cache.For("""{"online":[]}"""));
    }

    [Fact]
    public void The_run_index_answers_like_the_payload_order()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        IReadOnlyList<FirmwareOnline> online =
        [
            new("MPQT", "P3265-V", "12.11.147", 11),
            new("MPQT", "P3265-V", "11.11.238", 11), // a second entry of the same product and major: the first wins
            new("MPQT", "P3265-V", "11.11.220", null, [a]),
            new("MPQT", "P3265-V", "11.11.192", null, [a, b]), // a again: the first that names it wins
            new("PACS", "C1410", "11.11.238", null),
        ];
        IReadOnlyList<FirmwareFile> files = [new("f1", "P3265-V_12_11_77.bin", [b]), new("f2", "Q6135-LE_12_11_77.bin")];
        var index = new FirmwareRunIndex(online, files);

        Assert.Equal(2, index.OnlineNaming(a));
        Assert.Equal(3, index.OnlineNaming(b));
        Assert.Equal(-1, index.OnlineNaming(Guid.NewGuid()));
        Assert.Equal(0, index.FileNaming(b));
        Assert.Equal([1], index.FilesByProduct);
        foreach (var (model, version) in new[] { ("P3265-V", "11.11.160"), ("AXIS P3265-V", "11.11.160"), ("P3265-V", "10.12.300"), ("C1410", "11.11.205"), ("C1410", null), ("P3265-LVE", "11.11.160"), ("", "11.11.160") })
        {
            Assert.Equal(FirmwareCompatibility.FindOnline(model, version, online), index.OnlineFor(model, version));
        }
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Choosing_for_100000_devices_of_a_run_with_named_devices_is_O1_per_device()
    {
        const int Count = DeviceLimits.MaxDevices;
        var ids = Enumerable.Range(0, Count).Select(_ => Guid.NewGuid()).ToArray();
        // Half the devices deviate from the usual version and are named; the payload grows with them (README "Payload").
        IReadOnlyList<FirmwareOnline> online =
        [
            new("MPQT", "P3265-V", "12.11.147", 11),
            new("MPQT", "P3265-V", "11.11.238", null, [.. ids.Where((_, i) => i % 2 == 0)]),
        ];
        var images = online.Select(o => OnlineFirmware.ImageOf(o, 0)).ToList();

        var watch = Stopwatch.StartNew();
        var index = new FirmwareRunIndex(online, []);
        var named = 0;
        foreach (var id in ids)
        {
            var (o, _, _) = FirmwareCompatibility.ChooseFor(id, "P3265-V", "11.11.160", online, images, [], [], FactoryDefaultMode.None, false, runIndex: index);
            named += o == 1 ? 1 : 0;
        }

        watch.Stop();
        Assert.Equal(Count / 2, named);
        Assert.True(watch.ElapsedMilliseconds < 5000, $"{Count:N0} devices took {watch.ElapsedMilliseconds} ms");
        Console.WriteLine($"ChooseFor for {Count:N0} devices: {watch.ElapsedMilliseconds} ms");
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
