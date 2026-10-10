using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Oadm.Plugins.DeviceConfig;

/// <summary>A configuration file could not be used; the message is the user text.</summary>
public sealed class ConfigFileException : Exception
{
    public ConfigFileException()
    {
    }

    public ConfigFileException(string message)
        : base(message)
    {
    }

    public ConfigFileException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>One settings area of a file ("param.v2"), with its plain name and the number of values OADM imports.</summary>
public sealed record ConfigSection(string Key, string Name, int ValueCount);

/// <summary>
/// A device configuration file: the answer of <c>GET /config/rest/$export</c>
/// (<c>{"status":"success","data":{"param.v2":{...}},"metadata":{"fwVersion":"12.11.77","prodNbr":"P3265-V"}}</c>).
/// </summary>
public sealed partial class ConfigFile
{
    private ConfigFile(JsonObject data, string model, string? firmware)
    {
        Data = data;
        Model = model;
        FirmwareVersion = firmware;
        Sections = [.. data.Where(p => IsSectionKey(p.Key) && p.Value is JsonObject)
            .Select(p => new ConfigSection(p.Key, ConfigSections.Name(p.Key), ConfigIdentity.CountImported(p.Key, p.Value!)))];
    }

    /// <summary>The <c>data</c> object as in the file (identity values included).</summary>
    public JsonObject Data { get; }

    /// <summary><c>metadata.prodNbr</c>, "P3265-V".</summary>
    public string Model { get; }

    /// <summary><c>metadata.fwVersion</c>.</summary>
    public string? FirmwareVersion { get; }

    /// <summary>Settings areas in file order.</summary>
    public IReadOnlyList<ConfigSection> Sections { get; }

    /// <summary>"From AXIS P3265-V, AXIS OS 12.11.77".</summary>
    public string SourceText => "From " + ModelNames.Display(Model) + (string.IsNullOrWhiteSpace(FirmwareVersion) ? string.Empty : ", AXIS OS " + FirmwareVersion);

    /// <summary>Event recipients exist: their passwords are not in the file.</summary>
    public bool HasRecipients =>
        Data[ConfigSections.ActionRules] is JsonObject rules && rules["recipients"] is JsonArray { Count: > 0 };

    /// <summary>Reads a file of at most <see cref="DeviceConfigIds.MaxFileBytes"/>; throws <see cref="ConfigFileException"/>.</summary>
    public static async Task<ConfigFile> ReadAsync(Stream stream, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + n > DeviceConfigIds.MaxFileBytes)
            {
                throw new ConfigFileException(DeviceConfigIds.NotAConfigFile + ": larger than 2 MB");
            }

            buffer.Write(chunk, 0, n);
        }

        return Parse(buffer.ToArray());
    }

    public static ConfigFile Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Parse(System.Text.Encoding.UTF8.GetBytes(json));
    }

    public static ConfigFile Parse(ReadOnlySpan<byte> utf8)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(utf8, documentOptions: new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException ex)
        {
            throw new ConfigFileException(DeviceConfigIds.NotAConfigFile, ex);
        }

        if (root is not JsonObject obj || obj["data"] is not JsonObject data || !data.Any(p => IsSectionKey(p.Key) && p.Value is JsonObject))
        {
            throw new ConfigFileException(DeviceConfigIds.NotAConfigFile);
        }

        var metadata = obj["metadata"] as JsonObject;
        var model = Text(metadata?["prodNbr"]);
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ConfigFileException(DeviceConfigIds.NoModel);
        }

        obj.Remove("data");
        return new ConfigFile(data, model.Trim(), Text(metadata?["fwVersion"])?.Trim());
    }

    /// <summary>"param.v2", "action-rules.v2".</summary>
    public static bool IsSectionKey(string key) => SectionKey().IsMatch(key);

    /// <summary>The API name and major version of a section key: ("param", "v2").</summary>
    public static (string Api, string Version) SplitKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var dot = key.LastIndexOf('.');
        return (key[..dot], key[(dot + 1)..]);
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*\\.v[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SectionKey();
}

/// <summary>Plain names of the settings areas.</summary>
public static class ConfigSections
{
    public const string Param = "param.v2";
    public const string ActionRules = "action-rules.v2";
    public const string PowerSettings = "power-settings.v2";

    public static string Name(string key) => key switch
    {
        Param => "Network and system settings",
        ActionRules => "Event rules",
        "event-schedules.v2" => "Schedules",
        "event-mqtt-bridge.v2" => "MQTT events",
        "time.v2" => "Time zone",
        PowerSettings => "Power",
        "user-management.v2" => "Password policy",
        _ => key,
    };
}

/// <summary>
/// Values that belong to one device or cannot be written. They are never imported: one file goes to many devices, and
/// host name or a fixed IP address would be the same on all of them.
/// </summary>
public static class ConfigIdentity
{
    /// <summary>Paths in <c>param.v2</c> ("*" = every element of a collection).</summary>
    public static readonly IReadOnlyList<string[]> ParamPaths =
    [
        ["Network", "HostName"],
        ["Network", "IPAddress"],
        ["Network", "SubnetMask"],
        ["Network", "DefaultRouter"],
        ["Network", "Broadcast"],
        ["Network", "BootProto"],
        ["Network", "Bonjour", "FriendlyName"],
        ["Network", "UPnP", "FriendlyName"],
        ["Network", "VolatileHostName"],
        ["Network", "DHCP", "VendorClass"],
        ["Network", "IPv6", "IPAddress"],
        ["Network", "IPv6", "DefaultRouter"],
        ["Network", "InterfaceCollection", "*", "dot1x", "EAPTLS", "Identity"],
        ["SNMP", "EngineBoots"],
        ["SNMP", "InitialUserPasswdSet"],

        // The time zone belongs to time.v2. Importing these parameters sets a fixed POSIX zone and turns the DHCP time zone
        // off, also when the user left "Time zone" out (seen on AXIS M3098-LV, AXIS OS 12.11.118).
        ["Time", "POSIXTimeZone"],
        ["Time", "DST"],
        ["Time", "ObtainFromDHCP"],

        // Names of the device's own audio clips; the clips are not in the file (the Q3546-LVE refuses even its own name).
        ["MediaClipCollection"],
    ];

    /// <summary>
    /// Read-only parts of <c>power-settings.v2</c>. <c>poeAllocationRequest</c> is written: a Q3546-LVE (AXIS OS 12.11.118)
    /// refuses the area without it ("No 'poeAllocationRequest' object provided in import data").
    /// </summary>
    public static readonly IReadOnlyList<string[]> PowerPaths =
    [
        ["powerStatus"],
    ];

    /// <summary>A copy of the section without the device's own and read-only values.</summary>
    public static JsonNode Clean(string key, JsonNode section)
    {
        ArgumentNullException.ThrowIfNull(section);
        var copy = section.DeepClone();
        var paths = key switch
        {
            ConfigSections.Param => ParamPaths,
            ConfigSections.PowerSettings => PowerPaths,
            _ => [],
        };
        foreach (var path in paths)
        {
            Remove(copy, path, 0);
        }

        if (key == ConfigSections.Param)
        {
            KeepAutoRotation(copy);
        }

        return copy;
    }

    /// <summary>
    /// Writing a rotation turns auto rotation off, even the same value and even with <c>AutoRotationEnabled=yes</c> in the
    /// same request (AXIS Q3546-LVE, AXIS OS 12.11.118). Where the file has auto rotation on, the rotation is left out: the
    /// device chooses it.
    /// </summary>
    private static void KeepAutoRotation(JsonNode param)
    {
        if (param["ImageSourceCollection"] is JsonArray sources)
        {
            foreach (var source in sources.OfType<JsonObject>())
            {
                if (IsYes(source["AutoRotationEnabled"]))
                {
                    source.Remove("Rotation");
                }
            }
        }

        if (param["ImageCollection"] is JsonArray views)
        {
            foreach (var appearance in views.OfType<JsonObject>().Select(v => v["Appearance"]).OfType<JsonObject>())
            {
                if (IsYes(appearance["AutoRotationEnabled"]))
                {
                    appearance.Remove("Rotation");
                }
            }
        }
    }

    private static bool IsYes(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase);

    /// <summary>Number of values that would be imported (leaves after <see cref="Clean"/>).</summary>
    public static int CountImported(string key, JsonNode section) => CountLeaves(Clean(key, section));

    private static int CountLeaves(JsonNode? node) => node switch
    {
        JsonObject obj => obj.Sum(p => CountLeaves(p.Value)),
        JsonArray array => array.Sum(CountLeaves),
        _ => 1,
    };

    private static void Remove(JsonNode? node, string[] path, int index)
    {
        var name = path[index];
        if (name == "*")
        {
            if (node is JsonArray array)
            {
                foreach (var item in array)
                {
                    Remove(item, path, index + 1);
                }
            }

            return;
        }

        if (node is not JsonObject obj || !obj.ContainsKey(name))
        {
            return;
        }

        if (index == path.Length - 1)
        {
            obj.Remove(name);
            return;
        }

        var child = obj[name];
        Remove(child, path, index + 1);
        if (child is JsonObject { Count: 0 })
        {
            obj.Remove(name);
        }
    }
}

/// <summary>File names of exported configurations; the MAC address in them is what a ZIP import matches on.</summary>
public static partial class ConfigFileNames
{
    /// <summary>"10.0.0.48_ACCC8E5F6071_P3265-V_config.json".</summary>
    public static string ForDevice(string address, string serial, string? model) =>
        Safe(address) + "_" + Safe(serial) + "_" + Safe(ModelNames.Normalize(model).Length == 0 ? "device" : StripBrand(model!)) + "_config.json";

    /// <summary>"oadm-config-2026-10-10.zip".</summary>
    public static string ForZip(DateTime localDate) =>
        "oadm-config-" + localDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) + ".zip";

    /// <summary>Every MAC address in the name (12 hex digits, also with ":" "-" "." between the bytes), upper case without separators.</summary>
    public static IReadOnlyList<string> SerialsIn(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        // Not Path.GetFileName: on Windows it cuts "b8:a4:..." at the first ":" (drive separator).
        var name = fileName.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        return [.. Mac().Matches(name).Select(m => new string([.. m.Value.Where(char.IsAsciiHexDigit)]).ToUpperInvariant()).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>A serial number in the device table's form (upper hex, no separators).</summary>
    public static string NormalizeSerial(string serial) =>
        new string([.. (serial ?? string.Empty).Where(char.IsAsciiHexDigit)]).ToUpperInvariant();

    private static string StripBrand(string model)
    {
        var text = model.Trim();
        return text.StartsWith("AXIS ", StringComparison.OrdinalIgnoreCase) ? text[5..].TrimStart() : text;
    }

    private static string Safe(string text)
    {
        var chars = (text ?? string.Empty).Trim().Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '-').ToArray();
        var result = new string(chars).Trim('-');
        return result.Length == 0 ? "device" : result;
    }

    [GeneratedRegex("(?<![0-9A-Fa-f])(?:[0-9A-Fa-f]{2}[:.\\-]?){5}[0-9A-Fa-f]{2}(?![0-9A-Fa-f])", RegexOptions.CultureInvariant)]
    private static partial Regex Mac();
}
