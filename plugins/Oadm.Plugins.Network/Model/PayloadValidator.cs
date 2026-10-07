using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Oadm.Plugins.Network.Model;

/// <summary>
/// Validates a <see cref="NetworkPayload"/> for the whole batch (duplicates across devices included).
/// Used by the dialog (to enable Apply) and by the server before the first write.
/// </summary>
public static partial class PayloadValidator
{
    public const int MaxDnsServers = 2;
    public const int MaxSearchDomains = 6;

    public static IReadOnlyList<string> Validate(NetworkPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var errors = new List<string>();
        if (!payload.HasChanges)
        {
            errors.Add("Nothing to change: choose at least one section.");
            return errors;
        }

        if (payload.Devices.Count == 0)
        {
            errors.Add("No devices selected.");
            return errors;
        }

        if (payload.Ipv4 is { } v4)
        {
            ValidateIpv4(v4, payload.Devices, errors);
        }

        if (payload.Ipv6 is { } v6)
        {
            ValidateIpv6(v6, payload.Devices.Count, errors);
        }

        if (payload.Dns is { } dns)
        {
            ValidateDns(dns, errors);
        }

        if (payload.HostName is { UseDhcp: false })
        {
            ValidateHostNames(payload.Devices, errors);
        }

        return errors;
    }

    /// <summary>Throws <see cref="NetworkValidationException"/> when <see cref="Validate"/> finds errors.</summary>
    public static void ThrowIfInvalid(NetworkPayload payload)
    {
        var errors = Validate(payload);
        if (errors.Count > 0)
        {
            throw new NetworkValidationException(errors);
        }
    }

    private static void ValidateIpv4(Ipv4Change v4, IReadOnlyDictionary<Guid, DeviceAssignment> devices, List<string> errors)
    {
        if (v4.Mode != Ipv4Mode.Static)
        {
            return;
        }

        if (v4.PrefixLength is not { } prefix || prefix is < 1 or > 30)
        {
            errors.Add("IPv4: enter a subnet mask between /1 (128.0.0.0) and /30 (255.255.255.252).");
            return;
        }

        uint gateway = 0;
        if (string.IsNullOrWhiteSpace(v4.Gateway) || !Ipv4.TryParse(v4.Gateway, out gateway))
        {
            errors.Add("IPv4: enter a valid default gateway.");
        }
        else if (Ipv4.HostAddressProblem(gateway, prefix) is { } gatewayProblem)
        {
            errors.Add($"IPv4: gateway {v4.Gateway} {gatewayProblem}.");
            gateway = 0;
        }

        var seen = new Dictionary<uint, int>();
        var missing = 0;
        foreach (var (_, assignment) in devices)
        {
            var text = assignment.Ipv4Address;
            if (string.IsNullOrWhiteSpace(text))
            {
                missing++;
                continue;
            }

            if (!Ipv4.TryParse(text, out var address))
            {
                errors.Add($"IPv4: \"{text}\" is not a valid IPv4 address.");
                continue;
            }

            if (Ipv4.HostAddressProblem(address, prefix) is { } problem)
            {
                errors.Add($"IPv4: {text} {problem}.");
            }

            if (gateway != 0)
            {
                if (!Ipv4.SameSubnet(address, gateway, prefix))
                {
                    errors.Add($"IPv4: {text} and gateway {v4.Gateway} are not in the same /{prefix} subnet.");
                }
                else if (address == gateway)
                {
                    errors.Add($"IPv4: {text} is the gateway address.");
                }
            }

            seen[address] = seen.GetValueOrDefault(address) + 1;
        }

        if (missing > 0)
        {
            errors.Add(missing == 1 ? "IPv4: one device has no address." : $"IPv4: {missing} devices have no address.");
        }

        foreach (var (address, count) in seen.Where(p => p.Value > 1))
        {
            errors.Add($"IPv4: {Ipv4.Format(address)} is assigned to {count} devices.");
        }
    }

    private static void ValidateIpv6(Ipv6Change v6, int deviceCount, List<string> errors)
    {
        if (v6.Mode != Ipv6Mode.Static)
        {
            return;
        }

        if (deviceCount != 1)
        {
            errors.Add("IPv6: a static IPv6 address can only be set for one device at a time.");
        }

        if (!TryParseIpv6(v6.Address, out var address))
        {
            errors.Add("IPv6: enter a valid IPv6 address.");
        }
        else if (Ipv6AddressProblem(address) is { } problem)
        {
            errors.Add($"IPv6: {v6.Address} {problem}.");
        }

        if (v6.PrefixLength is not { } prefix || prefix is < 1 or > 128)
        {
            errors.Add("IPv6: enter a prefix length between 1 and 128.");
        }

        if (!string.IsNullOrWhiteSpace(v6.Gateway))
        {
            if (!TryParseIpv6(v6.Gateway, out var gateway))
            {
                errors.Add("IPv6: the gateway is not a valid IPv6 address.");
            }
            else if (Ipv6AddressProblem(gateway) is { } gatewayProblem)
            {
                errors.Add($"IPv6: gateway {v6.Gateway} {gatewayProblem}.");
            }
        }
    }

    private static void ValidateDns(DnsChange dns, List<string> errors)
    {
        if (dns.UseDhcp)
        {
            return;
        }

        var servers = (dns.Servers ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        if (servers.Count == 0)
        {
            errors.Add("DNS: enter at least one DNS server or use DNS from DHCP.");
        }
        else if (servers.Count > MaxDnsServers)
        {
            errors.Add($"DNS: at most {MaxDnsServers} DNS servers.");
        }

        foreach (var server in servers)
        {
            if (!Ipv4.IsIpLiteral(server))
            {
                errors.Add($"DNS: \"{server}\" is not a valid IP address.");
            }
            else if (Ipv4.TryParse(server, out var v4) && Ipv4.HostAddressProblem(v4, 32) is { } problem)
            {
                errors.Add($"DNS: {server} {problem}.");
            }
        }

        if (servers.Count != servers.Distinct(StringComparer.OrdinalIgnoreCase).Count())
        {
            errors.Add("DNS: the same server is entered twice.");
        }

        if (!string.IsNullOrWhiteSpace(dns.DomainName) && !IsValidDomainName(dns.DomainName.Trim()))
        {
            errors.Add($"DNS: \"{dns.DomainName}\" is not a valid domain name.");
        }

        var search = (dns.SearchDomains ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        if (search.Count > MaxSearchDomains)
        {
            errors.Add($"DNS: at most {MaxSearchDomains} search domains.");
        }

        foreach (var domain in search.Where(d => !IsValidDomainName(d.Trim())))
        {
            errors.Add($"DNS: search domain \"{domain}\" is not a valid domain name.");
        }
    }

    private static void ValidateHostNames(IReadOnlyDictionary<Guid, DeviceAssignment> devices, List<string> errors)
    {
        var missing = 0;
        var names = new List<string>();
        foreach (var (_, assignment) in devices)
        {
            var name = assignment.HostName?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                missing++;
                continue;
            }

            if (!IsValidHostName(name))
            {
                errors.Add($"Host name: \"{name}\" is not valid (letters, digits and hyphens, 1-63 characters, no leading or trailing hyphen).");
            }

            names.Add(name);
        }

        if (missing > 0)
        {
            errors.Add(missing == 1 ? "Host name: one device has no host name." : $"Host name: {missing} devices have no host name.");
        }

        foreach (var group in names.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            errors.Add($"Host name: \"{group.Key}\" is assigned to {group.Count()} devices. Use {{n}} or {{serial}} in the name.");
        }
    }

    public static bool IsValidHostName(string name) => HostLabelRegex().IsMatch(name) && !name.All(char.IsAsciiDigit);

    public static bool IsValidDomainName(string name)
    {
        var trimmed = name.EndsWith('.') ? name[..^1] : name;
        return trimmed.Length is > 0 and <= 253 && trimmed.Split('.').All(l => HostLabelRegex().IsMatch(l));
    }

    public static bool TryParseIpv6(string? text, out IPAddress address)
    {
        address = IPAddress.None;
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t) || !t.Contains(':', StringComparison.Ordinal) || t.Contains('%', StringComparison.Ordinal) || t.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        if (!IPAddress.TryParse(t, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        address = parsed;
        return true;
    }

    private static string? Ipv6AddressProblem(IPAddress address)
    {
        if (address.Equals(IPAddress.IPv6None) || address.Equals(IPAddress.IPv6Any))
        {
            return "is not a usable address";
        }

        if (address.Equals(IPAddress.IPv6Loopback))
        {
            return "is the loopback address";
        }

        if (address.IsIPv6Multicast)
        {
            return "is a multicast address";
        }

        return address.IsIPv4MappedToIPv6 ? "is an IPv4-mapped address" : null;
    }

    /// <summary>Expands a host name template: {n} = 1-based position in the selection, {serial} = lower-case serial.</summary>
    public static string ExpandHostName(string template, int position, string serial)
    {
        ArgumentNullException.ThrowIfNull(template);
        return template.Trim()
            .Replace("{n}", position.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{serial}", (serial ?? string.Empty).ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex("^[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?$")]
    private static partial Regex HostLabelRegex();
}
