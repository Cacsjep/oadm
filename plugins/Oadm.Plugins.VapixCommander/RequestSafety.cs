using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace Oadm.Plugins.VapixCommander;

/// <summary>What a request does to a device, from the least to the most restricted.</summary>
public enum SafetyLevel
{
    /// <summary>Reads only: runs without a confirmation, against the cached API list.</summary>
    Read = 0,

    /// <summary>Changes the device: needs a confirmation and a fresh API list.</summary>
    Write = 1,

    /// <summary>Can restart or reset the device, change its accounts or cut it off the network: needs its own confirmation.</summary>
    Dangerous = 2,
}

/// <summary>
/// Decides what a request does to a device from the request itself, for the server and the page alike. A command's own
/// <see cref="CommandDefinition.Writes"/> and <see cref="CommandDefinition.Dangerous"/> flags can only raise the level,
/// never lower it: a saved "GET /axis-cgi/factorydefault.cgi" marked as a read is dangerous.
/// </summary>
/// <remarks>
/// A request is a read only when it is a GET, or a POST whose JSON method is a read (<c>get*</c>, <c>list*</c>,
/// <c>status</c>, <c>systemready</c>) or whose SOAP operation is <c>Get*</c>, and its path is not one that changes the
/// device. The changing paths cover every changing command of the bundled library (a test compares them) and the CGIs
/// that restart, reset, upgrade or change accounts. What cannot be told (a value still to be filled in) counts as the
/// most restricted case of its path.
/// </remarks>
public static class RequestSafety
{
    /// <summary>GET <c>action=</c> values that only read; every other action changes the device.</summary>
    private static readonly HashSet<string> ReadActions = new(StringComparer.Ordinal)
    {
        "list", "listdefinitions", "listall", "get", "getstatus", "status", "statusall", "query", "check", "download",
    };

    /// <summary>CGIs that restart, reset or erase, whatever the method and the parameters.</summary>
    private static readonly string[] AlwaysDangerous =
    [
        "restart.cgi", "factorydefault.cgi", "hardfactorydefault.cgi", "firmwareupgrade.cgi", "disks/format.cgi", "record/remove.cgi",
    ];

    /// <summary>JSON CGIs whose every method but the reads is dangerous (network addressing, a restart).</summary>
    private static readonly string[] DangerousUnlessRead = ["network_settings.cgi", "capturemode.cgi"];

    /// <summary>CGIs that change the device with every request.</summary>
    private static readonly string[] AlwaysWrite =
    [
        "applications/upload.cgi", "applications/config.cgi", "applications/license.cgi", "playclip.cgi", "stopclip.cgi",
    ];

    /// <summary>File names that say what they do ("geolocation/set.cgi", "virtualinput/activate.cgi", "disks/mount.cgi").</summary>
    private static readonly HashSet<string> ChangingFileNames = new(StringComparer.Ordinal)
    {
        "add", "modify", "remove", "delete", "set", "update", "activate", "deactivate", "start", "stop", "record", "mount",
        "unmount", "format", "upload", "install", "restart", "reboot", "reset", "erase", "clear",
    };

    /// <summary>param.cgi groups whose change can cut the device off or open it (lower case).</summary>
    private static readonly string[] DangerousParameters = ["network.", "https.", "system.boa", "ieee8021x."];

    /// <summary>Groups below <c>Network.</c> that keep the device reachable (lower case).</summary>
    private static readonly string[] HarmlessNetworkParameters = ["network.upnp.", "network.qos.", "network.bonjour.", "network.zeroconf."];

    /// <summary>
    /// The level of a command as the page shows it, from its definition. Values still to be filled in count as the most
    /// restricted case; the server decides again from the filled-in request (<see cref="Of(CommandDefinition, RenderedRequest)"/>).
    /// </summary>
    public static SafetyLevel Of(CommandDefinition command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var request = command.Request;
        var pairs = new List<KeyValuePair<string, string>>(request.Query ?? []);
        JsonNode? json = null;
        string? xml = null;
        switch (request.EffectiveBodyType)
        {
            case BodyTypes.Json:
                json = request.Body;
                break;
            case BodyTypes.Xml:
                xml = StringOf(request.Body);
                break;
            case BodyTypes.Form when request.Body is JsonObject form:
                pairs.AddRange(form.Select(p => new KeyValuePair<string, string>(p.Key, StringOf(p.Value) ?? Placeholder)));
                break;
        }

        return Max(Flags(command), Classify(request.Method, request.Path, pairs, json, xml));
    }

    /// <summary>The level of the request that is about to be sent: what the server enforces.</summary>
    public static SafetyLevel Of(CommandDefinition command, RenderedRequest rendered)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(rendered);
        JsonNode? json = null;
        string? xml = null;
        if (rendered.Body is { } body && rendered.ContentType is { } contentType)
        {
            if (contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                json = ParseJson(body);
            }
            else if (contentType.Contains("xml", StringComparison.OrdinalIgnoreCase))
            {
                xml = body;
            }
        }

        return Max(Flags(command), Classify(rendered.Method, "/" + rendered.RelativeUri, [.. rendered.Form ?? []], json, xml));
    }

    /// <summary>The level the command's own flags ask for.</summary>
    private static SafetyLevel Flags(CommandDefinition command) =>
        command.Dangerous ? SafetyLevel.Dangerous : command.Writes ? SafetyLevel.Write : SafetyLevel.Read;

    private static SafetyLevel Max(SafetyLevel a, SafetyLevel b) => a > b ? a : b;

    /// <summary>A value that is not known yet (a field of the command).</summary>
    private const string Placeholder = "{{}}";

    private static bool IsOpen(string text) => text.Contains("{{", StringComparison.Ordinal);

    private static SafetyLevel Classify(string method, string pathAndQuery, List<KeyValuePair<string, string>> pairs, JsonNode? json, string? xml)
    {
        var q = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        var path = Unescape(q < 0 ? pathAndQuery : pathAndQuery[..q]).ToLowerInvariant();
        if (q >= 0)
        {
            foreach (var part in pathAndQuery[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=', StringComparison.Ordinal);
                pairs.Add(new(Unescape(eq < 0 ? part : part[..eq]), eq < 0 ? string.Empty : Unescape(part[(eq + 1)..])));
            }
        }

        // A name still to be filled in could become "action".
        var actions = pairs.Where(p => IsOpen(p.Key) || string.Equals(p.Key, "action", StringComparison.OrdinalIgnoreCase))
            .Select(p => IsOpen(p.Key) ? Placeholder : p.Value.ToLowerInvariant())
            .ToList();
        var readActions = actions.TrueForAll(ReadActions.Contains);
        var jsonMethod = json is JsonObject obj && obj.TryGetPropertyValue("method", out var node) ? StringOf(node) : null;
        var readMethod = jsonMethod is not null && IsReadMethod(jsonMethod);

        SafetyLevel level;
        if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
        {
            level = readActions && !IsOpen(path) ? SafetyLevel.Read : SafetyLevel.Write;
        }
        else if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            level = readMethod || IsSoapRead(xml) ? SafetyLevel.Read : SafetyLevel.Write;
        }
        else
        {
            level = SafetyLevel.Write;
        }

        return Max(level, PathLevel(path, pairs, actions, readActions, jsonMethod, readMethod));
    }

    /// <summary>The level a path asks for whatever the method: the list of changing paths.</summary>
    private static SafetyLevel PathLevel(
        string path, List<KeyValuePair<string, string>> pairs, List<string> actions, bool readActions, string? jsonMethod, bool readMethod)
    {
        bool Is(string name) => path.EndsWith("/" + name, StringComparison.Ordinal) || path.Contains("/" + name + "/", StringComparison.Ordinal);

        if (Array.Exists(AlwaysDangerous, Is))
        {
            return SafetyLevel.Dangerous;
        }

        if (Is("pwdgrp.cgi"))
        {
            // Accounts: only "get" reads.
            return actions.Count > 0 && actions.TrueForAll(a => a == "get") ? SafetyLevel.Read : SafetyLevel.Dangerous;
        }

        if (Is("firmwaremanagement.cgi"))
        {
            return readMethod ? SafetyLevel.Read : jsonMethod == "commit" ? SafetyLevel.Write : SafetyLevel.Dangerous;
        }

        if (Array.Exists(DangerousUnlessRead, Is))
        {
            return readMethod ? SafetyLevel.Read : SafetyLevel.Dangerous;
        }

        if (Is("applications/control.cgi"))
        {
            return actions.Count > 0 && actions.TrueForAll(a => a is "start" or "stop" or "restart") ? SafetyLevel.Write : SafetyLevel.Dangerous;
        }

        if (Is("param.cgi"))
        {
            if (actions.Count > 0 && readActions)
            {
                return SafetyLevel.Read;
            }

            return pairs.Exists(p => !string.Equals(p.Key, "action", StringComparison.OrdinalIgnoreCase) && IsDangerousParameter(p.Key))
                ? SafetyLevel.Dangerous
                : SafetyLevel.Write;
        }

        if (Is("ptz.cgi") || Is("ptzconfig.cgi"))
        {
            // Only a query of the position, the limits or the presets reads.
            var reads = pairs.Exists(p => p.Key is "query" or "info") && pairs.TrueForAll(p => p.Key is "query" or "info" or "camera");
            return reads ? SafetyLevel.Read : SafetyLevel.Write;
        }

        if (Array.Exists(AlwaysWrite, Is))
        {
            return SafetyLevel.Write;
        }

        var file = path[(path.LastIndexOf('/') + 1)..];
        var dot = file.IndexOf('.', StringComparison.Ordinal);
        return ChangingFileNames.Contains(dot < 0 ? file : file[..dot]) ? SafetyLevel.Write : SafetyLevel.Read;
    }

    /// <summary>JSON methods that only read, as the library names them.</summary>
    private static bool IsReadMethod(string method) =>
        !IsOpen(method)
        && (method.StartsWith("get", StringComparison.Ordinal) || method.StartsWith("list", StringComparison.Ordinal)
            || method is "status" or "systemready");

    /// <summary>A SOAP request whose operation (the first element of the body) is a <c>Get*</c>.</summary>
    private static bool IsSoapRead(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return false;
        }

        try
        {
            var root = XDocument.Parse(xml).Root;
            var operation = root?.Name.LocalName == "Envelope"
                ? root.Elements().FirstOrDefault(e => e.Name.LocalName == "Body")?.Elements().FirstOrDefault()
                : null;
            return operation is not null && operation.Name.LocalName.StartsWith("Get", StringComparison.Ordinal);
        }
        catch (XmlException)
        {
            return false;
        }
    }

    /// <summary>Whether a param.cgi parameter can cut the device off; a name still to be filled in counts when it could become one.</summary>
    private static bool IsDangerousParameter(string key)
    {
        var name = key.ToLowerInvariant();
        if (name.StartsWith("root.", StringComparison.Ordinal))
        {
            name = name[5..];
        }

        var open = name.IndexOf("{{", StringComparison.Ordinal);
        var known = open < 0 ? name : name[..open];
        if (Array.Exists(HarmlessNetworkParameters, p => known.StartsWith(p, StringComparison.Ordinal)))
        {
            return false;
        }

        return Array.Exists(DangerousParameters, p => known.StartsWith(p, StringComparison.Ordinal) || open >= 0 && p.StartsWith(known, StringComparison.Ordinal));
    }

    private static string? StringOf(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static JsonNode? ParseJson(string body)
    {
        try
        {
            return JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Unescape(string text)
    {
        try
        {
            return Uri.UnescapeDataString(text);
        }
        catch (UriFormatException)
        {
            return text;
        }
    }
}
