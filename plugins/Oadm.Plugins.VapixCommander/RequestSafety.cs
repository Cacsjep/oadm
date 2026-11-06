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
/// A request is a read only when it is known to read, by every sign it carries: the method and the body (a GET
/// without a body, or a POST whose JSON method is a read (<c>get*</c>, <c>list*</c>, <c>status</c>,
/// <c>systemready</c>) or whose SOAP operation is one <c>Get*</c>), every <c>action</c> of the query and of a form or
/// text body, the path, and the names of the parameters. An unknown request counts as a change. The changing paths
/// cover every changing command of the bundled library (a test compares them) and the CGIs that restart, reset,
/// upgrade or change accounts. The path is judged as the device could read it (<see cref="Target"/>). What cannot be
/// told (a value still to be filled in) counts as the most restricted case of its path.
/// </remarks>
public static class RequestSafety
{
    /// <summary><c>action=</c> values that only read; every other action changes the device.</summary>
    private static readonly HashSet<string> ReadActions = new(StringComparer.Ordinal)
    {
        "list", "listdefinitions", "listall", "get", "getstatus", "status", "statusall", "query", "download",
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
        "opticssetup.cgi", "record/continuous/addconfiguration.cgi", "record/continuous/removeconfiguration.cgi", "disks/checkdisk.cgi",
    ];

    /// <summary>
    /// Starts of file names that say what they do ("geolocation/set.cgi", "virtualinput/activate.cgi",
    /// "disks/checkdisk.cgi", "removeconfiguration.cgi").
    /// </summary>
    private static readonly string[] ChangingNames =
    [
        "add", "modify", "remove", "delete", "set", "update", "create", "check", "activate", "deactivate", "start", "stop",
        "record", "mount", "unmount", "format", "clear", "upload", "import", "install", "uninstall", "restart", "reboot",
        "reset", "erase", "enable", "disable", "perform", "apply", "repair", "restore", "rollback", "commit", "configure",
        "rename", "move", "run", "send", "write", "save", "change", "trigger", "test", "play",
    ];

    /// <summary>Parameter names that only choose what is read, on every path.</summary>
    private static readonly HashSet<string> ReadNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "group", "listformat", "responseformat", "schemaversion", "camera", "resolution",
    };

    /// <summary>CGIs known to read, with the further parameter names they take; null: any name (the image parameters).</summary>
    private static readonly (string Cgi, string[]? Names)[] ReadCgis =
    [
        ("jpg/image.cgi", null), ("bitmap/image.cgi", null), ("mjpg/video.cgi", null),
        ("io/port.cgi", ["check", "checkactive", "monitor"]),
        ("privacymask.cgi", ["query"]),
        ("auditlog.cgi", ["tail"]),
        ("serverreport.cgi", ["mode"]),
        ("systemlog.cgi", ["appname"]),
        ("temperaturecontrol.cgi", ["device", "id"]),
    ];

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
        string? text = null;
        switch (request.EffectiveBodyType)
        {
            case BodyTypes.Json:
                json = request.Body;
                break;
            case BodyTypes.Xml:
                xml = StringOf(request.Body);
                break;
            case BodyTypes.Text:
                text = StringOf(request.Body);
                break;
            case BodyTypes.Form when request.Body is JsonObject form:
                pairs.AddRange(form.Select(p => new KeyValuePair<string, string>(p.Key, StringOf(p.Value) ?? Placeholder)));
                break;
        }

        var hasBody = request.EffectiveBodyType != BodyTypes.None || request.Body is not null;
        return Max(Flags(command), Classify(request.Method, request.Path ?? string.Empty, pairs, json, xml, text, hasBody));
    }

    /// <summary>The level of the request that is about to be sent: what the server enforces.</summary>
    public static SafetyLevel Of(CommandDefinition command, RenderedRequest rendered)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(rendered);
        JsonNode? json = null;
        string? xml = null;
        string? text = null;
        if (rendered.Body is { } body)
        {
            var contentType = rendered.ContentType ?? string.Empty;
            if (contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                json = ParseJson(body);
            }
            else if (contentType.Contains("xml", StringComparison.OrdinalIgnoreCase))
            {
                xml = body;
            }
            else
            {
                text = body;
            }
        }

        var hasBody = rendered.Body is not null || rendered.Form is not null || rendered.Parts is not null;
        return Max(Flags(command), Classify(rendered.Method, "/" + rendered.RelativeUri, [.. rendered.Form ?? []], json, xml, text, hasBody));
    }

    /// <summary>The level the command's own flags ask for.</summary>
    private static SafetyLevel Flags(CommandDefinition command) =>
        command.Dangerous ? SafetyLevel.Dangerous : command.Writes ? SafetyLevel.Write : SafetyLevel.Read;

    private static SafetyLevel Max(SafetyLevel a, SafetyLevel b) => a > b ? a : b;

    /// <summary>A value that is not known yet (a field of the command).</summary>
    private const string Placeholder = "{{}}";

    private static bool IsOpen(string text) => text.Contains("{{", StringComparison.Ordinal);

    private static bool IsAction(string name) => string.Equals(name, "action", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The path and the query of a request as the device could read them, for the rules. The path ends at "#" (never
    /// sent) and is decoded until nothing changes, then cut where a decoded "?", "#" or NUL would end it, with "\" as
    /// "/", in lower case, without empty and "." segments, ".." resolved, and each segment without its ";" parameters
    /// and its trailing dots and spaces. So "/axis-cgi/x/%2e%2e/Restart.CGI;a#b" is "/axis-cgi/restart.cgi".
    /// <see cref="CommandValidator.IsDevicePath"/> refuses most of these forms; the rules hold without it.
    /// </summary>
    internal static (string Path, string Query) Target(string pathAndQuery)
    {
        var hash = pathAndQuery.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            pathAndQuery = pathAndQuery[..hash];
        }

        var q = pathAndQuery.IndexOf('?', StringComparison.Ordinal);
        var path = q < 0 ? pathAndQuery : pathAndQuery[..q];
        var query = q < 0 ? string.Empty : pathAndQuery[(q + 1)..];
        for (var round = 0; round < 4; round++)
        {
            var decoded = Unescape(path);
            if (decoded == path)
            {
                break;
            }

            path = decoded;
        }

        var end = path.IndexOfAny(['?', '#', '\0']);
        if (end >= 0)
        {
            path = path[..end];
        }

        var segments = new List<string>();
        foreach (var part in path.Replace('\\', '/').ToLowerInvariant().Split('/'))
        {
            var semicolon = part.IndexOf(';', StringComparison.Ordinal);
            var segment = (semicolon < 0 ? part : part[..semicolon]).Trim();
            var name = segment.TrimEnd('.', ' ');
            if (name.Length > 0)
            {
                segments.Add(name);
            }
            else if (segment.Length >= 2 && segments.Count > 0)
            {
                segments.RemoveAt(segments.Count - 1); // ".."
            }
        }

        return ("/" + string.Join('/', segments), query);
    }

    /// <summary>The pairs of a query, or of a body written like one ("action=update&amp;Network.x=1").</summary>
    private static List<KeyValuePair<string, string>> PairsOf(string text)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var part in text.Split(['&', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            var name = Unescape((eq < 0 ? part : part[..eq]).Replace('+', ' ')).Trim();
            if (name.Length > 0)
            {
                pairs.Add(new(name, eq < 0 ? string.Empty : Unescape(part[(eq + 1)..].Replace('+', ' '))));
            }
        }

        return pairs;
    }

    private static SafetyLevel Classify(
        string method, string pathAndQuery, List<KeyValuePair<string, string>> pairs, JsonNode? json, string? xml, string? text, bool hasBody)
    {
        var (path, query) = Target(pathAndQuery);
        pairs.AddRange(PairsOf(query));
        var level = Level(method, path, pairs, json, xml, hasBody);

        // A text body may be read like a query (param.cgi). Its pairs can only add to the level.
        var loose = text ?? (xml is not null && !IsXml(xml) ? xml : null);
        var bodyPairs = loose is null ? [] : PairsOf(loose);
        return bodyPairs.Count == 0 ? level : Max(level, Level(method, path, [.. pairs, .. bodyPairs], json, xml, hasBody));
    }

    /// <summary>A read only when the method, the body, every action and the path all read.</summary>
    private static SafetyLevel Level(string method, string path, List<KeyValuePair<string, string>> pairs, JsonNode? json, string? xml, bool hasBody)
    {
        // A name still to be filled in could become "action".
        var actions = pairs.Where(p => IsOpen(p.Key) || IsAction(p.Key))
            .Select(p => IsOpen(p.Key) ? Placeholder : p.Value.ToLowerInvariant())
            .ToList();
        var readActions = actions.TrueForAll(ReadActions.Contains);
        var jsonMethod = MethodOf(json);
        var readMethod = jsonMethod is not null && IsReadMethod(jsonMethod);

        // A GET carries no body; a POST reads only with a body that is known to.
        var reads = string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) ? !hasBody
            : string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) && (readMethod || IsSoapRead(xml));
        var level = reads && readActions && !IsOpen(path) ? SafetyLevel.Read : SafetyLevel.Write;
        return Max(level, PathLevel(path, pairs, actions, readActions, jsonMethod, readMethod));
    }

    /// <summary>The level a path and its parameters ask for whatever the method: the list of changing paths.</summary>
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
            return readMethod ? Unknown() : jsonMethod == "commit" ? SafetyLevel.Write : SafetyLevel.Dangerous;
        }

        if (Array.Exists(DangerousUnlessRead, Is))
        {
            return readMethod ? Unknown() : SafetyLevel.Dangerous;
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

            return pairs.Exists(p => !IsAction(p.Key) && IsDangerousParameter(p.Key)) ? SafetyLevel.Dangerous : SafetyLevel.Write;
        }

        if (Is("ptz.cgi") || Is("ptzconfig.cgi"))
        {
            // Only a query of the position, the limits or the presets reads.
            var reads = pairs.Exists(p => p.Key is "query" or "info") && pairs.TrueForAll(p => p.Key is "query" or "info" or "camera");
            return reads ? SafetyLevel.Read : SafetyLevel.Write;
        }

        return Array.Exists(AlwaysWrite, Is) ? SafetyLevel.Write : Unknown();

        // A path without a rule of its own reads only when nothing says otherwise: its file is not named after a change
        // and every parameter is one that chooses what to read.
        SafetyLevel Unknown()
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var file = Array.Find(segments, s => s.EndsWith(".cgi", StringComparison.Ordinal)) ?? (segments.Length > 0 ? segments[^1] : string.Empty);
            if (Array.Exists(segments, s => (s == segments[^1] || s.EndsWith(".cgi", StringComparison.Ordinal)) && IsChangingName(s)))
            {
                return SafetyLevel.Write;
            }

            if (file.StartsWith("list", StringComparison.Ordinal) || file.StartsWith("get", StringComparison.Ordinal))
            {
                return SafetyLevel.Read; // "disks/list.cgi?diskid=all", "zipstream/getstatus.cgi?camera=1"
            }

            var known = Array.Find(ReadCgis, c => Is(c.Cgi));
            if (known.Cgi is not null && known.Names is null)
            {
                return SafetyLevel.Read;
            }

            return pairs.TrueForAll(p => IsAction(p.Key) || ReadNames.Contains(p.Key) || known.Names?.Contains(p.Key, StringComparer.OrdinalIgnoreCase) == true)
                ? SafetyLevel.Read
                : SafetyLevel.Write;
        }
    }

    private static bool IsChangingName(string file) => Array.Exists(ChangingNames, n => file.StartsWith(n, StringComparison.Ordinal));

    /// <summary>The <c>method</c> of a JSON request; null when there is none or the body names it twice.</summary>
    private static string? MethodOf(JsonNode? json)
    {
        try
        {
            return json is JsonObject obj && obj.TryGetPropertyValue("method", out var node) ? StringOf(node) : null;
        }
        catch (ArgumentException)
        {
            return null; // the same name twice: which one the device takes is not known
        }
    }

    /// <summary>JSON methods that only read, as the library names them.</summary>
    private static bool IsReadMethod(string method) =>
        !IsOpen(method)
        && (method.StartsWith("get", StringComparison.Ordinal) || method.StartsWith("list", StringComparison.Ordinal)
            || method is "status" or "systemready");

    /// <summary>A SOAP request whose one operation (the only element of the one body) is a <c>Get*</c>.</summary>
    private static bool IsSoapRead(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return false;
        }

        try
        {
            var root = XDocument.Parse(xml).Root;
            var bodies = root?.Name.LocalName == "Envelope" ? root.Elements().Where(e => e.Name.LocalName == "Body").ToList() : [];
            var operations = bodies.Count == 1 ? bodies[0].Elements().ToList() : [];
            return operations.Count == 1 && operations[0].Name.LocalName.StartsWith("Get", StringComparison.Ordinal);
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static bool IsXml(string text)
    {
        try
        {
            return XDocument.Parse(text).Root is not null;
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
