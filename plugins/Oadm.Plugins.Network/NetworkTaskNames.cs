using Oadm.Plugins.Network.Model;

namespace Oadm.Plugins.Network;

/// <summary>
/// Task names (<c>ITaskPlugin.GetTaskName</c>) of both network plugins: exactly what the task does, computed once per
/// run from the shared payload. Null when the payload cannot be read (the host then uses the display name).
/// </summary>
public static class NetworkTaskNames
{
    public const string General = "Change network settings";

    /// <summary>
    /// The most specific name when only one section changes: "Set static IP 10.0.0.60" (one device) or
    /// "Set static IP addresses" (several), "Switch to DHCP", "Set DNS servers" / "Use DNS from DHCP",
    /// "Set host name cam-1" / "Set host names" / "Use host name from DHCP", "Change IPv6 settings";
    /// otherwise "Change network settings".
    /// </summary>
    public static string? ForNetworkSettings(string? payloadJson)
    {
        if (TryParse(payloadJson) is not { } payload)
        {
            return null;
        }

        var sections = (payload.Ipv4 is null ? 0 : 1) + (payload.Ipv6 is null ? 0 : 1) + (payload.Dns is null ? 0 : 1) + (payload.HostName is null ? 0 : 1);
        if (sections != 1)
        {
            return General;
        }

        if (payload.Ipv4 is { } v4)
        {
            return v4.Mode == Ipv4Mode.Dhcp
                ? "Switch to DHCP"
                : SingleAddress(payload) is { } address ? $"Set static IP {address}" : "Set static IP addresses";
        }

        if (payload.Dns is { } dns)
        {
            return dns.UseDhcp ? "Use DNS from DHCP" : "Set DNS servers";
        }

        if (payload.HostName is { } host)
        {
            if (host.UseDhcp)
            {
                return "Use host name from DHCP";
            }

            return payload.Devices.Count == 1 && payload.Devices.Values.First().HostName?.Trim() is { Length: > 0 } name
                ? $"Set host name {name}"
                : "Set host names";
        }

        return "Change IPv6 settings";
    }

    /// <summary>"Assign IP via DHCP", "Assign IP 10.0.0.60" (one device), "Assign IP addresses" (several).</summary>
    public static string? ForAssignIp(string? payloadJson)
    {
        if (TryParse(payloadJson) is not { } payload || payload.Ipv4 is not { } v4)
        {
            return null;
        }

        return v4.Mode == Ipv4Mode.Dhcp
            ? "Assign IP via DHCP"
            : SingleAddress(payload) is { } address ? $"Assign IP {address}" : "Assign IP addresses";
    }

    private static string? SingleAddress(NetworkPayload payload) =>
        payload.Devices.Count == 1 && payload.Devices.Values.First().Ipv4Address?.Trim() is { Length: > 0 } address ? address : null;

    private static NetworkPayload? TryParse(string? payloadJson)
    {
        try
        {
            return NetworkPayload.Parse(payloadJson);
        }
        catch (NetworkValidationException)
        {
            return null;
        }
    }
}
