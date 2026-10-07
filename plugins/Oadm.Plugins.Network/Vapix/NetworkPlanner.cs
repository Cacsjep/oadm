using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

using Oadm.Plugins.Network.Model;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Network.Vapix;

public enum StepKind
{
    HostName = 0,
    Dns = 1,
    Ipv6 = 2,
    Ipv4 = 3,
}

/// <summary>One section to write, as one or more requests in order.</summary>
public sealed record PlannedStep(StepKind Kind, string Description, IReadOnlyList<NetworkRequest> Requests);

/// <summary>How the plan affects the address OADM uses to reach the device.</summary>
/// <param name="Affected">The last step changes the address family OADM connects with.</param>
/// <param name="Readdressed">The device (probably) gets a different address.</param>
/// <param name="OldAddress">The address OADM uses now.</param>
/// <param name="NewAddress">The new address when known; null for DHCP or a disabled family.</param>
public sealed record ConnectionImpact(bool Affected, bool Readdressed, string OldAddress, string? NewAddress)
{
    public static ConnectionImpact None(string address) => new(false, false, address, null);
}

public sealed record NetworkPlan(IReadOnlyList<PlannedStep> Steps, ConnectionImpact Impact);

/// <summary>
/// Turns a validated payload into the ordered write requests for one device, choosing network-settings or
/// param.cgi per method from the device's fresh API list. Every API the plan needs is checked with
/// <c>Require</c> here, so a missing API fails with <see cref="DeviceNotCompatibleException"/> before the first write.
/// Write order keeps the device reachable as long as possible: host name, DNS, then the address family OADM
/// does not use, and the family OADM connects with last.
/// </summary>
public static class NetworkPlanner
{
    public static NetworkPlan Build(NetworkPayload payload, Guid deviceId, IReadOnlyList<DeviceApi> apis, CurrentNetworkSettings current, string connectionAddress)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(apis);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(connectionAddress);

        PayloadValidator.ThrowIfInvalid(payload);
        var entry = payload.Devices.TryGetValue(deviceId, out var e) ? e : null;
        if (entry is null)
        {
            throw new NetworkValidationException(["The task settings contain no entry for this device."]);
        }

        var json = NetworkApis.UseJsonApi(apis);
        DeviceApi? ns = json ? apis.Require(NetworkApis.NetworkSettings, NetworkApis.NetworkSettingsBase) : null;
        if (!json)
        {
            apis.Require(NetworkApis.ParamCgi, NetworkApis.ParamCgiBase);
        }

        var steps = new List<PlannedStep>();
        if (payload.HostName is { } host)
        {
            steps.Add(HostNameStep(host, entry, ns, apis));
        }

        if (payload.Dns is { } dns)
        {
            steps.Add(DnsStep(dns, ns, apis, current));
        }

        PlannedStep? ipv6 = payload.Ipv6 is { } v6 ? Ipv6Step(v6, ns, apis, current) : null;
        PlannedStep? ipv4 = payload.Ipv4 is { } v4 ? Ipv4Step(v4, entry, ns, apis, current) : null;
        var connectsWithIpv6 = IPAddress.TryParse(connectionAddress, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6;
        if (connectsWithIpv6)
        {
            AddIfSet(steps, ipv4);
            AddIfSet(steps, ipv6);
        }
        else
        {
            AddIfSet(steps, ipv6);
            AddIfSet(steps, ipv4);
        }

        var impact = connectsWithIpv6
            ? Ipv6Impact(payload.Ipv6, current, connectionAddress)
            : Ipv4Impact(payload.Ipv4, entry, current, connectionAddress);
        return new NetworkPlan(steps, impact);
    }

    private static void AddIfSet(List<PlannedStep> steps, PlannedStep? step)
    {
        if (step is not null)
        {
            steps.Add(step);
        }
    }

    private static PlannedStep HostNameStep(HostNameChange host, DeviceAssignment entry, DeviceApi? ns, IReadOnlyList<DeviceApi> apis)
    {
        var name = entry.HostName?.Trim() ?? string.Empty;
        var description = host.UseDhcp ? "Host name from DHCP" : $"Host name {name}";
        if (ns is not null)
        {
            var p = new JsonObject { ["useDhcpHostname"] = host.UseDhcp };
            if (!host.UseDhcp)
            {
                p["staticHostname"] = name;
            }

            return new PlannedStep(StepKind.HostName, description, [JsonMethodRequest.Create(ns.Version, "setHostnameConfiguration", p)]);
        }

        apis.Require(NetworkApis.ParamCgi, NetworkApis.ParamCgiBase);
        var request = host.UseDhcp
            ? ParamUpdateRequest.Of(("Network.VolatileHostName.ObtainFromDHCP", "yes"))
            : ParamUpdateRequest.Of(("Network.HostName", name), ("Network.VolatileHostName.ObtainFromDHCP", "no"));
        return new PlannedStep(StepKind.HostName, description, [request]);
    }

    private static PlannedStep DnsStep(DnsChange dns, DeviceApi? ns, IReadOnlyList<DeviceApi> apis, CurrentNetworkSettings current)
    {
        var servers = (dns.Servers ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        var keptSearch = current.Dns.StaticSearchDomains.Count > 0 ? current.Dns.StaticSearchDomains : current.Dns.SearchDomains;
        var search = (dns.KeepDomains ? (ns is null ? [] : keptSearch) : dns.SearchDomains ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        var domain = (dns.KeepDomains ? current.Dns.StaticDomainName ?? current.Dns.DomainName : dns.DomainName)?.Trim() ?? string.Empty;
        var description = dns.UseDhcp ? "DNS from DHCP" : $"DNS {string.Join(", ", servers)}";
        if (!dns.UseDhcp)
        {
            if (current.Dns.MaxStaticServers is { } maxServers && servers.Count > maxServers)
            {
                throw new NetworkValidationException([$"DNS: the device accepts at most {maxServers} DNS servers."]);
            }

            if (current.Dns.MaxStaticSearchDomains is { } maxSearch && maxSearch > 0 && search.Count > maxSearch)
            {
                throw new NetworkValidationException([$"DNS: the device accepts at most {maxSearch} search domains."]);
            }
        }

        if (ns is not null)
        {
            var p = new JsonObject { ["useDhcpResolverInfo"] = dns.UseDhcp };
            if (!dns.UseDhcp)
            {
                p["staticNameServers"] = new JsonArray(servers.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
                p["staticDomainName"] = domain;
                p["staticSearchDomains"] = new JsonArray(search.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray());
            }

            return new PlannedStep(StepKind.Dns, description, [JsonMethodRequest.Create(ns.Version, "setResolverConfiguration", p)]);
        }

        apis.Require(NetworkApis.ParamCgi, NetworkApis.ParamCgiBase);
        if (dns.UseDhcp)
        {
            return new PlannedStep(StepKind.Dns, description, [ParamUpdateRequest.Of(("Network.Resolver.ObtainFromDHCP", "yes"))]);
        }

        if (search.Count > 0)
        {
            throw new DeviceNotCompatibleException(
                $"Search domains need {NetworkApis.NetworkSettings} {NetworkApis.NetworkSettingsBase} or later; this device only offers param.cgi. Nothing was changed.");
        }

        var request = ParamUpdateRequest.Of(
            ("Network.Resolver.ObtainFromDHCP", "no"),
            ("Network.DNSServer1", servers.ElementAtOrDefault(0) ?? "0.0.0.0"),
            ("Network.DNSServer2", servers.ElementAtOrDefault(1) ?? "0.0.0.0"),
            ("Network.DomainName", domain));
        return new PlannedStep(StepKind.Dns, description, [request]);
    }

    private static PlannedStep Ipv6Step(Ipv6Change v6, DeviceApi? ns, IReadOnlyList<DeviceApi> apis, CurrentNetworkSettings current)
    {
        if (!current.Ipv6.Supported)
        {
            throw new DeviceNotCompatibleException("The device's network interface has no IPv6 support. Nothing was changed.");
        }

        var jsonToggle = ns is not null && apis.Supports(NetworkApis.NetworkSettings, NetworkApis.SetIpv6) && current.InterfaceName is not null;
        var requests = new List<NetworkRequest>();
        if (v6.Mode == Ipv6Mode.Disabled)
        {
            if (jsonToggle)
            {
                requests.Add(Ipv6Toggle(ns!, current.InterfaceName!, enabled: false));
            }
            else
            {
                apis.Require(NetworkApis.ParamCgi, NetworkApis.ParamCgiBase);
                requests.Add(ParamUpdateRequest.Of(("Network.IPv6.Enabled", "no")));
            }

            return new PlannedStep(StepKind.Ipv6, "Disable IPv6", requests);
        }

        // The address mode, static address and gateway exist only as param.cgi Network.IPv6.* parameters.
        apis.Require(NetworkApis.ParamCgi, NetworkApis.ParamCgiBase);
        var values = new List<(string, string)>();
        string description;
        switch (v6.Mode)
        {
            case Ipv6Mode.Auto:
                values.Add(("Network.IPv6.AcceptRA", "yes"));
                values.Add(("Network.IPv6.DHCPv6", "auto"));
                description = "IPv6 automatic (router advertisement)";
                break;
            case Ipv6Mode.Dhcp:
                values.Add(("Network.IPv6.AcceptRA", "yes"));
                values.Add(("Network.IPv6.DHCPv6", "stateful"));
                description = "IPv6 from DHCPv6";
                break;
            default:
                var address = string.Create(CultureInfo.InvariantCulture, $"{v6.Address!.Trim()}/{v6.PrefixLength}");
                values.Add(("Network.IPv6.AcceptRA", "no"));
                values.Add(("Network.IPv6.DHCPv6", "off"));
                values.Add(("Network.IPv6.IPAddress", address));
                values.Add(("Network.IPv6.DefaultRouter", v6.Gateway?.Trim() ?? string.Empty));
                description = $"IPv6 static {address}";
                break;
        }

        if (!jsonToggle)
        {
            values.Add(("Network.IPv6.Enabled", "yes"));
        }

        requests.Add(ParamUpdateRequest.Of([.. values]));
        if (jsonToggle)
        {
            requests.Add(Ipv6Toggle(ns!, current.InterfaceName!, enabled: true));
        }

        return new PlannedStep(StepKind.Ipv6, description, requests);
    }

    private static JsonMethodRequest Ipv6Toggle(DeviceApi ns, string interfaceName, bool enabled) =>
        JsonMethodRequest.Create(ns.Version, "setIPv6AddressConfiguration", new JsonObject { ["deviceName"] = interfaceName, ["enabled"] = enabled });

    private static PlannedStep Ipv4Step(Ipv4Change v4, DeviceAssignment entry, DeviceApi? ns, IReadOnlyList<DeviceApi> apis, CurrentNetworkSettings current)
    {
        if (!current.Ipv4.Supported)
        {
            throw new DeviceNotCompatibleException("The device's network interface has no IPv4 support. Nothing was changed.");
        }

        var isStatic = v4.Mode == Ipv4Mode.Static;
        var address = entry.Ipv4Address?.Trim() ?? string.Empty;
        var prefix = v4.PrefixLength ?? 0;
        var gateway = v4.Gateway?.Trim() ?? string.Empty;
        var description = isStatic
            ? string.Create(CultureInfo.InvariantCulture, $"IPv4 static {address}/{prefix}, gateway {gateway}")
            : "IPv4 from DHCP";

        if (ns is not null)
        {
            if (current.InterfaceName is null)
            {
                throw new DeviceNotCompatibleException("The device reports no network interface for IPv4. Nothing was changed.");
            }

            if (isStatic && current.Ipv4.MaxStaticAddresses is 0)
            {
                throw new DeviceNotCompatibleException("The device does not accept a static IPv4 address. Nothing was changed.");
            }

            var p = new JsonObject
            {
                ["deviceName"] = current.InterfaceName,
                ["configurationMode"] = isStatic ? "static" : "dhcp",
            };
            if (isStatic)
            {
                p["staticDefaultRouter"] = gateway;
                p["staticAddressConfigurations"] = new JsonArray(new JsonObject { ["address"] = address, ["prefixLength"] = prefix });
            }

            return new PlannedStep(StepKind.Ipv4, description, [JsonMethodRequest.Create(ns.Version, "setIPv4AddressConfiguration", p)]);
        }

        apis.Require(NetworkApis.ParamCgi, NetworkApis.ParamCgiBase);
        if (!isStatic)
        {
            return new PlannedStep(StepKind.Ipv4, description, [ParamUpdateRequest.Of(("Network.BootProto", "dhcp"))]);
        }

        if (!Ipv4.TryParse(address, out var a))
        {
            throw new NetworkValidationException([$"IPv4: \"{address}\" is not a valid IPv4 address."]);
        }

        var request = ParamUpdateRequest.Of(
            ("Network.IPAddress", address),
            ("Network.SubnetMask", Ipv4.MaskText(prefix)),
            ("Network.Broadcast", Ipv4.Format(Ipv4.Broadcast(a, prefix))),
            ("Network.DefaultRouter", gateway),
            ("Network.BootProto", "none"));
        return new PlannedStep(StepKind.Ipv4, description, [request]);
    }

    private static ConnectionImpact Ipv4Impact(Ipv4Change? v4, DeviceAssignment entry, CurrentNetworkSettings current, string connectionAddress)
    {
        if (v4 is null)
        {
            return ConnectionImpact.None(connectionAddress);
        }

        var connectedIp = Ipv4.TryParse(connectionAddress, out _) ? connectionAddress : current.Ipv4.Address;
        if (v4.Mode == Ipv4Mode.Static)
        {
            var target = entry.Ipv4Address?.Trim();
            var moves = connectedIp is null || !string.Equals(connectedIp, target, StringComparison.Ordinal);
            return new ConnectionImpact(true, moves, connectionAddress, target);
        }

        var wasDhcp = string.Equals(current.Ipv4.Mode, "dhcp", StringComparison.OrdinalIgnoreCase);
        return new ConnectionImpact(true, !wasDhcp, connectionAddress, wasDhcp ? connectedIp : null);
    }

    private static ConnectionImpact Ipv6Impact(Ipv6Change? v6, CurrentNetworkSettings current, string connectionAddress)
    {
        if (v6 is null)
        {
            return ConnectionImpact.None(connectionAddress);
        }

        return v6.Mode switch
        {
            Ipv6Mode.Disabled => new ConnectionImpact(true, true, connectionAddress, null),
            Ipv6Mode.Static => new ConnectionImpact(true, !IPAddress.Parse(v6.Address!.Trim()).Equals(IPAddress.Parse(connectionAddress)), connectionAddress, v6.Address.Trim()),
            _ => new ConnectionImpact(true, !string.Equals(current.Ipv6.Mode, v6.Mode == Ipv6Mode.Auto ? "auto" : "dhcp", StringComparison.OrdinalIgnoreCase), connectionAddress, null),
        };
    }
}
