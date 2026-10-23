using System.Diagnostics;

using Oadm.Core.DeviceGroups;
using Oadm.Core.Devices;
using Oadm.Sdk.Devices;

using Xunit.Abstractions;

namespace Oadm.Core.Tests.DeviceGroups;

/// <summary>The membership audit's changes (docs/spec/device-scope.md, Decisions "Membership"): entered and left groups per change.</summary>
public sealed class DeviceGroupMembershipTests(ITestOutputHelper output) : DeviceGroupTestBase
{
    [Fact]
    public async Task AnAddEntersTheGroupsThatCoverTheDeviceAndAMoveLeavesTheOldSitesGroups()
    {
        var a = Sites.Add("Building A");
        var b = Sites.Add("Building B");
        await SaveGroupAsync("A", sites: [a]);
        await SaveGroupAsync("B", sites: [b]);
        await SaveGroupAsync("Local", thisServer: true);
        await SaveGroupAsync("Not this one", sites: [a], except: ["ACCC8E000001"]);

        var device = new Device { Serial = "ACCC8E000001", SiteId = a, AddedByAllDevices = true };
        var added = await DeviceGroupMembership.EnteredAsync(Views, [device], Ct);
        var entered = Assert.Single(added);
        Assert.Equal(("A", true, "ACCC8E000001"), (entered.GroupName, entered.Entered, Assert.Single(entered.Serials)));

        var moved = device.Clone();
        moved.SiteId = b;
        var changes = await DeviceGroupMembership.ChangesAsync(Views, [(device, moved)], Ct);
        Assert.Equal([("A", false), ("B", true)], changes.Select(c => (c.GroupName, c.Entered)));

        Assert.Empty(await DeviceGroupMembership.ChangesAsync(Views, [(device, device)], Ct));
        Assert.Equal("1 device (moved): ACCC8E000001", DeviceGroupMembership.Detail(changes[0], "moved"));
    }

    [Fact]
    public async Task AGroupDeviceCountsForItsOwnSerialsOnly()
    {
        var single = await SaveGroupAsync("Single", serials: ["ACCC8E000002"]);
        var other = await SaveGroupAsync("Other", thisServer: true);

        var byOther = new Device { Serial = "ACCC8E000002", AddedByAllDevices = false, AddedByGroupId = other.Group.Id };
        Assert.Equal(["Other"], (await DeviceGroupMembership.EnteredAsync(Views, [byOther], Ct)).Select(c => c.GroupName));

        var byAll = new Device { Serial = "ACCC8E000002", AddedByAllDevices = true };
        Assert.Equal(["Other", "Single"], (await DeviceGroupMembership.EnteredAsync(Views, [byAll], Ct)).Select(c => c.GroupName));
        Assert.NotEqual(Guid.Empty, single.Group.Id);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task ThirtyThousandNewDevicesAgainstTwoHundredGroups()
    {
        var sites = Enumerable.Range(0, 100).Select(i => Sites.Add("Site " + i)).ToArray();
        for (var i = 0; i < DeviceGroup.MaxGroups; i++)
        {
            await SaveGroupAsync("Group " + i, thisServer: i == 0, sites: [sites[i % sites.Length]], serials: [$"ACCC8E{i:X6}"]);
        }

        var devices = Enumerable.Range(0, DeviceLimits.MaxDevices)
            .Select(i => (IDeviceInfo)new Device { Serial = $"ACCC8E{i:X6}", SiteId = i % 3 == 0 ? null : sites[i % sites.Length], AddedByAllDevices = true })
            .ToList();

        var watch = Stopwatch.StartNew();
        var changes = await DeviceGroupMembership.EnteredAsync(Views, devices, Ct);
        var elapsed = watch.ElapsedMilliseconds;
        output.WriteLine($"30,000 devices against 200 groups in {elapsed} ms, {changes.Count} groups entered");
        Assert.Equal(DeviceGroup.MaxGroups, changes.Count);
        Assert.True(elapsed < 10_000, $"too slow: {elapsed} ms");
    }
}
