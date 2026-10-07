using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using Oadm.Sdk.Vapix;

namespace Oadm.Core.Vapix;

/// <summary>Pure parsing of VAPIX responses. No I/O, unit-tested against recorded fixtures.</summary>
public static partial class VapixParsers
{
    private const string RootPrefix = "root.";

    /// <summary>
    /// Extracts the serial from a <c>WWW-Authenticate</c> header value such as
    /// <c>Digest realm="AXIS_B8A44F631339", nonce=...</c> or <c>Basic realm="AXIS_B8A44F631339"</c>.
    /// Returns null when the realm is not an Axis realm.
    /// </summary>
    public static string? ParseSerialFromRealm(string? wwwAuthenticate)
    {
        if (string.IsNullOrEmpty(wwwAuthenticate))
        {
            return null;
        }

        var match = AxisRealmRegex().Match(wwwAuthenticate);
        return match.Success ? match.Groups["serial"].Value.ToUpperInvariant() : null;
    }

    /// <summary>Extracts the serial from any of the given <c>WWW-Authenticate</c> values.</summary>
    public static string? ParseSerialFromRealm(IEnumerable<string> wwwAuthenticateValues)
    {
        ArgumentNullException.ThrowIfNull(wwwAuthenticateValues);
        return wwwAuthenticateValues.Select(ParseSerialFromRealm).FirstOrDefault(s => s is not null);
    }

    /// <summary>Returns the property list of a basicdeviceinfo.cgi response (getAllProperties or getAllUnrestrictedProperties).</summary>
    public static IReadOnlyDictionary<string, string> ParseBasicDeviceInfoProperties(string json)
    {
        using var doc = ParseJson(json, "basicdeviceinfo.cgi");
        var root = doc.RootElement;
        ThrowIfJsonError(root, "basicdeviceinfo.cgi");

        if (!root.TryGetProperty("data", out var data)
            || !data.TryGetProperty("propertyList", out var list)
            || list.ValueKind != JsonValueKind.Object)
        {
            throw new VapixException("basicdeviceinfo.cgi response has no data.propertyList.");
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in list.EnumerateObject())
        {
            result[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText();
        }

        return result;
    }

    /// <summary>Parses a basicdeviceinfo.cgi response into the SDK record.</summary>
    public static BasicDeviceInfo ParseBasicDeviceInfo(string json)
    {
        var p = ParseBasicDeviceInfoProperties(json);
        var serial = Get(p, "SerialNumber") ?? throw new VapixException("basicdeviceinfo.cgi response has no SerialNumber.");
        return new BasicDeviceInfo(
            SerialNumber: NormalizeSerial(serial),
            ProdNbr: Get(p, "ProdNbr") ?? string.Empty,
            ProdShortName: Get(p, "ProdShortName"),
            ProdFullName: Get(p, "ProdFullName"),
            Version: Get(p, "Version") ?? string.Empty,
            HardwareId: Get(p, "HardwareID"),
            Architecture: Get(p, "Architecture"),
            ProdType: Get(p, "ProdType"));
    }

    /// <summary>
    /// Parses <c>param.cgi?action=list</c> output. Keys are returned without the optional
    /// <c>root.</c> prefix (the device adds it for whole groups but not for single parameters).
    /// Lines starting with <c>#</c> are per-group errors and are returned in <paramref name="errors"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseParameterList(string text, out IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var errorList = new List<string>();

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                errorList.Add(line.TrimStart('#', ' '));
                continue;
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq];
            if (key.StartsWith(RootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                key = key[RootPrefix.Length..];
            }

            result[key] = line[(eq + 1)..];
        }

        errors = errorList;
        return result;
    }

    /// <summary>Parses <c>param.cgi?action=list</c> output, ignoring per-group errors.</summary>
    public static IReadOnlyDictionary<string, string> ParseParameterList(string text)
    {
        return ParseParameterList(text, out _);
    }

    /// <summary>Builds <see cref="NetworkInfo"/> from parameters listed with <see cref="VapixClient.NetworkInfoParameters"/>.</summary>
    public static NetworkInfo ParseNetworkInfo(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var bootProto = Get(parameters, "Network.BootProto");
        return new NetworkInfo(
            DhcpEnabled: bootProto is null ? null : string.Equals(bootProto, "dhcp", StringComparison.OrdinalIgnoreCase),
            HttpsEnabled: ParseBool(Get(parameters, "HTTPS.Enabled")),
            Dot1xEnabled: ParseBool(Get(parameters, "Network.Interface.I0.dot1x.Enabled")),
            UpnpFriendlyName: Get(parameters, "Network.UPnP.FriendlyName"));
    }

    /// <summary>
    /// Builds <see cref="ImageCapabilities"/> from param.cgi groups <c>Properties.Image</c> and <c>Image</c>:
    /// formats from <c>Properties.Image.Format</c> ("jpeg,mjpeg,h264,h265"), resolutions from
    /// <c>Properties.Image.Resolution</c>, and one source per <c>Image.I&lt;n&gt;</c> that is not disabled
    /// (view areas on single-sensor cameras, sensors on multisensor cameras, channels on encoders).
    /// Without any <c>Image.I&lt;n&gt;</c> the device has one source, camera 1.
    /// </summary>
    public static ImageCapabilities ParseImageCapabilities(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var formats = (Get(parameters, "Properties.Image.Format") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => f.ToLowerInvariant())
            .Distinct()
            .ToList();
        var resolutions = ParseResolutions(Get(parameters, "Properties.Image.Resolution"));

        var indices = new SortedSet<int>();
        foreach (var key in parameters.Keys)
        {
            var match = ImageIndexRegex().Match(key);
            if (match.Success)
            {
                indices.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
            }
        }

        var sources = new List<VideoSourceInfo>();
        foreach (var n in indices)
        {
            if (ParseBool(Get(parameters, $"Image.I{n}.Enabled")) == false)
            {
                continue;
            }

            var sensor = int.TryParse(Get(parameters, $"Image.I{n}.Source"), NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : 0;
            var own = ParseResolutions(Get(parameters, $"Properties.Image.I{n}.Resolution"));
            sources.Add(new VideoSourceInfo(n + 1, Get(parameters, $"Image.I{n}.Name") ?? $"Camera {n + 1}", sensor, own.Count > 0 ? own : resolutions));
        }

        if (sources.Count == 0)
        {
            sources.Add(new VideoSourceInfo(1, "Camera", 0, resolutions));
        }

        return new ImageCapabilities(formats, resolutions, sources);
    }

    private static List<ImageSize> ParseResolutions(string? list)
    {
        var result = new List<ImageSize>();
        foreach (var item in (list ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (ImageSize.TryParse(item, out var size) && !result.Contains(size))
            {
                result.Add(size);
            }
        }

        return result;
    }

    [GeneratedRegex(@"^Image\.I(\d+)\.(Enabled|Name|Source)$", RegexOptions.CultureInvariant)]
    private static partial Regex ImageIndexRegex();

    /// <summary>Parses a systemready.cgi response.</summary>
    public static SystemReadyInfo ParseSystemReady(string json)
    {
        using var doc = ParseJson(json, "systemready.cgi");
        var root = doc.RootElement;
        ThrowIfJsonError(root, "systemready.cgi");
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            throw new VapixException("systemready.cgi response has no data.");
        }

        string? Str(string name) => data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var uptime = Str("uptime");
        return new SystemReadyInfo(
            SystemReady: ParseBool(Str("systemready")) ?? false,
            NeedSetup: ParseBool(Str("needsetup")),
            PassphrasePolicy: Str("passphrasepolicy"),
            UptimeSeconds: long.TryParse(uptime, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u) ? u : null,
            BootId: Str("bootid"));
    }

    /// <summary>
    /// Interprets the plain text answer of <c>pwdgrp.cgi?action=add</c>. Success looks like
    /// <c>Created account root.</c>; failures contain <c>Error</c>. Must be combined with a 2xx status.
    /// </summary>
    public static bool IsPwdgrpSuccess(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return !body.Contains("error", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"yes"/"no", "true"/"false", "1"/"0"; null for anything else.</summary>
    public static bool? ParseBool(string? value)
    {
        return value?.Trim().ToUpperInvariant() switch
        {
            "YES" or "TRUE" or "1" or "ON" => true,
            "NO" or "FALSE" or "0" or "OFF" => false,
            _ => null,
        };
    }

    /// <summary>Serial / MAC as upper hex without separators.</summary>
    public static string NormalizeSerial(string serial)
    {
        ArgumentNullException.ThrowIfNull(serial);
        return new string(serial.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    }

    private static string? Get(IReadOnlyDictionary<string, string> p, string key)
    {
        return p.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : null;
    }

    private static JsonDocument ParseJson(string json, string endpoint)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new VapixException($"{endpoint} returned invalid JSON.", ex);
        }
    }

    private static void ThrowIfJsonError(JsonElement root, string endpoint)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
        {
            var code = error.TryGetProperty("code", out var c) ? c.GetRawText() : "?";
            var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
            throw new VapixException($"{endpoint} error {code}: {message}");
        }
    }

    [GeneratedRegex("realm=\"AXIS_(?<serial>[0-9A-Za-z]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex AxisRealmRegex();

    /// <summary>Parses <c>apidiscovery.cgi getApiList</c>: <c>data.apiList[] { id, version, name, status }</c>.</summary>
    public static IReadOnlyList<DeviceApi> ParseApiList(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("error", out var error))
        {
            throw new VapixException($"apidiscovery failed: {error}");
        }

        var result = new List<DeviceApi>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("apiList", out var list))
        {
            foreach (var api in list.EnumerateArray())
            {
                var id = api.TryGetProperty("id", out var i) ? i.GetString() : null;
                var version = api.TryGetProperty("version", out var v) ? v.GetString() : null;
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(version))
                {
                    continue;
                }

                result.Add(new DeviceApi(
                    id,
                    version,
                    api.TryGetProperty("name", out var n) ? n.GetString() : null,
                    api.TryGetProperty("status", out var st) ? st.GetString() : null));
            }
        }

        return result;
    }
}
