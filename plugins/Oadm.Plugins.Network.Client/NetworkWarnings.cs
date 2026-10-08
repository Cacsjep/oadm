using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Network.Client;

/// <summary>The reachability warnings both network dialogs show in the confirmation before a risky change.</summary>
public static class NetworkWarnings
{
    public const string Dhcp =
        "Devices may get another address. OADM finds them again. A DHCP server must be on their network.";

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
                ? "The device gets a new IP address. OADM follows it."
                : $"{moving} of {devices.Count} devices get a new IP address. OADM follows them.";
        }

        yield return MaskAndRouter;
    }
}
