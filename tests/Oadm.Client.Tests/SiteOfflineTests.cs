using Oadm.Client.Api;
using Oadm.Client.Devices;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;

using ContractStatus = Oadm.Contracts.V1.DeviceStatus;
using SdkStatus = Oadm.Sdk.Devices.DeviceStatus;

namespace Oadm.Client.Tests;

/// <summary>Site offline in the client: the status text, the amber icon, the device row and the fake mode's sample sites.</summary>
public sealed class SiteOfflineTests
{
    [Fact]
    public void TheStatusIsSiteOfflineInAmber()
    {
        Assert.Equal("Site offline", DeviceStatusInfo.ToText(ContractStatus.SiteOffline));
        Assert.Equal(PillKind.Warning, DeviceStatusInfo.ToKind(ContractStatus.SiteOffline));
        Assert.Equal(SdkStatus.SiteOffline, DeviceStatusInfo.ToSdk(ContractStatus.SiteOffline));
        Assert.True(DeviceIcon.IsWarning(SdkStatus.SiteOffline)); // the "warn" class: amber in the dark and the light theme
        Assert.Equal("Site offline", DeviceIcon.StatusMessage(SdkStatus.SiteOffline));
    }

    [Fact]
    public void TheDeviceRowCarriesTheStatusAndTheSite()
    {
        var site = Guid.NewGuid();
        var row = new DeviceRowViewModel(new Device { Id = Guid.NewGuid().ToString(), Serial = "ACCC8E000001", Address = "192.168.0.20", Status = ContractStatus.SiteOffline, SiteId = site.ToString() });

        Assert.Equal("Site offline", row.StatusText);
        Assert.True(row.IsStatusWarning);
        Assert.Equal(site, row.SiteId);
        IDeviceInfo info = row;
        Assert.Equal(SdkStatus.SiteOffline, info.Status);
        Assert.Equal(site, info.SiteId);
        Assert.True(row.Matches("site offline"));

        row.Update(new Device { Id = row.Id, Serial = "ACCC8E000001", Address = "10.0.0.20", Status = ContractStatus.Ok });
        Assert.Null(row.SiteId);
        Assert.Equal("OK", row.StatusText);
    }

    [Fact]
    public async Task FakeModeHasASiteOfflineThatRefusesLogins()
    {
        using var api = new FakeOadmApi(TimeSpan.Zero);
        var devices = await api.ListDevicesAsync(CancellationToken.None);
        var offline = devices.Where(d => d.Status == ContractStatus.SiteOffline).ToList();
        Assert.Equal(2, offline.Count);
        Assert.All(offline, d => Assert.Equal(FakeOadmApi.FakeSiteOfflineId.ToString(), d.SiteId));
        Assert.Contains(devices, d => d.SiteId == FakeOadmApi.FakeSiteOnlineId.ToString() && d.Status == ContractStatus.Ok);

        var login = await api.LogInDevicesAsync([offline[0].Id], "root", "pass", false, CancellationToken.None);
        Assert.Equal("Site offline: Warehouse", login.Results[0].Message);
        var tasks = await api.ListTaskPluginsAsync(CancellationToken.None);
        Assert.Contains(tasks.SelectMany(t => t.NotRunnableGroups), g => g.Reason == "Site offline: Warehouse");
    }
}
