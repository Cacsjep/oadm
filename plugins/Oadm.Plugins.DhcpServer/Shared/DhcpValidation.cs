using System.Globalization;

using Oadm.Plugins.DhcpServer.Protocol;
using Oadm.Plugins.Network.Model;

namespace Oadm.Plugins.DhcpServer;

/// <summary>
/// Field checks of the page (range) and the static lease dialog, shared by the page (errors while typing) and the server
/// (the same checks on Save). Messages are plain language and go directly under the field.
/// </summary>
public static class DhcpValidation
{
    /// <summary>Most addresses in the range (keeps the allocation scan and the lease table small).</summary>
    public const int MaxRangeSize = 65_536;

    public const int MaxNameLength = 63;

    /// <summary>Errors of the start and end address (null = fine) for the subnet of <paramref name="network"/>.</summary>
    public static (string? Start, string? End) Range(string? start, string? end, DhcpNetworkInfo? network)
    {
        var startError = Address(start, network, "Enter the first address clients can get.");
        var endError = Address(end, network, "Enter the last address clients can get.");
        if (startError is null && endError is null && Ipv4.TryParse(start, out var s) && Ipv4.TryParse(end, out var e))
        {
            if (e < s)
            {
                endError = "Must not be before the start address.";
            }
            else if ((ulong)e - s + 1 > MaxRangeSize)
            {
                endError = string.Create(CultureInfo.InvariantCulture, $"The range has more than {MaxRangeSize:N0} addresses.");
            }
        }

        return (startError, endError);
    }

    /// <summary>Errors of a static lease (format and subnet only; collisions are checked against the lease list).</summary>
    public static Dictionary<string, string> StaticLease(string? mac, string? address, string? name, DhcpNetworkInfo? network)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(mac))
        {
            errors["Mac"] = "Enter the MAC address of the device.";
        }
        else if (!MacAddress.TryParse(mac, out var value))
        {
            errors["Mac"] = "Enter a MAC address, e.g. B8:A4:4F:63:13:39.";
        }
        else if (!MacAddress.IsUnicast(value))
        {
            errors["Mac"] = "Must be the MAC address of a device.";
        }

        if (Address(address, network, "Enter the IP address the device always gets.") is { } addressError)
        {
            errors["Address"] = addressError;
        }

        if (name is { Length: > MaxNameLength })
        {
            errors["Name"] = string.Create(CultureInfo.InvariantCulture, $"At most {MaxNameLength} characters.");
        }

        return errors;
    }

    /// <summary>An IPv4 address inside the subnet, not the network, broadcast or server address.</summary>
    public static string? Address(string? text, DhcpNetworkInfo? network, string emptyMessage)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return emptyMessage;
        }

        if (!Ipv4.TryParse(text, out var address))
        {
            return "Enter an IPv4 address, e.g. 10.0.0.100.";
        }

        if (network is null)
        {
            return null;
        }

        if (!network.Contains(address))
        {
            return $"Must be inside the subnet {network.Subnet}.";
        }

        if (network.PrefixLength <= 30 && address == network.NetworkAddress)
        {
            return "Must not be the network address of the subnet.";
        }

        if (network.PrefixLength <= 30 && address == network.BroadcastAddress)
        {
            return "Must not be the broadcast address of the subnet.";
        }

        if (address == network.ServerAddress)
        {
            return "This is the address of the OADM server.";
        }

        return null;
    }
}
