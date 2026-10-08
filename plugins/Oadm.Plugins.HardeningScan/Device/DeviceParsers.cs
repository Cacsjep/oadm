using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;

using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.HardeningScan.Device;

/// <summary>A REST API listed by <c>GET /config/discover</c> (released or beta).</summary>
public sealed record RestApi(string Name, int Major, string? Version, string? State);

/// <summary>One disk of <c>disks/list.cgi?diskid=all</c>.</summary>
public sealed record DiskInfo(string Id, string? Group, string? Status, bool? EncryptionEnabled, bool? Encrypted)
{
    /// <summary>Connected and in use (status OK).</summary>
    public bool IsConnected => string.Equals(Status, "OK", StringComparison.OrdinalIgnoreCase);

    public bool IsSdCard => Id.StartsWith("SD_DISK", StringComparison.OrdinalIgnoreCase);

    /// <summary>"SD card", "Network share" or the disk id.</summary>
    public string Label => IsSdCard ? "SD card" : string.Equals(Id, "NetworkShare", StringComparison.OrdinalIgnoreCase) ? "Network share" : Id;
}

/// <summary>GET <c>/config/rest/firewall/v1</c>.</summary>
public sealed record FirewallInfo(bool Activated, string? DefaultPolicy, IReadOnlyList<string> RuleTypes);

/// <summary>GET <c>/config/rest/snmp/v1</c> or the legacy <c>SNMP.*</c> parameters.</summary>
public sealed record SnmpInfo(bool Enabled, bool V1, bool V2, bool V3);

/// <summary>Parsers of the read answers that no other plugin parses yet. Every XML goes through <see cref="DeviceXml"/>.</summary>
public static class DeviceParsers
{
    /// <summary>The REST APIs of <c>GET /config/discover</c> by name and major version ("firewall" 1).</summary>
    public static IReadOnlyDictionary<string, RestApi> ParseDiscover(string json)
    {
        var result = new Dictionary<string, RestApi>(StringComparer.OrdinalIgnoreCase);
        JsonObject? apis;
        try
        {
            apis = JsonNode.Parse(json)?["apis"] as JsonObject;
        }
        catch (JsonException)
        {
            return result;
        }

        foreach (var (name, versions) in apis ?? [])
        {
            if (versions is not JsonObject byVersion)
            {
                continue;
            }

            foreach (var (key, entry) in byVersion)
            {
                if (key.Length < 2 || key[0] != 'v' || !int.TryParse(key.AsSpan(1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var major))
                {
                    continue;
                }

                result[Key(name, major)] = new RestApi(name, major, Text(entry?["version"]), Text(entry?["state"]));
            }
        }

        return result;
    }

    public static string Key(string name, int major) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{name}/v{major}");

    /// <summary><c>data</c> of a REST answer; a <c>status: error</c> answer throws with the device's message.</summary>
    public static JsonNode Data(string json, string api)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"{api}: the device sent an unreadable answer.", ex);
        }

        if (string.Equals(Text(root?["status"]), "error", StringComparison.OrdinalIgnoreCase))
        {
            var message = Text(root?["error"]?["message"]) ?? Text(root?["message"]) ?? "error";
            throw new FormatException($"{api}: {message}");
        }

        return root?["data"] ?? throw new FormatException($"{api}: the device sent no data.");
    }

    /// <summary><c>settings.passphraseComplexity.policy</c> of user-management v2 ("none", "length", "complex").</summary>
    public static string? ParsePasswordPolicy(string json) =>
        Text(Data(json, "user-management")["settings"]?["passphraseComplexity"]?["policy"]);

    public static FirewallInfo ParseFirewall(string json)
    {
        var data = Data(json, "firewall");
        var rules = data["conf"]?["rules"];
        var types = (rules?["activeRules"] as JsonArray ?? [])
            .Select(r => Text(r?["ruleType"]) ?? "rule")
            .ToList();
        return new FirewallInfo(Bool(data["activated"]) ?? false, Text(rules?["activeDefaultPolicy"]), types);
    }

    public static bool? ParseLldpActivated(string json) => Bool(Data(json, "lldp")["activated"]);

    public static SnmpInfo ParseSnmp(string json)
    {
        var data = Data(json, "snmp");
        return new SnmpInfo(
            Bool(data["enabled"]) ?? false,
            Bool(data["snmpV1"]?["enabled"]) ?? false,
            Bool(data["snmpV2"]?["enabled"]) ?? false,
            Bool(data["snmpV3"]?["enabled"]) ?? false);
    }

    /// <summary>Legacy <c>SNMP.Enabled</c>, <c>SNMP.V1</c>, <c>SNMP.V2c</c>, <c>SNMP.V3</c>; null without SNMP parameters.</summary>
    public static SnmpInfo? ParseSnmpParameters(ParamList parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (parameters.Bool("SNMP.Enabled") is not { } enabled)
        {
            return null;
        }

        return new SnmpInfo(enabled, parameters.Bool("SNMP.V1") ?? false, parameters.Bool("SNMP.V2c") ?? false, parameters.Bool("SNMP.V3") ?? false);
    }

    /// <summary>The OpenID provider metadata URL of oidcsetup v1 (the device quotes values: <c>"\"\""</c> = empty), null when empty.</summary>
    public static string? ParseOidcProvider(string json)
    {
        var url = Text(Data(json, "oidcsetup")["BaseConfigEntity"]?["OIDC_ProviderMetadataURL"])?.Trim().Trim('"').Trim();
        return string.IsNullOrEmpty(url) ? null : url;
    }

    public static IReadOnlyList<DiskInfo> ParseDisks(string xml)
    {
        try
        {
            var document = DeviceXml.Parse(xml.Trim());
            return [.. document.Descendants().Where(e => e.Name.LocalName == "disk").Select(d => new DiskInfo(
                (string?)d.Attribute("diskid") ?? string.Empty,
                (string?)d.Attribute("group"),
                (string?)d.Attribute("status"),
                ParamList.ParseBool((string?)d.Attribute("encryptionenabled")),
                ParamList.ParseBool((string?)d.Attribute("diskencrypted"))))
                .Where(d => d.Id.Length > 0)];
        }
        catch (XmlException ex)
        {
            throw new FormatException("disks/list.cgi: the device sent an unreadable answer.", ex);
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node is JsonValue other ? other.ToJsonString() : null;

    private static bool? Bool(JsonNode? node) =>
        node is JsonValue value
            ? value.TryGetValue<bool>(out var b) ? b : value.TryGetValue<string>(out var s) ? ParamList.ParseBool(s) : null
            : null;
}
