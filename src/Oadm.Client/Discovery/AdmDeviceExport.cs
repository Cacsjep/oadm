using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Oadm.Client.Discovery;

/// <summary>
/// Reads a device export of AXIS Device Manager (the .json of its device export) into the same lines as a CSV import
/// (<see cref="DeviceImportFile"/>): per device the address (with its ports when they are not 80 / 443), the login, the
/// MAC address (the device that must answer, see <see cref="ImportLine.ExpectedSerial"/>) and its tags. Everything else
/// (model, firmware, host name, product type) OADM reads from the device. Two formats are known from exports:
/// <list type="bullet">
/// <item>1.x (ADM 5, 1.1): <c>{"encrypted", "fileFormatVersionMayor": 1, "data": {"deviceData": {"devices",
/// "deviceTag", "deviceTagRelation"}}}</c>, passwords as they are;</item>
/// <item>2.x (2.0): <c>"fileFormatVersionMayor": 2</c>, passwords base64 (UTF-8), tags as <c>"deviceTags": [{"name",
/// "devices": [device ids]}]</c>.</item>
/// </list>
/// Both tag layouts are read in both versions. Property names are matched case-insensitively and unknown properties are
/// ignored, so small format changes still read.
/// </summary>
public static class AdmDeviceExport
{
    public const string NotAnExport =
        "The file is not a device export of AXIS Device Manager. In AXIS Device Manager, export the devices to a file and choose that file.";

    public const string Encrypted =
        "The export is encrypted. Export the devices again in AXIS Device Manager without encryption.";

    /// <summary>A password of a 2.x export that is not base64 of UTF-8 text.</summary>
    public const string UnreadablePassword = "The password in the export cannot be read.";

    /// <summary>Reads a picked file.</summary>
    /// <exception cref="DeviceImportException">The file cannot be used at all.</exception>
    public static DeviceImportFile Parse(string fileName, byte[] content, bool isTooLarge)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (isTooLarge || content.Length > DeviceImportFile.MaxBytes)
        {
            throw new DeviceImportException(string.Create(CultureInfo.InvariantCulture,
                $"The file is larger than {DeviceImportFile.MaxBytes / (1024 * 1024)} MB."));
        }

        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(content).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            throw new DeviceImportException(NotAnExport);
        }

        return Parse(fileName, text);
    }

    /// <summary>Reads the text of an export.</summary>
    /// <exception cref="DeviceImportException">The file cannot be used at all.</exception>
    public static DeviceImportFile Parse(string fileName, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 16 });
        }
        catch (JsonException)
        {
            throw new DeviceImportException(NotAnExport);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new DeviceImportException(NotAnExport);
            }

            if (Property(root, "encrypted") is { ValueKind: JsonValueKind.True })
            {
                throw new DeviceImportException(Encrypted);
            }

            // Without a version: read as 1.x (passwords as they are).
            var version = Property(root, "fileFormatVersionMayor") is { ValueKind: JsonValueKind.Number } major && major.TryGetInt32(out var number) ? number : 1;
            if (version is not (1 or 2))
            {
                throw new DeviceImportException(string.Create(CultureInfo.InvariantCulture,
                    $"The export has the format version {version}, OADM reads versions 1 and 2. Tell us on GitHub, and import the addresses with Import from file meanwhile."));
            }

            var deviceData = Property(Property(root, "data"), "deviceData");
            if (Property(deviceData, "devices") is not { ValueKind: JsonValueKind.Array } devices)
            {
                throw new DeviceImportException(NotAnExport);
            }

            var tagsOf = TagsByDevice(deviceData);
            var lines = new List<ImportLine>();
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var device in devices.EnumerateArray())
            {
                if (device.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (lines.Count >= DeviceImportFile.MaxLines)
                {
                    throw new DeviceImportException(string.Create(CultureInfo.InvariantCulture,
                        $"The export has more than {DeviceImportFile.MaxLines:N0} devices. Export fewer devices at once."));
                }

                var lineNumber = lines.Count + 1;
                var id = Property(device, "id") is { ValueKind: JsonValueKind.Number } idValue && idValue.TryGetInt64(out var idNumber) ? idNumber : (long?)null;
                var tags = id is { } deviceId && tagsOf.TryGetValue(deviceId, out var list) ? list : [];
                var password = Password(Text(device, "devicePassword"), version);
                var line = DeviceImportFile.CheckLine(
                    lineNumber,
                    Address(device),
                    Text(device, "deviceUserName").Trim(),
                    password ?? "",
                    tags,
                    Serial(Text(device, "macAddress")),
                    seen);
                lines.Add(password is null && line.Problem is null ? line with { Problem = UnreadablePassword } : line);
            }

            if (lines.Count == 0)
            {
                throw new DeviceImportException("The export contains no devices.");
            }

            return DeviceImportFile.FromAdmExport(fileName ?? "", lines, Servers(Property(root, "data")));
        }
    }

    /// <summary>The address as ADM had it; its ports only when they are not 443 / 80 (HTTPS first, like OADM).</summary>
    internal static string Address(JsonElement device)
    {
        var address = Text(device, "address").Trim();
        if (address.Length == 0)
        {
            return address;
        }

        var host = address.Contains(':', StringComparison.Ordinal) && !address.StartsWith('[') ? $"[{address}]" : address;
        var https = Port(device, "httpsPort");
        var http = Port(device, "httpPort");
        return https is > 0 and not 443 ? string.Create(CultureInfo.InvariantCulture, $"https://{host}:{https}")
            : http is > 0 and not 80 ? string.Create(CultureInfo.InvariantCulture, $"http://{host}:{http}")
            : address;
    }

    /// <summary>
    /// The password as typed: 1.x as it is, 2.x decoded from base64 (UTF-8); null when a 2.x value is not base64 of UTF-8
    /// text (the line then shows <see cref="UnreadablePassword"/> and gets no login).
    /// </summary>
    internal static string? Password(string value, int version)
    {
        if (version < 2 || value.Length == 0)
        {
            return value;
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(value.Trim()));
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>The MAC address as OADM writes serials (upper hex, no separators); null when it is not one.</summary>
    internal static string? Serial(string mac)
    {
        var serial = new string([.. mac.Where(Uri.IsHexDigit)]).ToUpperInvariant();
        return serial.Length == 12 && mac.All(c => Uri.IsHexDigit(c) || c is ':' or '-' or ' ') ? serial : null;
    }

    /// <summary>
    /// The ADM servers of the export: "serverInformation" (one object in a single-server export, a list when there are
    /// several), by name, else address; distinct.
    /// </summary>
    private static List<string> Servers(JsonElement? data)
    {
        var entries = Property(data, "serverInformation") switch
        {
            { ValueKind: JsonValueKind.Object } one => [one],
            { ValueKind: JsonValueKind.Array } many => [.. many.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)],
            _ => new List<JsonElement>(),
        };
        return [.. entries
            .Select(e => Text(e, "name").Trim() is { Length: > 0 } name ? name : Text(e, "address").Trim())
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Device id -> tag names, from deviceTag (id, name) + deviceTagRelation (deviceTag, device) of 1.x and from
    /// deviceTags (name, devices) of 2.x.
    /// </summary>
    private static Dictionary<long, List<string>> TagsByDevice(JsonElement? deviceData)
    {
        var result = new Dictionary<long, List<string>>();
        void Add(long device, string name)
        {
            if (!result.TryGetValue(device, out var list))
            {
                result[device] = list = [];
            }

            if (!list.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(name);
            }
        }

        var names = new Dictionary<long, string>();
        if (Property(deviceData, "deviceTag") is { ValueKind: JsonValueKind.Array } tags)
        {
            foreach (var tag in tags.EnumerateArray())
            {
                if (Number(tag, "id") is { } id && Text(tag, "name").Trim() is { Length: > 0 } name)
                {
                    names[id] = name;
                }
            }
        }

        if (Property(deviceData, "deviceTagRelation") is { ValueKind: JsonValueKind.Array } relations)
        {
            foreach (var relation in relations.EnumerateArray())
            {
                if (Number(relation, "device") is { } device && Number(relation, "deviceTag") is { } tag && names.TryGetValue(tag, out var name))
                {
                    Add(device, name);
                }
            }
        }

        if (Property(deviceData, "deviceTags") is { ValueKind: JsonValueKind.Array } groups)
        {
            foreach (var group in groups.EnumerateArray())
            {
                if (Text(group, "name").Trim() is { Length: > 0 } name && Property(group, "devices") is { ValueKind: JsonValueKind.Array } members)
                {
                    foreach (var member in members.EnumerateArray())
                    {
                        if (member.ValueKind == JsonValueKind.Number && member.TryGetInt64(out var device))
                        {
                            Add(device, name);
                        }
                    }
                }
            }
        }

        return result;
    }

    private static JsonElement? Property(JsonElement? element, string name)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value)
        {
            return null;
        }

        foreach (var property in value.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static string Text(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? "" : "";

    private static long? Number(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var number) ? number : null;

    private static int? Port(JsonElement element, string name) =>
        Number(element, name) is { } port and >= 1 and <= 65535 ? (int)port : null;
}
