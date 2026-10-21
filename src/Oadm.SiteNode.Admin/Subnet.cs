using System.Net;
using System.Net.Sockets;

namespace Oadm.SiteNode.Admin;

/// <summary>An IPv4 or IPv6 network: "10.20.0.0/16".</summary>
public readonly record struct Subnet(IPAddress Network, int PrefixLength)
{
    /// <summary>The widest extra network the app accepts: IPv4 /8, IPv6 /16.</summary>
    public const int MinPrefixIpv4 = 8;

    public const int MinPrefixIpv6 = 16;

    /// <summary>Parses "10.20.0.0/16" (the host part is cleared); null for anything else, for a network wider than /8 (IPv6 /16) and for special networks (loopback, link-local, multicast, 0.0.0.0/8).</summary>
    public static Subnet? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var parts = text.Trim().Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) || !int.TryParse(parts[1], out var prefix))
            return null;
        if (address.AddressFamily == AddressFamily.InterNetwork && parts[0].Split('.').Length != 4)
            return null;
        var ipv4 = address.AddressFamily == AddressFamily.InterNetwork;
        var max = ipv4 ? 32 : 128;
        if (prefix < (ipv4 ? MinPrefixIpv4 : MinPrefixIpv6) || prefix > max || !IsUsable(Mask(address, prefix)) || !IsUsable(address))
            return null;
        return new Subnet(Mask(address, prefix), prefix);
    }

    /// <summary>
    /// True when the node may use the address for devices: IPv4 except 0.0.0.0/8, loopback 127/8, link-local 169.254/16,
    /// multicast 224/4 and 240/4 (reserved, broadcast); global or unique-local IPv6 without a zone (not loopback,
    /// unspecified, link-local, site-local, multicast or IPv4-mapped).
    /// </summary>
    public static bool IsUsable(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] != 0 && bytes[0] != 127 && !(bytes[0] == 169 && bytes[1] == 254) && bytes[0] < 224;
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6 && !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal
            && !address.IsIPv6Multicast && address.ScopeId == 0 && !address.IsIPv4MappedToIPv6
            && !IPAddress.IPv6Loopback.Equals(address) && !IPAddress.IPv6Any.Equals(address);
    }

    /// <summary>The network of an address and prefix ("192.168.0.5", 24 -> 192.168.0.0/24).</summary>
    public static Subnet Of(IPAddress address, int prefixLength)
    {
        ArgumentNullException.ThrowIfNull(address);
        return new Subnet(Mask(address, prefixLength), prefixLength);
    }

    public bool Contains(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        return address.AddressFamily == Network.AddressFamily && Mask(address, PrefixLength).Equals(Network);
    }

    public override string ToString() => $"{Network}/{PrefixLength}";

    private static IPAddress Mask(IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var bits = Math.Clamp(prefix - (i * 8), 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - bits));
        }

        return new IPAddress(bytes);
    }
}
