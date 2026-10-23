using System.Diagnostics;
using System.Text.Json.Nodes;

using Oadm.Core.Auth;
using Oadm.Core.Compliance;
using Oadm.Core.DeviceGroups;
using Oadm.Core.Devices;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Testing;

using Xunit.Abstractions;

namespace Oadm.Core.Tests.Compliance;

/// <summary>
/// Checks owned by a device group at the device limit: 30,000 devices, a group of half of them, 10 checks of the group and
/// 10 of all devices. The group's checks cover only its devices, the group user's list is filtered fast, and a change of
/// the group's rules moves every result of its checks.
/// </summary>
public sealed class ComplianceOwnerScaleTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Perf")]
    public async Task ThirtyThousandDevicesAndChecksOfAGroup()
    {
        await using var rig = await ComplianceRig.StartAsync(enabled: false);
        var odds = new HashSet<Guid>();
        for (var i = 0; i < DeviceLimits.MaxDevices; i++)
        {
            var added = await rig.Devices.AddAsync(new Device
            {
                Serial = i.ToString("X12", System.Globalization.CultureInfo.InvariantCulture),
                Address = $"10.{i / 65536}.{i / 256 % 256}.{i % 256}",
                Model = "P3265-V",
                FirmwareVersion = "10.12.1",
                Status = DeviceStatus.Ok,
                Category = DeviceCategory.Camera,
                Tags = [i % 2 == 0 ? "Even" : "Odd"],
            }, CancellationToken.None);
            if (i % 2 == 1)
            {
                odds.Add(added.Id);
            }
        }

        var even = (await rig.Tags.CreateAsync("Even", TagColor.None, CancellationToken.None)).Id;
        var odd = (await rig.Tags.CreateAsync("Odd", TagColor.None, CancellationToken.None)).Id;
        var group = (await rig.Groups.SaveAsync(null, "Even", "", new DeviceGroupRules(false, [], [even], [], []), 0, CancellationToken.None)).Group;
        var view = await rig.Views.ForScopeAsync(DeviceScope.Group(group.Id), "operator", CancellationToken.None);
        var mine = new List<Guid>();
        for (var c = 0; c < 10; c++)
        {
            var check = new ComplianceCheck
            {
                Name = "Group " + c,
                TypeId = BuiltInComplianceTypes.FirmwareId,
                Settings = new JsonObject { ["minimum"] = "11." + c },
                Devices = new DeviceSelector { Mode = DeviceSelectorMode.All },
                Severity = Sdk.Compliance.ComplianceSeverity.Problem,
            };
            mine.Add((await rig.Service.SaveAsync(check, view, null, false, CancellationToken.None)).Check.Id);
            await rig.SaveAsync(BuiltInComplianceTypes.FirmwareId, new JsonObject { ["minimum"] = "11." + c }, name: "All " + c);
        }

        var watch = Stopwatch.StartNew();
        await rig.Engine.SetEnabledAsync(true, CancellationToken.None);
        await Eventually.TrueAsync(() => rig.Engine.ListChecks().Where(c => mine.Contains(c.Check.Id)).All(c => c.Failed == DeviceLimits.MaxDevices / 2),
            "the group's checks cover half of the devices", TimeSpan.FromMinutes(5));
        var enable = watch.Elapsed;

        watch.Restart();
        var list = rig.Service.ListChecks(view);
        var listing = watch.Elapsed;
        Assert.Equal(mine.Order(), list.Select(c => c.Check.Id).Order());

        using (var results = rig.Engine.Watch())
        {
            watch.Restart();
            var visible = list.Select(c => c.Check.Id).ToHashSet();
            var filtered = results.Snapshot.Count(r => visible.Contains(r.CheckId));
            var filtering = watch.Elapsed;
            Assert.Equal(10 * DeviceLimits.MaxDevices / 2, filtered);
            output.WriteLine($"snapshot {results.Snapshot.Count:N0} results, {filtered:N0} of the group's checks filtered in {filtering.TotalMilliseconds:N0} ms");
            Assert.True(filtering < TimeSpan.FromSeconds(5), $"filtering took {filtering}");
        }

        // The group's rules change to the other half: every result of its checks moves.
        watch.Restart();
        await rig.Groups.SaveAsync(group.Id, "Even", "", new DeviceGroupRules(false, [], [odd], [], []), 0, CancellationToken.None);
        await Eventually.TrueAsync(() =>
        {
            using var now = rig.Engine.Watch();
            var first = mine[0];
            var results = now.Snapshot.Where(r => r.CheckId == first).ToList();
            return results.Count == DeviceLimits.MaxDevices / 2 && results.All(r => odds.Contains(r.DeviceId));
        }, "the results follow the group", TimeSpan.FromMinutes(5));
        var moved = watch.Elapsed;

        output.WriteLine($"enable {enable.TotalMilliseconds:N0} ms; the group's list {listing.TotalMilliseconds:N1} ms; group change {moved.TotalMilliseconds:N0} ms");
        Assert.True(enable < TimeSpan.FromSeconds(120), $"enable took {enable}");
        Assert.True(listing < TimeSpan.FromSeconds(1), $"the list took {listing}");
        Assert.True(moved < TimeSpan.FromSeconds(120), $"the group change took {moved}");
    }
}
