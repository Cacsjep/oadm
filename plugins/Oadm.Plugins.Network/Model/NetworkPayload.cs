using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oadm.Plugins.Network.Model;

/// <summary>
/// Task payload of "Network settings...". Every section is optional: null means "keep unchanged",
/// so only the sections the user touched are written. Per-device values (IPv4 address from the
/// range assignment, host name from the template) are resolved by the dialog into <see cref="Devices"/>.
/// The same payload is validated in the dialog and again on the server before anything is written.
/// </summary>
public sealed record NetworkPayload
{
    public Ipv4Change? Ipv4 { get; init; }

    public Ipv6Change? Ipv6 { get; init; }

    public DnsChange? Dns { get; init; }

    public HostNameChange? HostName { get; init; }

    /// <summary>One entry per selected device, keyed by OADM device id.</summary>
    public IReadOnlyDictionary<Guid, DeviceAssignment> Devices { get; init; } = new Dictionary<Guid, DeviceAssignment>();

    public bool HasChanges => Ipv4 is not null || Ipv6 is not null || Dns is not null || HostName is not null;

    public string ToJson() => JsonSerializer.Serialize(this, PayloadJson.Options);

    /// <summary>Parses the payload; throws <see cref="NetworkValidationException"/> for malformed JSON.</summary>
    public static NetworkPayload Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new NetworkValidationException(["The task has no settings to apply."]);
        }

        try
        {
            return JsonSerializer.Deserialize<NetworkPayload>(json, PayloadJson.Options)
                ?? throw new NetworkValidationException(["The task has no settings to apply."]);
        }
        catch (JsonException ex)
        {
            throw new NetworkValidationException(["The task settings could not be read: " + ex.Message]);
        }
    }
}

public enum Ipv4Mode
{
    Dhcp = 0,
    Static = 1,
}

/// <summary>IPv4 change. For <see cref="Ipv4Mode.Static"/> the address comes per device from <see cref="DeviceAssignment.Ipv4Address"/>.</summary>
public sealed record Ipv4Change(Ipv4Mode Mode, int? PrefixLength = null, string? Gateway = null);

public enum Ipv6Mode
{
    Disabled = 0,

    /// <summary>Router advertisements (SLAAC), DHCPv6 as advertised by the router.</summary>
    Auto = 1,

    /// <summary>Stateful DHCPv6.</summary>
    Dhcp = 2,

    /// <summary>Manual address; only for a single device.</summary>
    Static = 3,
}

public sealed record Ipv6Change(Ipv6Mode Mode, string? Address = null, int? PrefixLength = null, string? Gateway = null);

/// <summary>
/// DNS resolver change. When <see cref="UseDhcp"/> is true the static fields are not written. With
/// <see cref="KeepDomains"/> only the servers change: each device keeps its own static domain name and search
/// domains (read in "Read current settings"), used by "Assign IP address...".
/// </summary>
public sealed record DnsChange(
    bool UseDhcp,
    IReadOnlyList<string>? Servers = null,
    string? DomainName = null,
    IReadOnlyList<string>? SearchDomains = null,
    bool KeepDomains = false);

/// <summary>Host name change. When <see cref="UseDhcp"/> is false the name comes per device from <see cref="DeviceAssignment.HostName"/>.</summary>
public sealed record HostNameChange(bool UseDhcp);

public sealed record DeviceAssignment(string? Ipv4Address = null, string? HostName = null);

/// <summary>Input is invalid. Thrown before anything is written, so the message says so.</summary>
public sealed class NetworkValidationException : Exception
{
    public NetworkValidationException(IReadOnlyList<string> errors)
        : base(string.Join(" ", errors ?? []) + " Nothing was changed.")
    {
        Errors = errors ?? [];
    }

    public NetworkValidationException()
        : this([])
    {
    }

    public NetworkValidationException(string message)
        : this([message])
    {
    }

    public NetworkValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
        Errors = [message];
    }

    public IReadOnlyList<string> Errors { get; }
}

internal static class PayloadJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
