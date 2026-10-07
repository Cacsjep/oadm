using System.Globalization;
using System.Net.Sockets;

using Oadm.Sdk.Network;

namespace Oadm.Plugins.DhcpServer.Status;

/// <summary>The status line texts (spec "DHCP server", one at a time, plain language) and the mapping of bind errors.</summary>
public static class DhcpStatusTexts
{
    public static ServiceStatus Stopped { get; } = new(ServiceStatus.Neutral, "Stopped");

    /// <summary>"Running on Ethernet (10.0.0.17/24)".</summary>
    public static ServiceStatus Running(string interfaceName, string address, int prefixLength) =>
        new(ServiceStatus.Ok, string.Create(CultureInfo.InvariantCulture, $"Running on {interfaceName} ({address}/{prefixLength})"));

    public static ServiceStatus InterfaceNotAvailable(string interfaceName) =>
        new(ServiceStatus.Error, $"Interface {interfaceName} is not available",
            "The interface is down or has no IPv4 address on this server. OADM tries again every 30 s; select another interface to serve now.");

    public static ServiceStatus RangeOutsideSubnet(string subnet) =>
        new(ServiceStatus.Error, "Range is not inside the interface subnet",
            $"The interface is now in {subnet}. Change the start and end address to addresses of this subnet and save.");

    public static ServiceStatus OtherServer(IReadOnlyList<string> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);
        return new(ServiceStatus.Warning, $"Another DHCP server answers on this network ({string.Join(", ", servers)})",
            "Running two DHCP servers on one network causes address conflicts. Turn off the other DHCP server (often the router), or disable this one.");
    }

    public static ServiceStatus PoolExhausted { get; } = new(ServiceStatus.Warning, "Address pool exhausted",
        "Every address of the range is in use, so new devices get no address. Make the range larger or release leases that are no longer needed.");

    /// <summary>
    /// Maps a failed bind of port 67. Windows has no privileged ports, so AccessDenied there means another program holds the
    /// port (the Windows DHCP Server role, Internet Connection Sharing); Linux needs root or capabilities; macOS needs sudo.
    /// </summary>
    /// <param name="error">Socket error of the bind.</param>
    /// <param name="port">67.</param>
    /// <param name="interfaceName">Selected interface.</param>
    /// <param name="os">Server OS.</param>
    /// <param name="windowsHolder">Windows: the running service that holds the port ("Windows DHCP Server", "Internet Connection Sharing"), if known.</param>
    public static ServiceStatus ForBindError(SocketError error, int port, string interfaceName, HostOs os, string? windowsHolder = null)
    {
        switch (PortBindErrors.Classify(error, os))
        {
            case BindErrorKind.InUse:
                return new(ServiceStatus.Error, PortBindErrors.InUseText(port), os switch
                {
                    HostOs.Windows => (windowsHolder is null
                        ? "Usually the Windows DHCP Server role or Internet Connection Sharing. "
                        : $"The {windowsHolder} service is running and holds it. ")
                        + "Stop it (Services, or \"net stop DHCPServer\" / \"net stop SharedAccess\" as administrator), "
                        + "or find the program with \"netstat -abno -p UDP\" (as administrator) and stop it.",
                    HostOs.Linux => PortBindErrors.FindUdpPortOwner(os, port, "often dnsmasq, isc-dhcp-server or a NetworkManager shared connection"),
                    HostOs.MacOs => PortBindErrors.FindUdpPortOwner(os, port, "bootpd runs while Internet Sharing is on"),
                    _ => "Stop the other DHCP server on this machine.",
                });

            case BindErrorKind.Permission:
                return new(ServiceStatus.Error, PortBindErrors.PermissionText(port), os switch
                {
                    HostOs.Linux => PortBindErrors.PermissionFix(os, "cap_net_bind_service,cap_net_raw")
                        + " (binding to one interface needs cap_net_raw on Linux kernels before 5.7)",
                    _ => PortBindErrors.PermissionFix(os),
                });

            case BindErrorKind.AddressNotAvailable:
                return InterfaceNotAvailable(interfaceName);

            default:
                return new(ServiceStatus.Error, PortBindErrors.OtherText(port, error));
        }
    }
}
