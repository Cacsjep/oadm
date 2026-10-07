using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Oadm.Sdk.Network;

/// <summary>
/// One network interface of the OADM server (for core plugins that serve the network, e.g. NTP or DHCP server pages).
/// </summary>
/// <param name="Id">Stable id of the OS (<see cref="NetworkInterface.Id"/>: a GUID on Windows, the name on Linux/macOS).</param>
/// <param name="Name">"Ethernet", "eth0", "en0".</param>
/// <param name="Description">Adapter description ("Intel(R) Ethernet Connection I219-LM"); may equal the name.</param>
/// <param name="Addresses">Unicast addresses, IPv4 first; IPv6 link-local addresses carry their scope id.</param>
/// <param name="IsUp">Operational status is Up.</param>
/// <param name="IsLoopback">Loopback interface (127.0.0.1, ::1).</param>
public sealed record ServerNetworkInterface(
    string Id,
    string Name,
    string Description,
    IReadOnlyList<IPAddress> Addresses,
    bool IsUp,
    bool IsLoopback)
{
    /// <summary>The first IPv4 address, else the first non-link-local IPv6 address, else the first address.</summary>
    public IPAddress? PrimaryAddress =>
        Addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
        ?? Addresses.FirstOrDefault(a => !a.IsIPv6LinkLocal)
        ?? (Addresses.Count > 0 ? Addresses[0] : null);

    /// <summary>IPv4 interface index of the OS (-1 unknown): binds a socket to this interface (macOS IP_BOUND_IF, packet info).</summary>
    public int Ipv4Index { get; init; } = -1;

    /// <summary>Prefix length per unicast address (IPv4 /24 = 24); addresses without a known prefix are missing.</summary>
    public IReadOnlyDictionary<IPAddress, int> PrefixLengths { get; init; } = new Dictionary<IPAddress, int>();

    /// <summary>Default gateways of the interface (IPv4 first).</summary>
    public IReadOnlyList<IPAddress> Gateways { get; init; } = [];

    /// <summary>DNS servers the OS uses on this interface (IPv4 first).</summary>
    public IReadOnlyList<IPAddress> DnsServers { get; init; } = [];

    /// <summary>DNS suffix (domain name) of the interface, null when none.</summary>
    public string? DnsSuffix { get; init; }

    /// <summary>The first IPv4 address, null when the interface has none.</summary>
    public IPAddress? PrimaryIpv4 => Addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

    /// <summary>Prefix length of <paramref name="address"/>, null when unknown.</summary>
    public int? PrefixLengthOf(IPAddress address) => PrefixLengths.TryGetValue(address, out var length) ? length : null;
}

/// <summary>Lists the server's network interfaces. Injectable so plugins can be tested without real adapters.</summary>
public interface IServerNetworkInterfaces
{
    IReadOnlyList<ServerNetworkInterface> List();
}

/// <summary>
/// <see cref="IServerNetworkInterfaces"/> over <see cref="NetworkInterface.GetAllNetworkInterfaces"/> (Windows, Linux, macOS).
/// </summary>
public sealed class SystemNetworkInterfaces : IServerNetworkInterfaces
{
    public static SystemNetworkInterfaces Instance { get; } = new();

    public IReadOnlyList<ServerNetworkInterface> List()
    {
        NetworkInterface[] all;
        try
        {
            all = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return [];
        }

        var result = new List<ServerNetworkInterface>(all.Length);
        foreach (var nic in all)
        {
            IReadOnlyList<IPAddress> addresses = [];
            var prefixes = new Dictionary<IPAddress, int>();
            IReadOnlyList<IPAddress> gateways = [];
            IReadOnlyList<IPAddress> dns = [];
            string? suffix = null;
            var index = -1;
            try
            {
                var properties = nic.GetIPProperties();
                var unicast = properties.UnicastAddresses
                    .Where(u => u.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    .ToList();
                addresses = [.. unicast.Select(u => u.Address)
                    .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : a.IsIPv6LinkLocal ? 2 : 1)];
                foreach (var u in unicast)
                {
                    if (TryPrefix(u) is { } length)
                    {
                        prefixes[u.Address] = length;
                    }
                }

                gateways = Safe(() => properties.GatewayAddresses.Select(g => g.Address).Where(a => !a.Equals(IPAddress.Any)).OrderBy(Ipv4First).ToList(), []);
                dns = Safe(() => properties.DnsAddresses.Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6).OrderBy(Ipv4First).ToList(), []);
                suffix = Safe(() => string.IsNullOrWhiteSpace(properties.DnsSuffix) ? null : properties.DnsSuffix.Trim(), null);
                index = Safe(() => properties.GetIPv4Properties()?.Index ?? -1, -1);
            }
            catch (NetworkInformationException)
            {
            }
            catch (PlatformNotSupportedException)
            {
            }

            result.Add(new ServerNetworkInterface(
                nic.Id,
                nic.Name,
                string.IsNullOrWhiteSpace(nic.Description) ? nic.Name : nic.Description,
                addresses,
                nic.OperationalStatus == OperationalStatus.Up,
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                Ipv4Index = index,
                PrefixLengths = prefixes,
                Gateways = gateways,
                DnsServers = dns,
                DnsSuffix = suffix,
            });
        }

        return result;
    }

    private static int Ipv4First(IPAddress address) => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1;

    private static int? TryPrefix(UnicastIPAddressInformation info)
    {
        try
        {
            if (info.PrefixLength is > 0 and <= 128)
            {
                return info.PrefixLength;
            }
        }
        catch (PlatformNotSupportedException)
        {
        }
        catch (NotImplementedException)
        {
        }

        try
        {
            if (info.Address.AddressFamily == AddressFamily.InterNetwork && info.IPv4Mask is { } mask && !mask.Equals(IPAddress.Any))
            {
                var bytes = mask.GetAddressBytes();
                return bytes.Sum(b => System.Numerics.BitOperations.PopCount(b));
            }
        }
        catch (PlatformNotSupportedException)
        {
        }
        catch (NotImplementedException)
        {
        }

        return null;
    }

    /// <summary>Some properties are not implemented on every OS: an empty value then.</summary>
    private static T Safe<T>(Func<T> read, T fallback = default!)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or NotImplementedException or NetworkInformationException)
        {
            return fallback;
        }
    }
}
