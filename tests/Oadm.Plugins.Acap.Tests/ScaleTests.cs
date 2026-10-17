using System.Diagnostics;
using System.Globalization;

using Oadm.Plugins.Acap.Client;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

using Xunit.Abstractions;

namespace Oadm.Plugins.Acap.Tests;

/// <summary>
/// HARD RULE "scale to thousands of devices": the Applications dialog with 30,000 selected devices checks a bounded
/// sample with bounded parallelism (never one query per device), builds the rows in one pass, and CanRun stays cheap.
/// </summary>
public sealed class ScaleTests : IDisposable
{
    private const int Count = DeviceLimits.MaxDevices;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oadm-acap-scale-" + Guid.NewGuid().ToString("N"));

    private readonly ITestOutputHelper _output;

    public ScaleTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static List<IDeviceInfo> Devices() =>
        [.. Enumerable.Range(0, Count).Select(i => (IDeviceInfo)new FakeDevice
        {
            Address = string.Create(CultureInfo.InvariantCulture, $"10.0.{i / 256}.{i % 256}"),
            Model = i % 2 == 0 ? "P3265-V" : "M3106",
            FirmwareVersion = i % 1000 == 7 ? "11.11.124" : "12.11.77",
        })];

    [Fact]
    public async Task Dialog_with_30000_devices_checks_a_bounded_sample() => await OpenWithTheMostDevicesAsync();

    [Fact]
    [Trait("Category", "Perf")]
    public async Task Dialog_with_30000_devices_opens_fast()
    {
        var elapsed = await OpenWithTheMostDevicesAsync();
        _output.WriteLine($"Open the dialog with 30,000 devices and check the sample: {elapsed.TotalMilliseconds:0} ms");
        Assert.True(elapsed < TimeSpan.FromSeconds(2), $"Opening and checking took {elapsed.TotalMilliseconds:0} ms");
    }

    private async Task<TimeSpan> OpenWithTheMostDevicesAsync()
    {
        var devices = Devices();
        var ctx = new SlowContext();
        var path = Path.Combine(_dir, "hello.eap");
        await File.WriteAllBytesAsync(path, EapBuilder.FromManifest(EapBuilder.Manifest(appName: "hello", version: "1.2.0")));

        var watch = Stopwatch.StartNew();
        var vm = new AcapDialogViewModel(ctx, devices, new FakePicker(path));
        await vm.InitializeAsync();
        await vm.LoadPackageAsync(path);
        watch.Stop();

        Assert.Equal(Count, vm.Compatibility.Count);
        Assert.Equal(AcapDialogViewModel.CompatibilitySampleSize, vm.CheckedCount);
        Assert.Equal(AcapDialogViewModel.CompatibilitySampleSize, ctx.Queries); // the first device (application list) is cached
        Assert.True(ctx.MaxInFlight <= AcapDialogViewModel.MaxParallelQueries, $"{ctx.MaxInFlight} queries in flight");
        Assert.Equal(AcapDialogViewModel.CompatibilitySampleSize, vm.CompatibleCount);
        Assert.Equal("Checked at install", vm.Compatibility[Count - 1].Verdict);
        Assert.False(vm.Compatibility[Count - 1].IsError);
        Assert.Equal("25 of 25 checked devices can install this package; the other 29,975 are checked by the task before installing.", vm.CompatibilitySummary);
        Assert.True(vm.InstallCommand.CanExecute(null));
        Assert.Equal("Actions apply to all 30,000 selected devices.", vm.ScopeText);
        return watch.Elapsed;
    }

    [Fact]
    public void Sample_takes_one_device_per_model_and_firmware_first() => SampleTheMostDevices();

    [Fact]
    [Trait("Category", "Perf")]
    public void Sample_of_30000_devices_is_fast()
    {
        var elapsed = SampleTheMostDevices();
        _output.WriteLine($"Sample of 30,000 devices: {elapsed.TotalMilliseconds:0} ms");
        Assert.True(elapsed < TimeSpan.FromMilliseconds(500), $"Sampling took {elapsed.TotalMilliseconds:0} ms");
    }

    private static TimeSpan SampleTheMostDevices()
    {
        var choices = Devices().Select(d => new DeviceChoice(d)).ToList();

        var watch = Stopwatch.StartNew();
        var sample = AcapDialogViewModel.SampleIndexes(choices, 25);
        watch.Stop();

        Assert.Equal(25, sample.Count);
        Assert.Contains(7, sample); // M3106 on 11.11.124
        Assert.Contains(0, sample);
        Assert.Contains(1, sample);
        Assert.Equal(sample.Order(), sample);
        return watch.Elapsed;
    }

    [Fact]
    [Trait("Category", "Perf")] // time budget; a CI runner can take over a second
    public void CanRun_over_30000_devices_is_cheap()
    {
        IReadOnlyList<DeviceApi> apis = [.. Enumerable.Range(0, 120).Select(i => new DeviceApi("api-" + i, "1." + i)), new("application", "1.3")];
        var devices = Enumerable.Range(0, Count).Select(_ => new FakeDevice { Apis = apis }).ToList();
        var plugin = new AcapTaskPlugin();

        var watch = Stopwatch.StartNew();
        var runnable = 0;
        for (var round = 0; round < 10; round++)
        {
            runnable += devices.Count(plugin.CanRun);
        }

        watch.Stop();
        Assert.Equal(10 * Count, runnable);
        _output.WriteLine($"300,000 CanRun calls: {watch.ElapsedMilliseconds} ms");
        Assert.True(watch.ElapsedMilliseconds < 2000, $"300,000 CanRun calls took {watch.ElapsedMilliseconds} ms");
    }

    /// <summary>Every device runs AXIS OS 12.11 on aarch64 without the app; answers after a short delay.</summary>
    private sealed class SlowContext : ITaskDialogContext
    {
        private readonly string _state = FakeDialogContext.State().ToJson();
        private int _queries;
        private int _inFlight;
        private int _maxInFlight;

        public int Queries => _queries;

        public int MaxInFlight => _maxInFlight;

        public async Task<string?> QueryAsync(Guid deviceId, string method, string? payloadJson, CancellationToken ct)
        {
            Interlocked.Increment(ref _queries);
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = _maxInFlight) && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen)
            {
            }

            await Task.Delay(5, ct);
            Interlocked.Decrement(ref _inFlight);
            return _state;
        }

        public Task<UploadedFile> UploadAsync(string localPath, IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();
    }
}
