using System.Diagnostics;

using Oadm.Core.Devices;
using Oadm.Server.Tests.Support;

using Xunit.Abstractions;

using SdkDeviceStatus = Oadm.Sdk.Devices.DeviceStatus;

namespace Oadm.Server.Tests.Perf;

/// <summary>
/// Every device changes its status, one write each, on the full server (event rules, change feeds, caches listening): does
/// the cost per change stay flat when the device count grows? An outage changes every device. Opt-in: OADM_LIMITS_DEVICES.
/// </summary>
[Trait("Category", "Perf")]
public sealed class StatusChangeLimitsTests(ITestOutputHelper output)
{
    [Fact]
    public async Task EveryDeviceTurnsUnreachableOneWriteEach()
    {
        var sizes = (Environment.GetEnvironmentVariable("OADM_LIMITS_DEVICES") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse).ToList();
        foreach (var size in sizes)
        {
            await using var host = await TestServerHost.StartAsync();
            var ids = await ServerScale.SeedDevicesAsync(host, size);
            var devices = host.Get<DeviceRepository>();
            var watch = Stopwatch.StartNew();
            await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (id, ct) =>
                await devices.UpdateAsync(id, d => d.Status = SdkDeviceStatus.Unreachable, ct));
            watch.Stop();
            output.WriteLine($"{size:N0} devices turn Unreachable: {watch.Elapsed.TotalSeconds:N1} s, {watch.Elapsed.TotalMilliseconds / size:N2} ms per change");
        }
    }
}
