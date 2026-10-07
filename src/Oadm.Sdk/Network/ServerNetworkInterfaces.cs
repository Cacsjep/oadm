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
            IReadOnlyList<IPAddress> addresses;
            try
            {
                addresses = [.. nic.GetIPProperties().UnicastAddresses
                    .Select(u => u.Address)
                    .Where(a => a.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : a.IsIPv6LinkLocal ? 2 : 1)];
            }
            catch (NetworkInformationException)
            {
                addresses = [];
            }
            catch (PlatformNotSupportedException)
            {
                addresses = [];
            }

            result.Add(new ServerNetworkInterface(
                nic.Id,
                nic.Name,
                string.IsNullOrWhiteSpace(nic.Description) ? nic.Name : nic.Description,
                addresses,
                nic.OperationalStatus == OperationalStatus.Up,
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback));
        }

        return result;
    }
}
