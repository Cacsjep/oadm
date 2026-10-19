using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>Fake mode: two sample sites, "Building B" online and "Warehouse" offline, so the client shows Site offline devices.</summary>
public sealed partial class FakeOadmApi
{
    /// <summary>The online sample site.</summary>
    public static readonly Guid FakeSiteOnlineId = Guid.Parse("5b1e0c3a-7d42-4f6e-9a10-2c8d4e6f0b11");

    /// <summary>The offline sample site: its devices show Site offline.</summary>
    public static readonly Guid FakeSiteOfflineId = Guid.Parse("9e2d4f60-1a3b-4c5d-8e7f-0a1b2c3d4e22");

    private static readonly Dictionary<Guid, string> FakeSites = new()
    {
        [FakeSiteOnlineId] = "Building B",
        [FakeSiteOfflineId] = "Warehouse",
    };

    /// <summary>The name of a sample site, or null.</summary>
    public static string? FakeSiteName(string? siteId) =>
        Guid.TryParse(siteId, out var id) && FakeSites.TryGetValue(id, out var name) ? name : null;

    /// <summary>"Site offline: Warehouse" for a device of the offline sample site.</summary>
    private static string FakeSiteOfflineText(Device device) => Oadm.Sdk.Sites.SiteTexts.SiteOffline(FakeSiteName(device.SiteId));

    /// <summary>Devices at the sample sites (added after the other sample devices, so their positions stay).</summary>
    private void AddSiteSampleDevices()
    {
        Device atSite = CreateDevice("B8A44F70B001", "192.168.0.20", "AXIS P3265-V", "12.11.77", DeviceStatus.Ok);
        atSite.SiteId = FakeSiteOnlineId.ToString();
        _devices.Add(atSite);
        foreach (var (serial, address, model) in new[] { ("B8A44F70C001", "192.168.5.20", "AXIS M3106-L Mk II"), ("B8A44F70C002", "192.168.5.21", "AXIS P1468-LE") })
        {
            Device offline = CreateDevice(serial, address, model, "12.6.94", DeviceStatus.SiteOffline);
            offline.SiteId = FakeSiteOfflineId.ToString();
            _devices.Add(offline);
        }
    }
}
