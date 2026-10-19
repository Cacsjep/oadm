using System.Diagnostics;
using System.Text.Json.Nodes;

using Oadm.Core.Compliance;
using Oadm.Core.Devices;
using Oadm.Sdk.Compliance;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Testing;

using Xunit.Abstractions;

namespace Oadm.Core.Tests.Compliance;

/// <summary>
/// The engine at the device limit: 30,000 devices and the most checks (50), one check with all 30,000 devices chosen one by
/// one; turning compliance on, a burst of device changes and the events stay fast and change only what changed.
/// </summary>
public sealed class ComplianceScaleTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Perf")]
    public async Task ThirtyThousandDevicesWithFiftyChecks()
    {
        await using var rig = await ComplianceRig.StartAsync(enabled: false);
        var devices = new List<Device>();
        for (var i = 0; i < DeviceLimits.MaxDevices; i++)
        {
            devices.Add(await rig.Devices.AddAsync(new Device
            {
                Serial = i.ToString("X12", System.Globalization.CultureInfo.InvariantCulture),
                Address = $"10.{i / 65536}.{i / 256 % 256}.{i % 256}",
                Model = "P3265-V",
                FirmwareVersion = i % 10 == 0 ? "10.12.1" : "12.11.77",
                Status = DeviceStatus.Ok,
                Category = DeviceCategory.Camera,
                Tags = [i % 2 == 0 ? "Even" : "Odd"],
            }, CancellationToken.None));
        }

        var chosen = await rig.SaveAsync(BuiltInComplianceTypes.FirmwareId, new JsonObject { ["minimum"] = "11.0" }, name: "Chosen",
            devices: new DeviceSelector { Mode = DeviceSelectorMode.Devices, DeviceIds = [.. devices.Select(d => d.Id)] });
        for (var c = 0; c < ComplianceStore.MaxChecks - 3; c++)
        {
            await rig.SaveAsync(BuiltInComplianceTypes.FirmwareId, new JsonObject { ["minimum"] = "11." + c }, name: "Firmware " + c,
                devices: new DeviceSelector { Mode = DeviceSelectorMode.Devices, Tags = [c % 2 == 0 ? "Even" : "Odd"] });
        }

        var watch = Stopwatch.StartNew();
        await rig.Engine.SetEnabledAsync(true, CancellationToken.None);
        await Eventually.TrueAsync(
            () => rig.Engine.ListChecks().Single(c => c.Check.Id == chosen.Id).Failed == DeviceLimits.MaxDevices / 10,
            "every device checked", TimeSpan.FromMinutes(5));
        var enable = watch.Elapsed;
        var results = rig.Engine.ListChecks().Sum(c => c.Passed + c.Failed + c.NotChecked + c.NotApplicable + c.CouldNotCheck);
        watch.Restart();
        await rig.Engine.FlushAsync(CancellationToken.None); // in chunks of 5,000 rows per transaction
        var write = watch.Elapsed;
        Assert.Equal(results, (await rig.Store.ListResultsAsync(CancellationToken.None)).Count);

        // 1,000 devices get the new firmware: only their results and events change.
        var fullReports = rig.Events.FullReports.Values.Sum();
        watch.Restart();
        foreach (var device in devices.Where((_, i) => i % 10 == 0).Take(1_000))
        {
            await rig.Devices.UpdateAsync(device.Id, d => d.FirmwareVersion = "12.11.77", CancellationToken.None);
        }

        await Eventually.TrueAsync(
            () => rig.Engine.ListChecks().Single(c => c.Check.Id == chosen.Id).Failed == DeviceLimits.MaxDevices / 10 - 1_000,
            "the changed devices pass", TimeSpan.FromMinutes(5));
        var changes = watch.Elapsed;

        using var snapshot = rig.Engine.Watch();
        watch.Restart();
        var list = rig.Engine.ListChecks();
        var listing = watch.Elapsed;

        output.WriteLine($"{results:N0} results; enable {enable.TotalMilliseconds:N0} ms; 1,000 device changes {changes.TotalMilliseconds:N0} ms; " +
            $"written in {write.TotalMilliseconds:N0} ms; list of {list.Count} checks {listing.TotalMilliseconds:N0} ms; snapshot {snapshot.Snapshot.Count:N0} results");
        Assert.Equal(rig.Events.FullReports.Values.Sum(), fullReports); // changes go out one by one, never as full reports
        Assert.Equal(DeviceLimits.MaxDevices / 10 - 1_000, rig.Events.States[ComplianceEngine.EventTypeId(chosen.Id)].Count);
        Assert.True(enable < TimeSpan.FromSeconds(60), $"enable took {enable}");
        Assert.True(changes < TimeSpan.FromSeconds(60), $"changes took {changes}");
        Assert.True(listing < TimeSpan.FromSeconds(1), $"the list took {listing}");
        Assert.True(write < TimeSpan.FromSeconds(300), $"writing took {write}");
    }
}
