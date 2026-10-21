using Grpc.Core;

using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// Fake mode: discovery and imports at the sample sites (docs/spec/sites.md, Client: add page). Two cameras a scan finds answer
/// through "Building B" (one also through this server), an address of 192.168.0.x answers through Building B, and
/// 192.168.0.20 answers through Building B and this server with different cameras (an import line without a site lists both).
/// </summary>
public sealed partial class FakeOadmApi
{
    /// <summary>The camera that answers at 192.168.0.20 on this server's own network.</summary>
    public const string FakeServerCameraSerial = "ACCC8E7A0C20";

    public async Task<DiscoverySession> ProbeLineAsync(ProbeAddressRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        Guid? site = null;
        if (!string.IsNullOrWhiteSpace(request.Site))
        {
            string name = request.Site.Trim();
            KeyValuePair<Guid, string> found = FakeSites.FirstOrDefault(s => string.Equals(s.Value, name, StringComparison.OrdinalIgnoreCase));
            if (found.Value is null)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, $"There is no site named {name}."));
            }

            site = found.Key;
        }

        string id = await ProbeAddressAsync(request.Address, request.UserName.Length > 0 ? request.UserName : null,
            request.Password.Length > 0 ? request.Password : null, ct).ConfigureAwait(false);
        lock (_gate)
        {
            _sessions[id].Site = site;
            if (site == FakeSiteOfflineId)
            {
                _sessions[id].Problem = "Site offline: " + FakeSites[FakeSiteOfflineId]; // like the server: nothing answers, the line can wait
            }
        }

        return new DiscoverySession { SessionId = id, SiteId = site?.ToString() ?? "" };
    }

    /// <summary>
    /// Two of the sample cameras a scan finds answer through the online sample site "Building B" (the P1455-LE, and the
    /// factory-default P3268-LV, which also answers through this server): the Site column and its tooltip.
    /// </summary>
    private static void AddSiteSamples(List<DiscoveredDevice> list, FakeSession session)
    {
        _ = session;
        foreach (DiscoveredDevice device in list.Where(d => d.Serial is "B8A44FB4C5D6" or "B8A44F99CC01"))
        {
            device.SiteId = FakeSiteOnlineId.ToString();
            device.SiteName = FakeSites[FakeSiteOnlineId];
            if (device.Serial == "B8A44F99CC01")
            {
                device.OtherSites.Add(""); // also answers through this server
            }
        }
    }

    /// <summary>
    /// "Add manually" and import lines at the sites: a line with a site answers through it only (a 192.168.0.x address); a
    /// line without one at 192.168.0.20 also finds another camera on this server's own network.
    /// </summary>
    private static void ManualAtSites(List<DiscoveredDevice> list, FakeSession session, string host)
    {
        if (list.Count == 0)
        {
            return;
        }

        bool siteAddress = host.StartsWith("192.168.0.", StringComparison.Ordinal);
        if (session.Site is { } site)
        {
            if (!siteAddress || session.Problem is not null)
            {
                list.Clear(); // nothing answers there through the site
                return;
            }

            list[0].SiteId = site.ToString();
            list[0].SiteName = FakeSites[site];
            return;
        }

        if (siteAddress && list[0].SiteId.Length == 0)
        {
            list[0].SiteId = FakeSiteOnlineId.ToString();
            list[0].SiteName = FakeSites[FakeSiteOnlineId];
        }

        if (host == "192.168.0.20")
        {
            DiscoveredDevice server = new()
            {
                Serial = FakeServerCameraSerial,
                Address = "192.168.0.20",
                Model = "AXIS M3106-L Mk II",
                Status = DeviceStatus.CredentialsRequired,
                AuthState = AuthState.Authenticated,
                AuthUserName = "root",
                CredentialId = "list:1",
                Source = DiscoverySource.Manual,
                EnteredAddress = host,
                Scheme = "https",
                Category = DeviceCategory.Camera,
                ProductType = "Box Camera",
            };
            list.Add(server);
        }
    }
}
