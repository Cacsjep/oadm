using Oadm.Core.Sites;

namespace Oadm.Server.Devices;

/// <summary>
/// The detail of the audit entry "Device moved": "from 10.0.0.5 to 10.0.0.20" on the server's own network, and with a site
/// on either side every side names its route ("from Building B 192.168.0.20 to This server 10.0.0.20", docs/spec/sites.md
/// "Moving devices").
/// </summary>
public static class DeviceMoveText
{
    /// <summary>The server's own network in the text.</summary>
    public const string ThisServer = "This server";

    public static string Of(DeviceRoute from, string fromAddress, DeviceRoute to, string toAddress, ISiteStates? sites)
    {
        if (from.IsLocal && to.IsLocal)
        {
            return $"from {fromAddress} to {toAddress}";
        }

        return $"from {Name(from, sites)} {fromAddress} to {Name(to, sites)} {toAddress}";
    }

    private static string Name(DeviceRoute route, ISiteStates? sites) =>
        route.SiteId is { } site ? sites?.NameOf(site) ?? site.ToString() : ThisServer;
}
