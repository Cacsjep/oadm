using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Network.Client;

/// <summary>The reachability warnings both network dialogs show before an IPv4 change (must be acknowledged).</summary>
public static class NetworkWarnings
{
    public const string Dhcp =
        "With DHCP the devices get their address from the DHCP server. It can differ from the current address; OADM keeps " +
        "the current address and finds a device again with the next mDNS scan while it is unreachable. Make sure a DHCP " +
        "server is available on their network.";

    public const string MaskAndRouter =
        "A wrong subnet mask or default router makes devices unreachable from the server. Make sure the server can reach the new addresses.";

    /// <summary>How many devices move, and that OADM follows them; empty addresses count as unchanged.</summary>
    public static IEnumerable<string> Static(IReadOnlyList<IDeviceInfo> devices, IReadOnlyList<string> addresses)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(addresses);
        var moving = devices.Where((d, i) => !string.Equals(d.Address, addresses.ElementAtOrDefault(i), StringComparison.OrdinalIgnoreCase)).Count();
        if (moving > 0)
        {
            yield return moving == 1 && devices.Count == 1
                ? "The device gets a new IPv4 address. OADM follows it when it answers there with the same serial number; otherwise the OADM device record keeps the current address."
                : $"{moving} of {devices.Count} devices get a new IPv4 address. OADM follows each device when it answers there with the same serial number; otherwise its OADM device record keeps the current address.";
        }

        yield return MaskAndRouter;
    }
}
