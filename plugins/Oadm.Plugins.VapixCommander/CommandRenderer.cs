using System.Net.Http.Headers;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.VapixCommander;

/// <summary>
/// A command with its values filled in, ready to send. Kept in memory only; <see cref="Describe"/> masks
/// password values (<see cref="Secrets"/>) for display.
/// </summary>
public sealed class RenderedRequest
{
    public required string Method { get; init; }

    /// <summary>Path and query relative to the device base address, without a leading slash ("axis-cgi/param.cgi?action=list").</summary>
    public required string RelativeUri { get; init; }

    public required IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; }

    public string? Body { get; init; }

    /// <summary>Form body pairs (bodyType form); sent url-encoded.</summary>
    public IReadOnlyList<KeyValuePair<string, string>>? Form { get; init; }

    public string? ContentType { get; init; }

    public required TimeSpan Timeout { get; init; }

    /// <summary>The response rules with placeholders in extract paths and params filled in.</summary>
    public CommandResponse? Response { get; init; }

    /// <summary>Rendered password values, masked in <see cref="Describe"/>, never logged.</summary>
    public required IReadOnlyList<string> Secrets { get; init; }

    public HttpRequestMessage ToHttpRequest()
    {
        var request = new HttpRequestMessage(new HttpMethod(Method), new Uri(RelativeUri, UriKind.Relative));
        if (Form is not null)
        {
            request.Content = new FormUrlEncodedContent(Form);
        }
        else if (Body is not null)
        {
            request.Content = new StringContent(Body, Encoding.UTF8);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(ContentType ?? "text/plain; charset=utf-8");
        }

        foreach (var (name, value) in Headers)
        {
            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                if (request.Content is not null)
                {
                    request.Content.Headers.Remove("Content-Type");
                    request.Content.Headers.TryAddWithoutValidation("Content-Type", value);
                }

                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(name, value) && request.Content is not null)
            {
                request.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }

        request.Options.Set(VapixRequestOptions.Timeout, Timeout);
        return request;
    }

    /// <summary>"GET /axis-cgi/param.cgi?action=list" with password values replaced by ***.</summary>
    public string Describe() => Mask(Method + " /" + RelativeUri);

    /// <summary>The request body for display, password values masked; null without a body.</summary>
    public string? DescribeBody()
    {
        if (Form is not null)
        {
            return Mask(string.Join('&', Form.Select(p => p.Key + "=" + p.Value)));
        }

        return Body is null ? null : Mask(Body);
    }

    public string Mask(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (var secret in Secrets.Where(s => s.Length > 0).OrderByDescending(s => s.Length))
        {
            text = text.Replace(secret, "***", StringComparison.Ordinal)
                .Replace(Uri.EscapeDataString(secret), "***", StringComparison.Ordinal);
        }

        return text;
    }
}

/// <summary>
/// Builds the request of a command from checked values: placeholders in path, query, headers and body are
/// replaced (URL-escaped in the path and query, XML-escaped in XML bodies, typed in JSON bodies).
/// </summary>
public static class CommandRenderer
{
    /// <summary>Validates the definition and the values, then renders. Throws <see cref="CommandValidationException"/>; nothing is sent then.</summary>
    public static RenderedRequest Render(CommandDefinition command, IReadOnlyDictionary<string, JsonElement>? values)
    {
        CommandValidator.EnsureValid(command);
        var resolved = FieldValues.Resolve(command, values);
        return Render(command, resolved);
    }

    public static RenderedRequest Render(CommandDefinition command, IReadOnlyDictionary<string, FieldValue> values)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(values);
        var paramCgi = command.Response.Kind == ResponseKinds.ParamCgi;
        var request = command.Request;

        string Text(string? template, Func<string, string>? escape = null) =>
            Placeholders.Pattern().Replace(template ?? string.Empty, m =>
            {
                var text = values.TryGetValue(m.Groups[1].Value, out var v) ? v.Text(paramCgi) : string.Empty;
                return escape is null ? text : escape(text);
            });

        var renderedPath = Text(request.Path, Uri.EscapeDataString);
        if (!CommandValidator.IsDevicePath(renderedPath))
        {
            throw new CommandValidationException(CommandValidator.PathProblem); // every request goes to the device itself
        }

        var path = renderedPath.TrimStart('/');
        var query = new StringBuilder();
        foreach (var (key, value) in request.Query ?? [])
        {
            var renderedKey = Text(key);
            if (renderedKey.Length == 0)
            {
                continue;
            }

            query.Append(query.Length == 0 ? string.Empty : "&").Append(EscapeQuery(renderedKey)).Append('=').Append(EscapeQuery(Text(value)));
        }

        var relative = query.Length == 0 ? path : path + (path.Contains('?', StringComparison.Ordinal) ? "&" : "?") + query;

        var headers = new List<KeyValuePair<string, string>>();
        foreach (var (key, value) in request.Headers ?? [])
        {
            var renderedValue = Text(value);
            if (renderedValue.Any(c => c is '\r' or '\n'))
            {
                throw new CommandValidationException($"Header \"{key}\" must not contain line breaks.");
            }

            headers.Add(new(Text(key), renderedValue));
        }

        string? body = null;
        string? contentType = null;
        List<KeyValuePair<string, string>>? form = null;
        switch (request.EffectiveBodyType)
        {
            case BodyTypes.Json:
                body = RenderJson(request.Body, values, paramCgi)?.ToJsonString() ?? "null";
                contentType = "application/json; charset=utf-8";
                break;
            case BodyTypes.Form:
                form = [.. ((JsonObject)request.Body!).Select(p => new KeyValuePair<string, string>(Text(p.Key), Text(p.Value!.GetValue<string>())))];
                break;
            case BodyTypes.Text:
                body = Text(request.Body!.GetValue<string>());
                contentType = "text/plain; charset=utf-8";
                break;
            case BodyTypes.Xml:
                body = Text(request.Body!.GetValue<string>(), SecurityElement.Escape);
                contentType = body.Contains(":Envelope", StringComparison.Ordinal) || body.Contains("<Envelope", StringComparison.Ordinal)
                    ? "application/soap+xml; charset=utf-8"
                    : "application/xml; charset=utf-8";
                break;
        }

        var response = new CommandResponse
        {
            Kind = command.Response.Kind,
            Success = command.Response.Success,
            ErrorPattern = command.Response.ErrorPattern,
            Extract = command.Response.Extract?.Select(e => new ResponseExtract
            {
                Label = e.Label,
                Path = e.Path is null ? null : Text(e.Path),
                Param = e.Param is null ? null : Text(e.Param),
            }).ToList(),
        };

        return new RenderedRequest
        {
            Response = response,
            Method = request.Method,
            RelativeUri = relative,
            Headers = headers,
            Body = body,
            Form = form,
            ContentType = contentType,
            Timeout = TimeSpan.FromSeconds(Math.Clamp(request.EffectiveTimeoutSeconds, 1, CommandLimits.MaxTimeoutSeconds)),
            Secrets = [.. values.Values.Where(v => v.Field.IsSecret && v.Value is string { Length: > 0 }).Select(v => (string)v.Value!)],
        };
    }

    /// <summary>Query escaping that keeps "," and ":" readable (param.cgi group lists, times).</summary>
    public static string EscapeQuery(string text) =>
        Uri.EscapeDataString(text).Replace("%2C", ",", StringComparison.Ordinal).Replace("%3A", ":", StringComparison.Ordinal);

    private static JsonNode? RenderJson(JsonNode? node, IReadOnlyDictionary<string, FieldValue> values, bool paramCgi)
    {
        string Text(string template) => Placeholders.Pattern().Replace(template, m =>
            values.TryGetValue(m.Groups[1].Value, out var v) ? v.Text(paramCgi) : string.Empty);

        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
            {
                var copy = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    copy[Text(key)] = RenderJson(value, values, paramCgi);
                }

                return copy;
            }

            case JsonArray array:
            {
                var copy = new JsonArray();
                foreach (var item in array)
                {
                    copy.Add(RenderJson(item, values, paramCgi));
                }

                return copy;
            }

            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
            {
                var text = value.GetValue<string>();
                if (Placeholders.Whole(text) is { } name)
                {
                    return values.TryGetValue(name, out var typed) ? typed.Json(paramCgi) : null;
                }

                return JsonValue.Create(Text(text));
            }

            default:
                return node.DeepClone();
        }
    }
}
