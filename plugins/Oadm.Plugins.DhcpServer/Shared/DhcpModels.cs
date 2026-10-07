using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using Oadm.Plugins.Network.Model;
using Oadm.Sdk.Network;

namespace Oadm.Plugins.DhcpServer;

/// <summary>Ids and names shared by the server part, the page and the tests.</summary>
public static class DhcpServerPluginInfo
{
    public const string PluginId = "oadm.dhcp-server";
    public const string DisplayName = "DHCP server";
    public const string IconKey = "network";

    /// <summary>Lease time handed to clients (24 h); renewal at 50 %, rebinding at 87.5 %.</summary>
    public static readonly TimeSpan LeaseTime = TimeSpan.FromHours(24);
}

/// <summary>Page backend methods (<c>InvokeAsync</c>) and live event topics.</summary>
public static class DhcpServerMethods
{
    /// <summary>() -> <see cref="DhcpState"/> with interfaces and all leases.</summary>
    public const string GetState = "getState";

    /// <summary><see cref="DhcpSaveRequest"/> -> <see cref="DhcpSaveReply"/>.</summary>
    public const string Save = "save";

    /// <summary><see cref="StaticLeaseRequest"/> -> <see cref="StaticLeaseReply"/> (add or edit).</summary>
    public const string SaveStatic = "saveStatic";

    /// <summary><see cref="LeaseRequest"/> -> <see cref="DhcpState"/> (no leases).</summary>
    public const string DeleteStatic = "deleteStatic";

    /// <summary><see cref="LeaseRequest"/> -> <see cref="StaticLeaseReply"/>: a dynamic lease becomes static.</summary>
    public const string MakeStatic = "makeStatic";

    /// <summary><see cref="LeaseRequest"/> -> <see cref="DhcpState"/> (no leases): forgets a dynamic lease.</summary>
    public const string Release = "release";

    /// <summary>Event: <see cref="DhcpState"/> without interfaces and leases (status changed).</summary>
    public const string StateTopic = "state";

    /// <summary>Event: <see cref="LeasesEvent"/>, changed and removed leases (batched, at most every 500 ms).</summary>
    public const string LeasesTopic = "leases";
}

/// <summary>Persisted settings (plugin setting <c>config</c>), restored on server start. The plugin starts disabled.</summary>
public sealed record DhcpConfig
{
    public bool Enabled { get; init; }

    public string? InterfaceId { get; init; }

    /// <summary>Interface name when it was saved ("Ethernet"), for "Interface Ethernet is not available".</summary>
    public string? InterfaceName { get; init; }

    public string? RangeStart { get; init; }

    public string? RangeEnd { get; init; }
}

/// <summary>What clients get on an interface (everything derived, nothing asked).</summary>
/// <param name="InterfaceId">OS interface id.</param>
/// <param name="Name">"Ethernet".</param>
/// <param name="Address">Server address on it ("10.0.0.17"), also the server identifier.</param>
/// <param name="PrefixLength">24.</param>
/// <param name="Router">The interface's gateway when it is in the subnet, else null.</param>
/// <param name="Dns">The interface's IPv4 DNS servers.</param>
/// <param name="Domain">The interface's DNS suffix, null when none.</param>
public sealed record DhcpNetworkInfo(string InterfaceId, string Name, string Address, int PrefixLength, string? Router, IReadOnlyList<string> Dns, string? Domain)
{
    [JsonIgnore]
    public uint ServerAddress => Ipv4.TryParse(Address, out var a) ? a : 0;

    [JsonIgnore]
    public uint NetworkAddress => Ipv4.Network(ServerAddress, PrefixLength);

    [JsonIgnore]
    public uint BroadcastAddress => Ipv4.Broadcast(ServerAddress, PrefixLength);

    /// <summary>"10.0.0.0/24".</summary>
    [JsonIgnore]
    public string Subnet => string.Create(CultureInfo.InvariantCulture, $"{Ipv4.Format(NetworkAddress)}/{PrefixLength}");

    /// <summary>"Clients get mask 255.255.255.0, router 10.0.0.138, DNS 10.0.0.138, lease 24 h".</summary>
    [JsonIgnore]
    public string ClientsGet
    {
        get
        {
            var text = $"Clients get mask {Ipv4.MaskText(PrefixLength)}, "
                + (Router is null ? "no router" : "router " + Router) + ", "
                + (Dns.Count == 0 ? "no DNS" : "DNS " + string.Join(", ", Dns))
                + (Domain is null ? string.Empty : ", domain " + Domain)
                + string.Create(CultureInfo.InvariantCulture, $", lease {DhcpServerPluginInfo.LeaseTime.TotalHours:0} h");
            return text;
        }
    }

    public bool Contains(uint address) => Ipv4.SameSubnet(address, ServerAddress, PrefixLength);
}

/// <summary>One lease of the list.</summary>
/// <param name="Mac">"B8:A4:4F:63:13:39".</param>
/// <param name="Address">"10.0.0.48".</param>
/// <param name="HostName">Host name the client sent.</param>
/// <param name="Name">Name of a static lease (optional).</param>
/// <param name="IsStatic">Static (reserved) or dynamic.</param>
/// <param name="State">"Active", "Expired", "Released", "Reserved" (static, not seen yet).</param>
/// <param name="ExpiresUtc">End of an active lease.</param>
public sealed record LeaseInfo(string Mac, string Address, string? HostName, string? Name, bool IsStatic, string State, DateTime? ExpiresUtc)
{
    public const string Active = "Active";
    public const string Expired = "Expired";
    public const string Released = "Released";
    public const string Reserved = "Reserved";
}

/// <summary>Everything the page shows.</summary>
public sealed record DhcpState
{
    public DhcpConfig Config { get; init; } = new();

    public ServiceStatus Status { get; init; } = new(ServiceStatus.Neutral, "Stopped");

    /// <summary>Interfaces with an IPv4 address (no "all").</summary>
    public IReadOnlyList<InterfaceOption> Interfaces { get; init; } = [];

    /// <summary>Per interface of <see cref="Interfaces"/>: subnet and what clients get.</summary>
    public IReadOnlyList<DhcpNetworkInfo> Networks { get; init; } = [];

    /// <summary>All leases (full state only).</summary>
    public IReadOnlyList<LeaseInfo>? Leases { get; init; }

    /// <summary>Lease list version (events with a version up to this one are already contained).</summary>
    public long LeaseVersion { get; init; }

    /// <summary>Other DHCP servers seen by the last check.</summary>
    public IReadOnlyList<string> OtherServers { get; init; } = [];
}

/// <param name="Enabled">Enable DHCP server.</param>
/// <param name="InterfaceId">Selected interface.</param>
/// <param name="RangeStart">First address.</param>
/// <param name="RangeEnd">Last address.</param>
/// <param name="ConfirmedOtherServers">The user confirmed enabling although these servers answer (from the popup).</param>
public sealed record DhcpSaveRequest(bool Enabled, string? InterfaceId, string? RangeStart, string? RangeEnd, IReadOnlyList<string>? ConfirmedOtherServers = null);

/// <param name="Saved">False: nothing changed (field errors, or another server answers and needs a confirmation).</param>
/// <param name="FieldErrors">Property ("RangeStart", "RangeEnd", "Interface") -> message, shown under the field.</param>
/// <param name="OtherServers">Other DHCP servers that answer: the page asks before saving again with them confirmed.</param>
/// <param name="State">The state after saving (no leases).</param>
public sealed record DhcpSaveReply(bool Saved, IReadOnlyDictionary<string, string>? FieldErrors, IReadOnlyList<string>? OtherServers, DhcpState State);

/// <param name="Mac">MAC address.</param>
/// <param name="Address">IPv4 address.</param>
/// <param name="Name">Optional name.</param>
/// <param name="OriginalMac">Edit: the MAC of the static lease being edited; null = add.</param>
public sealed record StaticLeaseRequest(string? Mac, string? Address, string? Name, string? OriginalMac = null);

public sealed record LeaseRequest(string? Mac);

/// <param name="Saved">False: field errors.</param>
/// <param name="FieldErrors">"Mac", "Address", "Name" -> message.</param>
public sealed record StaticLeaseReply(bool Saved, IReadOnlyDictionary<string, string>? FieldErrors);

/// <param name="Version">Lease list version after these changes.</param>
/// <param name="Changed">Added or changed leases.</param>
/// <param name="Removed">MAC addresses of removed leases.</param>
public sealed record LeasesEvent(long Version, IReadOnlyList<LeaseInfo> Changed, IReadOnlyList<string> Removed);

public static class DhcpJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Throws <see cref="ArgumentException"/> (INVALID_ARGUMENT) for missing or unreadable JSON.</summary>
    public static T Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("The request is empty.", nameof(json));
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, Options) ?? throw new ArgumentException("The request is empty.", nameof(json));
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The request cannot be read: " + ex.Message, nameof(json), ex);
        }
    }
}
