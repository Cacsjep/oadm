using System.Text.Json;

namespace Oadm.Plugins.Network.Model;

/// <summary>
/// Current network settings of one device, normalized from network-settings getNetworkInfo or from
/// the legacy param.cgi Network group. Result of the read-only query "getNetworkInfo" (dialog prefill)
/// and input for the task's plan (interface name, current address, device limits).
/// </summary>
public sealed record CurrentNetworkSettings
{
    /// <summary>Where the values came from, e.g. "network-settings 1.37" or "param.cgi".</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Network interface device used for the IPv4/IPv6 settings ("eth0"); null for param.cgi.</summary>
    public string? InterfaceName { get; init; }

    public CurrentIpv4 Ipv4 { get; init; } = new();

    public CurrentIpv6 Ipv6 { get; init; } = new();

    public CurrentDns Dns { get; init; } = new();

    public CurrentHostName HostName { get; init; } = new();

    public string ToJson() => JsonSerializer.Serialize(this, PayloadJson.Options);

    public static CurrentNetworkSettings FromJson(string json) =>
        JsonSerializer.Deserialize<CurrentNetworkSettings>(json, PayloadJson.Options)
        ?? throw new JsonException("Empty network settings.");
}

public sealed record CurrentIpv4
{
    public bool Supported { get; init; } = true;

    /// <summary>"dhcp" or "static" (lower case, as the device reports it).</summary>
    public string? Mode { get; init; }

    /// <summary>Address in use (first global one).</summary>
    public string? Address { get; init; }

    public int? PrefixLength { get; init; }

    public string? Gateway { get; init; }

    public string? StaticAddress { get; init; }

    public int? StaticPrefixLength { get; init; }

    public string? StaticGateway { get; init; }

    /// <summary>Device limit for static IPv4 address configurations (network-settings only).</summary>
    public int? MaxStaticAddresses { get; init; }
}

public sealed record CurrentIpv6
{
    public bool Supported { get; init; } = true;

    public bool Enabled { get; init; }

    /// <summary>"auto", "dhcp" or "static", derived from Network.IPv6.AcceptRA / DHCPv6; null when unknown.</summary>
    public string? Mode { get; init; }

    public IReadOnlyList<string> Addresses { get; init; } = [];

    /// <summary>Manually configured addresses ("2001:db8::10/64"), from Network.IPv6.IPAddress.</summary>
    public IReadOnlyList<string> StaticAddresses { get; init; } = [];

    public string? Gateway { get; init; }
}

public sealed record CurrentDns
{
    public bool UseDhcp { get; init; }

    public IReadOnlyList<string> Servers { get; init; } = [];

    public IReadOnlyList<string> StaticServers { get; init; } = [];

    public string? DomainName { get; init; }

    public string? StaticDomainName { get; init; }

    public IReadOnlyList<string> SearchDomains { get; init; } = [];

    public IReadOnlyList<string> StaticSearchDomains { get; init; } = [];

    public int? MaxStaticServers { get; init; }

    public int? MaxStaticSearchDomains { get; init; }
}

public sealed record CurrentHostName
{
    public bool UseDhcp { get; init; }

    public string? HostName { get; init; }

    public string? StaticHostName { get; init; }
}
