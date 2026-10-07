using System.Globalization;
using System.Text.Json;

using Oadm.Plugins.Network.Model;

namespace Oadm.Plugins.Network.Vapix;

/// <summary>Parses getNetworkInfo responses and param.cgi Network.* lists into <see cref="CurrentNetworkSettings"/>.</summary>
public static class NetworkInfoParser
{
    /// <summary>param.cgi groups read for the legacy path and for the IPv6 address mode.</summary>
    public static readonly IReadOnlyList<string> LegacyGroups =
    [
        "Network.BootProto", "Network.IPAddress", "Network.SubnetMask", "Network.DefaultRouter",
        "Network.HostName", "Network.DNSServer1", "Network.DNSServer2", "Network.DomainName",
        "Network.eth0", "Network.Routing", "Network.Resolver", "Network.VolatileHostName", "Network.IPv6",
    ];

    public static readonly IReadOnlyList<string> Ipv6Groups = ["Network.IPv6", "Network.eth0.IPv6", "Network.Routing.IPv6"];

    /// <summary>
    /// Parses a getNetworkInfo response. <paramref name="connectionAddress"/> (the address OADM uses) picks the
    /// network interface device; otherwise the first active one with IPv4 is used.
    /// </summary>
    public static CurrentNetworkSettings ParseGetNetworkInfo(string json, string? connectionAddress)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        NetworkSettingsClient.ThrowIfError(root, "getNetworkInfo");
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("getNetworkInfo: response has no data.");
        }

        var version = Str(root, "apiVersion");
        var system = Obj(data, "system");
        var iface = PickInterface(data, system, connectionAddress);

        return new CurrentNetworkSettings
        {
            Source = version is null ? "network-settings" : $"network-settings {version}",
            InterfaceName = iface is { } i ? Str(i, "name") : null,
            Ipv4 = ParseIpv4(iface),
            Ipv6 = ParseIpv6(iface),
            Dns = ParseResolver(system is { } s ? Obj(s, "resolver") : null),
            HostName = ParseHostName(system is { } s2 ? Obj(s2, "hostname") : null),
        };
    }

    /// <summary>Complements a getNetworkInfo result with the IPv6 address mode, static addresses and gateway from param.cgi.</summary>
    public static CurrentNetworkSettings WithIpv6Parameters(CurrentNetworkSettings settings, IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(parameters);
        if (!parameters.Keys.Any(k => k.StartsWith("Network.IPv6.", StringComparison.OrdinalIgnoreCase)))
        {
            return settings;
        }

        return settings with
        {
            Ipv6 = settings.Ipv6 with
            {
                Mode = Ipv6ModeFromParameters(parameters),
                StaticAddresses = SplitList(Get(parameters, "Network.IPv6.IPAddress")),
                Gateway = NullIfEmpty(Get(parameters, "Network.IPv6.DefaultRouter")) ?? settings.Ipv6.Gateway,
            },
        };
    }

    /// <summary>Parses the legacy param.cgi Network group (keys without "root.").</summary>
    public static CurrentNetworkSettings ParseParameters(IReadOnlyDictionary<string, string> p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var bootProto = Get(p, "Network.BootProto");
        var currentMask = Get(p, "Network.eth0.SubnetMask");
        var staticMask = Get(p, "Network.SubnetMask");
        var ipv6Enabled = Get(p, "Network.IPv6.Enabled");
        var nameServers = SplitList(Get(p, "Network.Resolver.NameServerList"));
        if (nameServers.Count == 0)
        {
            nameServers = new[] { Get(p, "Network.Resolver.NameServer1"), Get(p, "Network.Resolver.NameServer2") }
                .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList();
        }

        return new CurrentNetworkSettings
        {
            Source = "param.cgi",
            Ipv4 = new CurrentIpv4
            {
                Mode = bootProto is null ? null : string.Equals(bootProto, "dhcp", StringComparison.OrdinalIgnoreCase) ? "dhcp" : "static",
                Address = NullIfEmpty(Get(p, "Network.eth0.IPAddress")),
                PrefixLength = Ipv4.TryParsePrefix(currentMask, out var currentPrefix) ? currentPrefix : null,
                Gateway = NullIfEmpty(Get(p, "Network.Routing.DefaultRouter")),
                StaticAddress = NullIfEmpty(Get(p, "Network.IPAddress")),
                StaticPrefixLength = Ipv4.TryParsePrefix(staticMask, out var staticPrefix) ? staticPrefix : null,
                StaticGateway = NullIfEmpty(Get(p, "Network.DefaultRouter")),
            },
            Ipv6 = new CurrentIpv6
            {
                Supported = ipv6Enabled is not null,
                Enabled = IsYes(ipv6Enabled),
                Mode = ipv6Enabled is null ? null : Ipv6ModeFromParameters(p),
                Addresses = SplitList(Get(p, "Network.eth0.IPv6.IPAddresses")),
                StaticAddresses = SplitList(Get(p, "Network.IPv6.IPAddress")),
                Gateway = NullIfEmpty(Get(p, "Network.IPv6.DefaultRouter")) ?? NullIfEmpty(Get(p, "Network.Routing.IPv6.DefaultRouter")),
            },
            Dns = new CurrentDns
            {
                UseDhcp = IsYes(Get(p, "Network.Resolver.ObtainFromDHCP")),
                Servers = nameServers,
                StaticServers = new[] { Get(p, "Network.DNSServer1"), Get(p, "Network.DNSServer2") }
                    .Where(s => !string.IsNullOrWhiteSpace(s) && s != "0.0.0.0").Select(s => s!).ToList(),
                DomainName = NullIfEmpty(Get(p, "Network.DomainName")),
                StaticDomainName = NullIfEmpty(Get(p, "Network.DomainName")),
                SearchDomains = SplitList(Get(p, "Network.Resolver.Search")),
                MaxStaticServers = 2,
                MaxStaticSearchDomains = 0,
            },
            HostName = new CurrentHostName
            {
                UseDhcp = IsYes(Get(p, "Network.VolatileHostName.ObtainFromDHCP")),
                HostName = NullIfEmpty(Get(p, "Network.VolatileHostName.HostName")) ?? NullIfEmpty(Get(p, "Network.HostName")),
                StaticHostName = NullIfEmpty(Get(p, "Network.HostName")),
            },
        };
    }

    /// <summary>Network.IPv6.DHCPv6=stateful -> dhcp; AcceptRA=no and DHCPv6=off -> static; otherwise auto.</summary>
    public static string Ipv6ModeFromParameters(IReadOnlyDictionary<string, string> p)
    {
        var dhcp = Get(p, "Network.IPv6.DHCPv6") ?? "auto";
        var acceptRa = Get(p, "Network.IPv6.AcceptRA");
        if (string.Equals(dhcp, "stateful", StringComparison.OrdinalIgnoreCase))
        {
            return "dhcp";
        }

        return string.Equals(dhcp, "off", StringComparison.OrdinalIgnoreCase) && !IsYes(acceptRa) ? "static" : "auto";
    }

    private static JsonElement? PickInterface(JsonElement data, JsonElement? system, string? connectionAddress)
    {
        if (!data.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array || devices.GetArrayLength() == 0)
        {
            return null;
        }

        var all = devices.EnumerateArray().ToList();
        if (!string.IsNullOrWhiteSpace(connectionAddress))
        {
            foreach (var device in all)
            {
                foreach (var family in new[] { "IPv4", "IPv6" })
                {
                    if (Obj(device, family) is { } ip && ip.TryGetProperty("addresses", out var addresses) && addresses.ValueKind == JsonValueKind.Array
                        && addresses.EnumerateArray().Any(a => string.Equals(Str(a, "address"), connectionAddress, StringComparison.OrdinalIgnoreCase)))
                    {
                        return device;
                    }
                }
            }
        }

        if (system is { } s && Obj(s, "deviceSwitching") is { } switching && switching.TryGetProperty("activeDevices", out var active) && active.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in active.EnumerateArray().Select(a => a.GetString()))
            {
                var match = all.FirstOrDefault(d => Str(d, "name") == name && Obj(d, "IPv4") is not null);
                if (match.ValueKind == JsonValueKind.Object)
                {
                    return match;
                }
            }
        }

        var withIpv4 = all.FirstOrDefault(d => Obj(d, "IPv4") is not null);
        return withIpv4.ValueKind == JsonValueKind.Object ? withIpv4 : all[0];
    }

    private static CurrentIpv4 ParseIpv4(JsonElement? iface)
    {
        if (iface is not { } i || Obj(i, "IPv4") is not { } v4)
        {
            return new CurrentIpv4 { Supported = false };
        }

        var current = FirstAddress(v4);
        var stat = v4.TryGetProperty("staticAddressConfigurations", out var statics) && statics.ValueKind == JsonValueKind.Array && statics.GetArrayLength() > 0
            ? statics[0]
            : (JsonElement?)null;
        return new CurrentIpv4
        {
            Mode = Str(v4, "configurationMode"),
            Address = current is { } c ? Str(c, "address") : null,
            PrefixLength = current is { } c2 ? Int(c2, "prefixLength") : null,
            Gateway = NullIfEmpty(Str(v4, "defaultRouter")),
            StaticAddress = stat is { } s ? NullIfEmpty(Str(s, "address")) : null,
            StaticPrefixLength = stat is { } s2 ? Int(s2, "prefixLength") : null,
            StaticGateway = NullIfEmpty(Str(v4, "staticDefaultRouter")),
            MaxStaticAddresses = Int(v4, "maxSupportedStaticAddressConfigurations"),
        };
    }

    private static CurrentIpv6 ParseIpv6(JsonElement? iface)
    {
        if (iface is not { } i || Obj(i, "IPv6") is not { } v6)
        {
            return new CurrentIpv6 { Supported = false };
        }

        var addresses = new List<string>();
        if (v6.TryGetProperty("addresses", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            addresses.AddRange(list.EnumerateArray()
                .Select(a => (Address: Str(a, "address"), Prefix: Int(a, "prefixLength")))
                .Where(a => !string.IsNullOrEmpty(a.Address))
                .Select(a => a.Prefix is { } p ? string.Create(CultureInfo.InvariantCulture, $"{a.Address}/{p}") : a.Address!));
        }

        var statics = new List<string>();
        if (v6.TryGetProperty("staticAddressConfigurations", out var st) && st.ValueKind == JsonValueKind.Array)
        {
            statics.AddRange(st.EnumerateArray()
                .Select(a => (Address: Str(a, "address"), Prefix: Int(a, "prefixLength")))
                .Where(a => !string.IsNullOrEmpty(a.Address))
                .Select(a => a.Prefix is { } p ? string.Create(CultureInfo.InvariantCulture, $"{a.Address}/{p}") : a.Address!));
        }

        return new CurrentIpv6
        {
            Enabled = Bool(v6, "enabled") ?? false,
            Mode = null,
            Addresses = addresses,
            StaticAddresses = statics,
            Gateway = NullIfEmpty(Str(v6, "defaultRouter")) ?? NullIfEmpty(Str(v6, "staticDefaultRouter")),
        };
    }

    private static CurrentDns ParseResolver(JsonElement? resolver)
    {
        if (resolver is not { } r)
        {
            return new CurrentDns();
        }

        return new CurrentDns
        {
            // The device answers "useDhcpResolverInfo"; parts of the documentation spell it "useDHCPResolverInfo".
            UseDhcp = Bool(r, "useDhcpResolverInfo") ?? Bool(r, "useDHCPResolverInfo") ?? false,
            Servers = Strings(r, "nameServers"),
            StaticServers = Strings(r, "staticNameServers"),
            DomainName = NullIfEmpty(Str(r, "domainName")),
            StaticDomainName = NullIfEmpty(Str(r, "staticDomainName")),
            SearchDomains = Strings(r, "searchDomains"),
            StaticSearchDomains = Strings(r, "staticSearchDomains"),
            MaxStaticServers = Int(r, "maxSupportedStaticNameServers"),
            MaxStaticSearchDomains = Int(r, "maxSupportedStaticSearchDomains"),
        };
    }

    private static CurrentHostName ParseHostName(JsonElement? hostname) => hostname is not { } h
        ? new CurrentHostName()
        : new CurrentHostName
        {
            UseDhcp = Bool(h, "useDhcpHostname") ?? false,
            HostName = NullIfEmpty(Str(h, "hostname")),
            StaticHostName = NullIfEmpty(Str(h, "staticHostname")),
        };

    private static JsonElement? FirstAddress(JsonElement ip)
    {
        if (!ip.TryGetProperty("addresses", out var addresses) || addresses.ValueKind != JsonValueKind.Array || addresses.GetArrayLength() == 0)
        {
            return null;
        }

        var all = addresses.EnumerateArray().ToList();
        var global = all.FirstOrDefault(a => Str(a, "scope") == "global");
        return global.ValueKind == JsonValueKind.Object ? global : all[0];
    }

    private static JsonElement? Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static List<string> Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(s => s.Length > 0).ToList()
            : [];

    private static string? Get(IReadOnlyDictionary<string, string> p, string key) =>
        p.TryGetValue(key, out var v) ? v : p.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)).Value;

    private static bool IsYes(string? value) => string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static List<string> SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
