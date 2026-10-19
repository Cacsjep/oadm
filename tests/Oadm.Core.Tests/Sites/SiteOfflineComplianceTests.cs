using Oadm.Core.Compliance;
using Oadm.Core.Devices;
using Oadm.Core.Sites;
using Oadm.Core.Tests.Compliance;
using Oadm.Sdk.Compliance;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Testing;
using Oadm.Tests.Shared;

namespace Oadm.Core.Tests.Sites;

/// <summary>
/// Compliance and Site offline: every check (Reachable and Login works too) is Not checked "Site offline: &lt;site&gt;"
/// for the devices of an offline site, a site error is Not checked (never Failed or Could not check), and no compliance
/// event turns on because of it.
/// </summary>
public sealed class SiteOfflineComplianceTests
{
    private static readonly Guid Reachable = BuiltInComplianceTypes.ReachableCheckId;
    private static readonly Guid LoginWorks = BuiltInComplianceTypes.LoginWorksCheckId;

    [Fact]
    public async Task EveryCheckIsNotCheckedForTheDevicesOfAnOfflineSite()
    {
        var sites = new FakeSiteTransport();
        var site = sites.AddSite("Building B");
        await using var rig = await ComplianceRig.StartAsync(sites: sites);
        await using var publisher = new SiteStatusPublisher(sites, rig.Devices, rig.Devices.Changes);
        publisher.Start();
        var device = await AddAtSiteAsync(rig, site.Id, DeviceStatus.Unreachable, "ACCC8E100001");
        var ok = await AddAtSiteAsync(rig, site.Id, DeviceStatus.Ok, "ACCC8E100002");
        var check = await rig.SaveAsync("test.read");
        await rig.WaitForAsync(Reachable, device.Id, ComplianceOutcome.Failed);
        await rig.WaitForAsync(check.Id, ok.Id, ComplianceOutcome.Passed);
        var reachableEvent = ComplianceEngine.EventTypeId(Reachable);
        await Eventually.TrueAsync(() => rig.Events.States.GetValueOrDefault(reachableEvent)?.Contains(device.Id) == true, "Reachable fails on the device");

        sites.SetOnline(site.Id, false);

        foreach (var (checkId, deviceId) in new[] { (Reachable, device.Id), (LoginWorks, device.Id), (Reachable, ok.Id), (check.Id, ok.Id) })
        {
            await Eventually.TrueAsync(
                () => rig.Result(checkId, deviceId) is { Outcome: ComplianceOutcome.NotChecked, Detail: "Site offline: Building B" },
                "Not checked: Site offline");
        }

        // A failing check of a device whose site went offline turns its event off; nothing turns on.
        await Eventually.TrueAsync(() => rig.Events.States.GetValueOrDefault(reachableEvent)?.Contains(device.Id) == false, "the event is off");
        var evaluations = rig.Plugin.Read.Evaluations.Count;
        rig.Engine.CheckNow([check.Id], null);
        await Eventually.StaysTrueAsync(() => rig.Plugin.Read.Evaluations.Count == evaluations, "an offline site is not read", TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task ASiteErrorIsNotCheckedNeverFailedOrCouldNotCheck()
    {
        var sites = new FakeSiteTransport();
        var site = sites.AddSite("Building B");
        await using var rig = await ComplianceRig.StartAsync(sites: sites);
        var device = await AddAtSiteAsync(rig, site.Id, DeviceStatus.Ok, "ACCC8E100003");
        rig.Plugin.Read.ThrowException = new HttpRequestException(
            HttpRequestError.ConnectionError, "busy", SiteException.For(site.Id, "Building B", SiteProblem.Busy));

        var check = await rig.SaveAsync("test.read");

        await rig.WaitForAsync(check.Id, device.Id, ComplianceOutcome.NotChecked, "a site error is Not checked");
        await Eventually.TrueAsync(() => rig.Result(check.Id, device.Id)!.Detail == "Site Building B is busy.", "the site's text");
        Assert.DoesNotContain(device.Id, rig.Events.States.GetValueOrDefault(ComplianceEngine.EventTypeId(check.Id)) ?? []);
    }

    private static Task<Device> AddAtSiteAsync(ComplianceRig rig, Guid siteId, DeviceStatus status, string serial) =>
        rig.Devices.AddAsync(new Device
        {
            Serial = serial,
            Address = "192.168.0.20",
            SiteId = siteId,
            Model = "P3265-V",
            FirmwareVersion = "12.11.77",
            Status = status,
            Category = DeviceCategory.Camera,
        }, CancellationToken.None);
}
