using System.Diagnostics;

using Oadm.Plugins.Users.Client;
using Oadm.Sdk.Client;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Users.Tests;

/// <summary>HARD RULE "scale to thousands of devices": the Users dialog and CanRun with 5,000 devices.</summary>
public sealed class ScaleTests
{
    private const int Count = 5000;

    [Fact]
    [Trait("Category", "Timing")] // time budget
    public async Task Users_dialog_with_5000_devices_reads_only_the_first_device()
    {
        var apis = Fixtures.P3265ApiList();
        var devices = Enumerable.Range(0, Count).Select(i => (IDeviceInfo)new ScaleDevice($"10.0.{i / 256}.{i % 256}", DeviceStatus.Ok, apis)).ToList();
        var ctx = new CountingContext();

        var watch = Stopwatch.StartNew();
        var vm = new UsersDialogViewModel(devices);
        await vm.LoadAsync(ctx, CancellationToken.None);
        vm.UserName = "operator1";
        vm.Password = "Secret-Passw0rd!";
        vm.ConfirmPassword = "Secret-Passw0rd!";
        watch.Stop();

        Assert.Equal(1, ctx.Queries); // never one query per selected device
        Assert.Contains("5000 devices", vm.Summary, StringComparison.Ordinal);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"Opening took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    [Trait("Category", "Perf")] // time budget; a CI runner can take over a second
    public void CanRun_over_5000_devices_is_cheap()
    {
        var apis = Fixtures.P3265ApiList();
        var devices = Enumerable.Range(0, Count).Select(i => new ScaleDevice("10.0.0.48", i % 10 == 0 ? DeviceStatus.Unreachable : DeviceStatus.Ok, apis)).ToList();
        var plugin = new UsersTaskPlugin();

        var watch = Stopwatch.StartNew();
        var runnable = 0;
        for (var round = 0; round < 10; round++)
        {
            runnable += devices.Count(plugin.CanRun);
        }

        watch.Stop();
        Assert.Equal(10 * (Count - (Count / 10)), runnable);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"50,000 CanRun calls took {watch.ElapsedMilliseconds} ms");
    }

    /// <summary>Like <see cref="FakeDevice"/> without parsing the API list fixture per device.</summary>
    private sealed record ScaleDevice(string Address, DeviceStatus Status, IReadOnlyList<DeviceApi> Apis) : IDeviceInfo
    {
        public Guid Id { get; } = Guid.NewGuid();

        public string Serial => "B8A44F631339";

        public string? HostName => null;

        public string? Model => "AXIS P3265-V";

        public string? FirmwareVersion => "12.11.77";

        public DeviceCategory Category => DeviceCategory.Camera;

        public bool HasVideo => true;
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
