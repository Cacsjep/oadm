using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Oadm.Plugins.VapixCommander.Library.Tests;

/// <summary>A request with every placeholder replaced, ready to send.</summary>
public sealed record RenderedRequest(
    string Method,
    string PathAndQuery,
    IReadOnlyDictionary<string, string> Headers,
    string BodyType,
    string? Body,
    int TimeoutSeconds);

/// <summary>
/// Minimal, test-only implementation of the command format rules (command-format.md): placeholder
/// rendering and response interpretation. The real engine lives in the commander plugin; this copy
/// exists so the library can be proven on hardware without referencing it.
/// </summary>
public static class CommandRendering
{
    /// <summary>Default value of every field (password fields must be supplied by the caller).</summary>
    public static Dictionary<string, JsonNode?> Defaults(JsonObject command) =>
        CommandLibrary.Fields(command).ToDictionary(f => f["name"]!.GetValue<string>(), f => f["default"]?.DeepClone());

    public static RenderedRequest Render(JsonObject command, IReadOnlyDictionary<string, JsonNode?> values)
    {
        var request = CommandLibrary.Request(command);
        var kind = CommandLibrary.Kind(command);
        var fields = CommandLibrary.Fields(command).ToDictionary(f => f["name"]!.GetValue<string>());

        string Text(string template) => CommandLibrary.PlaceholderRegex().Replace(template, m =>
        {
            var name = m.Groups[1].Value;
            return FieldText(fields[name], values[name], kind);
        });

        var path = Text(request["path"]!.GetValue<string>());
        var query = request["query"]?.AsObject() ?? [];
        if (query.Count > 0)
        {
            var parts = query.Select(kv =>
                Uri.EscapeDataString(Text(kv.Key)) + "=" + Uri.EscapeDataString(Text(kv.Value!.GetValue<string>())));
            path += "?" + string.Join("&", parts);
        }

        var headers = (request["headers"]?.AsObject() ?? []).ToDictionary(kv => kv.Key, kv => Text(kv.Value!.GetValue<string>()));
        var bodyType = request["bodyType"]?.GetValue<string>() ?? "none";
        var bodyNode = request["body"];
        string? body = bodyType switch
        {
            "none" => null,
            "json" => RenderJson(bodyNode, fields, values, kind)!.ToJsonString(),
            "form" => string.Join("&", bodyNode!.AsObject().Select(kv =>
                Uri.EscapeDataString(Text(kv.Key)) + "=" + Uri.EscapeDataString(Text(kv.Value!.GetValue<string>())))),
            _ => Text(bodyNode!.GetValue<string>()),
        };

        return new RenderedRequest(
            request["method"]!.GetValue<string>(),
            path,
            headers,
            bodyType,
            body,
            request["timeoutSeconds"]?.GetValue<int>() ?? 15);
    }

    /// <summary>Text form of a field value inside a string (query, path, header, text body).</summary>
    public static string FieldText(JsonObject field, JsonNode? value, string kind)
    {
        if (value is null)
        {
            return string.Empty;
        }

        if (field["type"]!.GetValue<string>() == "boolean")
        {
            var on = value.GetValue<bool>();
            var custom = field[on ? "trueValue" : "falseValue"];
            if (custom is not null)
            {
                return custom.GetValue<string>();
            }

            return kind == "param-cgi" ? (on ? "yes" : "no") : (on ? "true" : "false");
        }

        return value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number => value.GetValue<double>().ToString(CultureInfo.InvariantCulture),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.ToJsonString(),
        };
    }

    private static JsonNode? RenderJson(JsonNode? node, Dictionary<string, JsonObject> fields,
        IReadOnlyDictionary<string, JsonNode?> values, string kind)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
            {
                var copy = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    copy[RenderString(key, fields, values, kind)] = RenderJson(value, fields, values, kind);
                }

                return copy;
            }

            case JsonArray array:
                return new JsonArray(array.Select(i => RenderJson(i, fields, values, kind)).ToArray());
            case JsonValue v when v.TryGetValue<string>(out var text):
            {
                var exact = CommandLibrary.PlaceholderRegex().Match(text);
                if (exact.Success && exact.Value == text)
                {
                    var field = fields[exact.Groups[1].Value];
                    var value = values[exact.Groups[1].Value];
                    var type = field["type"]!.GetValue<string>();
                    if (type == "boolean" && field["trueValue"] is null)
                    {
                        return JsonValue.Create(value!.GetValue<bool>());
                    }

                    if (type is "integer" or "number" or "enum" && value is not null)
                    {
                        return value.DeepClone();
                    }
                }

                return JsonValue.Create(RenderString(text, fields, values, kind));
            }

            default:
                return node.DeepClone();
        }
    }

    private static string RenderString(string template, Dictionary<string, JsonObject> fields,
        IReadOnlyDictionary<string, JsonNode?> values, string kind) =>
        CommandLibrary.PlaceholderRegex().Replace(template, m => FieldText(fields[m.Groups[1].Value], values[m.Groups[1].Value], kind));

    /// <summary>Applies the response.kind success rule. Returns null on success, else the error text.</summary>
    public static string? Interpret(JsonObject command, int status, string body)
    {
        if (status is < 200 or > 299)
        {
            return $"HTTP {status}: {Excerpt(body)}";
        }

        var response = CommandLibrary.Response(command);
        var success = response["success"]?.GetValue<string>();
        switch (CommandLibrary.Kind(command))
        {
            case "param-cgi":
                if (body.TrimStart().StartsWith("# Error", StringComparison.Ordinal) || body.Contains("Error:", StringComparison.Ordinal))
                {
                    return Excerpt(body);
                }

                break;
            case "json-rpc":
            {
                JsonNode? json;
                try
                {
                    json = JsonNode.Parse(body);
                }
                catch (JsonException e)
                {
                    return "Invalid JSON: " + e.Message;
                }

                if (json is JsonObject o && o["error"] is JsonObject error)
                {
                    return $"{error["message"]} (code {error["code"]})";
                }

                break;
            }

            case "rest":
                if (JsonNode.Parse(body) is JsonObject r && r["status"]?.GetValue<string>() == "error")
                {
                    return r["error"]?["message"]?.ToString() ?? Excerpt(body);
                }

                break;
            case "xml":
                if (body.Contains("Fault>", StringComparison.Ordinal) || body.Contains("<GeneralError", StringComparison.Ordinal))
                {
                    return Excerpt(body);
                }

                break;
        }

        if (success is not null && CommandLibrary.Kind(command) is "text" or "param-cgi" && !body.Contains(success, StringComparison.Ordinal))
        {
            return $"Expected '{success}' in: {Excerpt(body)}";
        }

        return null;
    }

    /// <summary>Resolves one response.extract entry; null when the value is missing.</summary>
    public static string? Extract(JsonObject extract, string body, JsonObject command, IReadOnlyDictionary<string, JsonNode?> values)
    {
        var fields = CommandLibrary.Fields(command).ToDictionary(f => f["name"]!.GetValue<string>());
        var kind = CommandLibrary.Kind(command);
        string Text(string template) => CommandLibrary.PlaceholderRegex().Replace(template,
            m => FieldText(fields[m.Groups[1].Value], values[m.Groups[1].Value], kind));

        if (extract["param"] is { } param)
        {
            var key = Text(param.GetValue<string>());
            return ParseKeyValues(body).TryGetValue(key, out var v) ? v : null;
        }

        var path = Text(extract["path"]!.GetValue<string>());
        JsonNode? node = JsonNode.Parse(body);
        foreach (var segment in path.Split('.'))
        {
            node = node switch
            {
                JsonObject o => o[segment],
                JsonArray a when int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < a.Count => a[i],
                _ => null,
            };
            if (node is null)
            {
                return null;
            }
        }

        return node is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : node!.ToJsonString();
    }

    /// <summary>key=value lines (param.cgi style); the "root." prefix and surrounding quotes are removed.</summary>
    public static Dictionary<string, string> ParseKeyValues(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var key = line[..eq].Trim();
            if (key.StartsWith("root.", StringComparison.Ordinal))
            {
                key = key["root.".Length..];
            }

            result[key] = line[(eq + 1)..].Trim().Trim('"');
        }

        return result;
    }

    public static string Excerpt(string body)
    {
        var flat = new StringBuilder(body.Length);
        foreach (var c in body)
        {
            flat.Append(char.IsControl(c) ? ' ' : c);
        }

        var text = flat.ToString().Trim();
        return text.Length <= 300 ? text : text[..300];
    }
}
