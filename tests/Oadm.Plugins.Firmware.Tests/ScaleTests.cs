extern alias fwclient;

using System.Diagnostics;
using System.Globalization;

using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

using ClientStatus = fwclient::Oadm.Plugins.Firmware.FirmwareStatusInfo;
using DialogViewModel = fwclient::Oadm.Plugins.Firmware.Client.FirmwareDialogViewModel;

// Own namespace: inside Oadm.Plugins.Firmware.* the server's copies of the shared types would win.
namespace Oadm.FirmwareDialogTests;

/// <summary>
/// HARD RULE "scale to thousands of devices": the Upgrade firmware dialog with 5,000 selected devices reads the
/// status of a bounded number of devices with bounded parallelism, evaluates every row in one pass, and CanRun stays cheap.
/// </summary>
public sealed class FirmwareScaleTests
{
    private const int Count = 5000;

    private static Device[] Devices() =>
        [.. Enumerable.Range(0, Count).Select(i => new Device(
            string.Create(CultureInfo.InvariantCulture, $"10.{i / 65536}.{i / 256 % 256}.{i % 256}"),
            i % 25 == 0 ? "Q6135-LE" : "P3265-V",
            i % 3 == 0 ? "12.11.77" : "11.11.160"))];

    [Fact]
    [Trait("Category", "Perf")] // about 2 s; flaky on its time budget when the whole unit suite runs in parallel
    public async Task Dialog_with_5000_devices_reads_a_bounded_number_of_statuses_and_evaluates_fast()
    {
        var devices = Devices();
        var ctx = new SlowStatusContext();
        var watch = Stopwatch.StartNew();
        using var vm = new DialogViewModel(ctx, devices, new FakeFileSource());
        await vm.SelectFileAsync("P3265-V_12_11_77.bin", CancellationToken.None);
        await vm.LoadStatusAsync(CancellationToken.None);
        var open = watch.ElapsedMilliseconds;

        Assert.Equal(DialogViewModel.StatusPreloadCount, ctx.Queries); // not one query per selected device
        Assert.True(ctx.MaxInFlight <= DialogViewModel.MaxParallelStatusQueries, $"{ctx.MaxInFlight} status queries in flight");
        Assert.Equal("Rollback to 11.11.100 possible", vm.Devices[0].FirmwareState);
        Assert.Null(vm.Devices[4000].FirmwareState); // read when the grid shows the row

        // The grid shows a row far down: only that row is read.
        await vm.EnsureStatusAsync(vm.Devices[4000]);
        await vm.EnsureStatusAsync(vm.Devices[4000]);
        Assert.Equal(DialogViewModel.StatusPreloadCount + 1, ctx.Queries);
        Assert.Equal("Rollback to 11.11.100 possible", vm.Devices[4000].FirmwareState);

        watch.Restart();
        vm.SelectedMode = vm.Modes[1];
        vm.AllowDowngrade = true;
        var evaluate = watch.ElapsedMilliseconds;

        // 1 in 3 is up to date (12.11.77), 1 in 25 is a Q6135-LE (wrong product), the others upgrade.
        var upToDate = devices.Count(d => d.FirmwareVersion == "12.11.77" && d.Model == "P3265-V");
        Assert.Contains("already up to date", vm.Summary, StringComparison.Ordinal);
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"of {Count:N0} device(s) will be updated, {upToDate:N0} already up to date"), vm.Summary, StringComparison.Ordinal);
        Assert.True(open < 2000, $"Opening took {open} ms");
        Assert.True(evaluate < 1000, $"Re-evaluating 5,000 rows twice took {evaluate} ms");
    }

    [Fact]
    [Trait("Category", "Perf")] // time budget; a CI runner can take over a second
    public void CanRun_over_5000_devices_is_cheap()
    {
        DeviceApi[] apis = [.. Enumerable.Range(0, 120).Select(i => new DeviceApi("api-" + i, "1." + i)), new("fwmgr", "1.10")];
        var devices = Enumerable.Range(0, Count).Select(_ => new Oadm.Plugins.Firmware.Tests.FakeDevice(DeviceStatus.Ok, "P3265-V", "11.11.160", apis)).ToList();
        var plugin = new Oadm.Plugins.Firmware.FirmwareTaskPlugin();

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

    /// <summary>Answers every status query after a short delay and records how many run at once.</summary>
    private sealed class SlowStatusContext : ITaskDialogContext
    {
        private readonly string _status = new ClientStatus { Supported = true, ActiveVersion = null, InactiveVersion = "11.11.100", IsCommitted = true }.ToJson();
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
            return _status;
        }

        public Task<UploadedFile> UploadAsync(string localPath, IProgress<double>? progress, CancellationToken ct) => throw new NotSupportedException();
    }
}
