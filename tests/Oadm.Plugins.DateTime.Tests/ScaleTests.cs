using System.Diagnostics;

using Oadm.Plugins.DateAndTime.Client;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.DateAndTime.Tests;

/// <summary>HARD RULE "scale to thousands of devices": opening the dialog and CanRun with 5,000 devices.</summary>
public sealed class ScaleTests
{
    private const int Count = 5000;

    [Fact]
    public async Task Dialog_with_5000_devices_reads_only_the_first_device()
    {
        var devices = Enumerable.Range(0, Count).Select(i => (IDeviceInfo)new FakeDevice(Guid.NewGuid(), $"10.0.{i / 256}.{i % 256}")).ToList();
        var ctx = new CountingContext();

        var watch = Stopwatch.StartNew();
        var vm = new DateTimeDialogViewModel(devices);
        await vm.LoadCurrentAsync(ctx, CancellationToken.None);
        watch.Stop();

        Assert.Equal(1, ctx.Queries);
        Assert.Equal(Count, vm.NtsDeviceCount);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"Opening took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    [Trait("Category", "Perf")] // time budget; a CI runner can take over a second
    public void CanRun_over_5000_devices_is_cheap()
    {
        IReadOnlyList<DeviceApi> apis = [.. Enumerable.Range(0, 120).Select(i => new DeviceApi("api-" + i, "1." + i)), .. Fixture.Modern];
        var devices = Enumerable.Range(0, Count).Select(_ => new FakeDevice(Guid.NewGuid()) { Apis = apis }).ToList();
        var plugin = new DateTimeTaskPlugin();

        var watch = Stopwatch.StartNew();
        var runnable = 0;
        for (var round = 0; round < 10; round++)
        {
            runnable += devices.Count(plugin.CanRun);
        }

        watch.Stop();
        Assert.Equal(10 * Count, runnable);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"50,000 CanRun calls took {watch.ElapsedMilliseconds} ms");
    }

    private sealed class CountingContext : ITaskDialogContext
    {
        private int _queries;

        public int Queries => _queries;

        public Task<string?> QueryAsync(Guid deviceId, string method, string? payloadJson, CancellationToken ct)
        {
            Interlocked.Increment(ref _queries);
            return Task.FromResult<string?>(null);
        }

        public Task<UploadedFile> UploadAsync(string localPath, IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();
    }
}
