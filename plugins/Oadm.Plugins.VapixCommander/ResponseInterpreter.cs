using System.Globalization;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Oadm.Plugins.VapixCommander;

/// <summary>A value shown after success (<c>response.extract</c>) or a parameter of a param.cgi list.</summary>
public sealed record NamedValue(string Name, string Value);

/// <summary>What a command did on one device: success with the extracted result, or the error text for the user.</summary>
public sealed record Interpretation(
    bool Success,
    string Summary,
    IReadOnlyList<NamedValue> Values,
    IReadOnlyList<NamedValue> Parameters)
{
    public static Interpretation Fail(string error) => new(false, error, [], []);
}

/// <summary>
/// Interprets a device response per <c>response.kind</c> (command-format.md, "Response interpretation") and maps
/// HTTP statuses to readable texts with the device's own error text when it has one.
/// </summary>
public static partial class ResponseInterpreter
{
    public const int ExcerptLength = 300;

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    [GeneratedRegex("<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex HtmlTitle();

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex HtmlTag();

    public static Interpretation Interpret(CommandResponse spec, int status, string? reason, string? contentType, string body)
    {
        ArgumentNullException.ThrowIfNull(spec);
        body ??= string.Empty;
        if (status is < 200 or > 299)
        {
            var statusText = StatusText(status, reason);
            var deviceText = DeviceErrorText(spec.Kind, contentType, body);
            return Interpretation.Fail(deviceText is null ? statusText : statusText + ": " + deviceText);
        }

        if (spec.ErrorPattern is { Length: > 0 } errorPattern && MatchError(errorPattern, body) is { } matched)
        {
            return Interpretation.Fail(matched);
        }

        return spec.Kind switch
        {
            ResponseKinds.ParamCgi => ParamCgi(spec, body),
            ResponseKinds.JsonRpc => Json(spec, body, rest: false),
            ResponseKinds.Rest => Json(spec, body, rest: true),
            ResponseKinds.Text => Text(spec, body),
            ResponseKinds.Xml => Xml(spec, body),
            _ => Raw(spec, body, status),
        };
    }

    /// <summary>True for responses that are not text (JPEG snapshots, downloads): shown as content type and size.</summary>
    public static bool IsBinary(string? contentType, ReadOnlySpan<byte> body)
    {
        var type = contentType?.Split(';')[0].Trim().ToLowerInvariant() ?? string.Empty;
        if (type.StartsWith("image/", StringComparison.Ordinal) || type.StartsWith("video/", StringComparison.Ordinal)
            || type.StartsWith("audio/", StringComparison.Ordinal)
            || type is "application/octet-stream" or "application/zip" or "application/x-gzip" or "application/gzip" or "application/pdf")
        {
            return true;
        }

        var probe = body[..Math.Min(body.Length, 1024)];
        return type.Length == 0 && probe.IndexOf((byte)0) >= 0;
    }

    /// <summary>"image/jpeg, 123.4 KB".</summary>
    public static string DescribeBinary(string? contentType, long length)
    {
        var size = length < 1024
            ? string.Create(CultureInfo.InvariantCulture, $"{length} bytes")
            : length < 1024 * 1024
                ? string.Create(CultureInfo.InvariantCulture, $"{length / 1024.0:0.0} KB")
                : string.Create(CultureInfo.InvariantCulture, $"{length / 1024.0 / 1024.0:0.0} MB");
        var type = string.IsNullOrWhiteSpace(contentType) ? "binary data" : contentType.Split(';')[0].Trim();
        return type + ", " + size;
    }

    /// <summary>Readable text of a non-success HTTP status.</summary>
    public static string StatusText(int status, string? reason) => status switch
    {
        400 => "Bad Request - HTTP 400",
        401 => "Unauthorized - HTTP 401 (check credentials)",
        403 => "Forbidden - HTTP 403 (user lacks permission)",
        404 => "Not Found - HTTP 404 (API not available on this firmware)",
        405 => "Method Not Allowed - HTTP 405",
        >= 500 and <= 599 => string.Create(CultureInfo.InvariantCulture, $"Server error - HTTP {status}"),
        _ => string.IsNullOrWhiteSpace(reason)
            ? string.Create(CultureInfo.InvariantCulture, $"HTTP {status}")
            : string.Create(CultureInfo.InvariantCulture, $"{reason} - HTTP {status}"),
    };

    /// <summary>The device's own error text in a response body, or null (HTML pages without a title, empty bodies).</summary>
    public static string? DeviceErrorText(string kind, string? contentType, string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var trimmed = body.Trim();
        if (ParamError(trimmed) is { } paramError)
        {
            return paramError;
        }

        if (trimmed[0] is '{' or '[' && TryParseJson(trimmed) is { } doc)
        {
            using (doc)
            {
                return JsonError(doc.RootElement) ?? (kind is ResponseKinds.JsonRpc or ResponseKinds.Rest ? null : Excerpt(trimmed));
            }
        }

        if (trimmed[0] == '<')
        {
            if (contentType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true || trimmed.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
            {
                var title = HtmlTitle().Match(trimmed);
                var text = title.Success ? HtmlTag().Replace(title.Groups[1].Value, string.Empty).Trim() : null;
                return string.IsNullOrEmpty(text) ? null : Excerpt(text);
            }

            return TryParseXml(trimmed) is { } xml ? XmlError(xml) : null;
        }

        return Excerpt(FirstLine(trimmed));
    }

    private static Interpretation ParamCgi(CommandResponse spec, string body)
    {
        var trimmed = body.Trim();
        if (ParamError(trimmed) is { } error)
        {
            return Interpretation.Fail(error);
        }

        if (spec.Success is { Length: > 0 } success && !trimmed.Contains(success, StringComparison.Ordinal))
        {
            return Interpretation.Fail("Unexpected response: " + Excerpt(FirstLine(trimmed)));
        }

        var parameters = ParseParameters(trimmed);
        var values = new List<NamedValue>();
        foreach (var extract in spec.Extract ?? [])
        {
            if (extract.Param is { Length: > 0 } name)
            {
                var found = parameters.FirstOrDefault(p => ParamMatches(p.Name, name));
                values.Add(new NamedValue(extract.Label, found?.Value ?? "(not found)"));
            }
        }

        string summary;
        if (values.Count > 0)
        {
            summary = Join(values);
        }
        else if (parameters.Count is > 0 and <= 3)
        {
            summary = string.Join(", ", parameters.Select(p => p.Name + "=" + p.Value));
        }
        else if (parameters.Count > 3)
        {
            summary = string.Create(CultureInfo.InvariantCulture, $"{parameters.Count} parameters");
        }
        else
        {
            summary = trimmed.Length == 0 ? "OK" : Excerpt(FirstLine(trimmed));
        }

        return new Interpretation(true, summary, values, parameters);
    }

    private static Interpretation Json(CommandResponse spec, string body, bool rest)
    {
        var trimmed = body.Trim();
        if (trimmed.Length == 0)
        {
            // HTTP 204 / empty answers of set methods are a success.
            return new Interpretation(true, "OK", [], []);
        }

        var parsed = TryParseJson(trimmed);
        if (parsed is null)
        {
            return Interpretation.Fail("Invalid JSON response: " + Excerpt(trimmed));
        }

        using var doc = parsed;
        var root = doc.RootElement;
        if (JsonError(root) is { } error)
        {
            return Interpretation.Fail(error);
        }

        if (spec.Success is { Length: > 0 } success && !trimmed.Contains(success, StringComparison.Ordinal))
        {
            return Interpretation.Fail("Unexpected response: " + Excerpt(trimmed));
        }

        var values = new List<NamedValue>();
        foreach (var extract in spec.Extract ?? [])
        {
            if (extract.Path is { Length: > 0 } path)
            {
                values.Add(new NamedValue(extract.Label, JsonPath(root, path) ?? "(not found)"));
            }
        }

        return new Interpretation(true, values.Count > 0 ? Join(values) : "OK", values, []);
    }

    private static Interpretation Text(CommandResponse spec, string body)
    {
        var trimmed = body.Trim();
        if (spec.Success is { Length: > 0 } success && !trimmed.Contains(success, StringComparison.Ordinal))
        {
            return Interpretation.Fail(trimmed.Length == 0 ? "Empty response." : Excerpt(FirstLine(trimmed)));
        }

        return new Interpretation(true, trimmed.Length == 0 ? "OK" : Excerpt(FirstLine(trimmed)), [], []);
    }

    private static Interpretation Xml(CommandResponse spec, string body)
    {
        var trimmed = body.Trim();
        if (trimmed.Length == 0)
        {
            return new Interpretation(true, "OK", [], []);
        }

        var doc = TryParseXml(trimmed);
        if (doc is null)
        {
            return Interpretation.Fail("Invalid XML response: " + Excerpt(trimmed));
        }

        if (XmlError(doc) is { } error)
        {
            return Interpretation.Fail(error);
        }

        if (spec.Success is { Length: > 0 } success && !trimmed.Contains(success, StringComparison.Ordinal))
        {
            return Interpretation.Fail("Unexpected response: " + Excerpt(trimmed));
        }

        var values = new List<NamedValue>();
        foreach (var extract in spec.Extract ?? [])
        {
            if (extract.Path is { Length: > 0 } path)
            {
                values.Add(new NamedValue(extract.Label, XmlPath(doc, path) ?? "(not found)"));
            }
        }

        return new Interpretation(true, values.Count > 0 ? Join(values) : "OK", values, []);
    }

    private static Interpretation Raw(CommandResponse spec, string body, int status)
    {
        var trimmed = body.Trim();
        if (spec.Success is { Length: > 0 } success && !trimmed.Contains(success, StringComparison.Ordinal))
        {
            return Interpretation.Fail("Unexpected response: " + Excerpt(trimmed));
        }

        return new Interpretation(true, trimmed.Length == 0 ? string.Create(CultureInfo.InvariantCulture, $"OK (HTTP {status})") : Excerpt(trimmed), [], []);
    }

    /// <summary>The line matching <c>response.errorPattern</c> (or the match itself), else null.</summary>
    private static string? MatchError(string pattern, string body)
    {
        try
        {
            var match = Regex.Match(body, pattern, RegexOptions.CultureInvariant | RegexOptions.Multiline, TimeSpan.FromSeconds(1));
            if (!match.Success)
            {
                return null;
            }

            var lineStart = match.Index == 0 ? 0 : body.LastIndexOf('\n', match.Index - 1) + 1;
            var lineEnd = body.IndexOf('\n', match.Index);
            var line = body[lineStart..(lineEnd < 0 ? body.Length : lineEnd)].Trim().TrimStart('#', ' ');
            return Excerpt(line.Length > 0 ? line : match.Value);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>"Error: ..." of a param.cgi answer ("# Error: ..." or a line containing "Error:"), else null.</summary>
    private static string? ParamError(string body)
    {
        if (body.StartsWith("# Error", StringComparison.OrdinalIgnoreCase))
        {
            return Excerpt(FirstLine(body).TrimStart('#', ' '));
        }

        if (body.Length > 0 && body[0] is '{' or '<')
        {
            return null;
        }

        var line = body.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("Error:", StringComparison.OrdinalIgnoreCase) && !l.Contains('=', StringComparison.Ordinal));
        return line is null ? null : Excerpt(line.TrimStart('#', ' '));
    }

    private static List<NamedValue> ParseParameters(string body)
    {
        var result = new List<NamedValue>();
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && !line.StartsWith('#'))
            {
                result.Add(new NamedValue(line[..eq].Trim(), line[(eq + 1)..].Trim()));
            }
        }

        return result;
    }

    private static bool ParamMatches(string name, string wanted)
    {
        static string Strip(string n)
        {
            n = n.Trim().Trim('"', '\'').Trim();
            return n.StartsWith("root.", StringComparison.OrdinalIgnoreCase) ? n[5..] : n;
        }

        return string.Equals(Strip(name), Strip(wanted), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"message (code N)" of an Axis JSON API error, a REST error or a problem+json document.</summary>
    private static string? JsonError(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.Object)
            {
                var message = Scalar(error, "message") ?? Scalar(error, "description") ?? "Unknown error";
                var code = Scalar(error, "code");
                return Excerpt(code is null ? message : $"{message} (code {code})");
            }

            if (error.ValueKind == JsonValueKind.String)
            {
                return Excerpt(error.GetString()!);
            }
        }

        if (Scalar(root, "status") is { } status && string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
        {
            return Excerpt(Scalar(root, "message") ?? "The device reported an error.");
        }

        var title = Scalar(root, "title");
        var detail = Scalar(root, "detail");
        if (title is not null && (detail is not null || root.TryGetProperty("type", out _) || root.TryGetProperty("status", out _)))
        {
            return Excerpt(detail is null ? title : $"{title}: {detail}");
        }

        return null;
    }

    /// <summary>SOAP fault text or Axis &lt;GeneralError&gt; description, else null.</summary>
    private static string? XmlError(XDocument doc)
    {
        var fault = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Fault");
        if (fault is not null)
        {
            var text = fault.Descendants().FirstOrDefault(e => e.Name.LocalName is "Text" or "faultstring" && !string.IsNullOrWhiteSpace(e.Value))?.Value
                ?? fault.Descendants().FirstOrDefault(e => e.Name.LocalName is "Reason" or "Value" or "faultcode")?.Value;
            return Excerpt(string.IsNullOrWhiteSpace(text) ? "SOAP fault" : text.Trim());
        }

        var general = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "GeneralError");
        if (general is not null)
        {
            var description = general.Elements().FirstOrDefault(e => e.Name.LocalName == "ErrorDescription")?.Value?.Trim();
            var code = general.Elements().FirstOrDefault(e => e.Name.LocalName == "ErrorCode")?.Value?.Trim();
            var message = string.IsNullOrEmpty(description) ? "The device reported an error" : description;
            return Excerpt(string.IsNullOrEmpty(code) ? message : $"{message} (code {code})");
        }

        return null;
    }

    /// <summary>Value at a dotted path ("data.propertyList.ProdNbr", "data.items[0].id" or "data.items.0.id").</summary>
    public static string? JsonPath(JsonElement root, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var current = root;
        foreach (var segment in path.Replace("[", ".", StringComparison.Ordinal).Replace("]", string.Empty, StringComparison.Ordinal).Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(segment, out var next))
            {
                current = next;
            }
            else if (current.ValueKind == JsonValueKind.Array && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < current.GetArrayLength())
            {
                current = current[index];
            }
            else
            {
                return null;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Object or JsonValueKind.Array => Excerpt(current.GetRawText()),
            _ => FieldValues.TextOf(current),
        };
    }

    /// <summary>Value at a dotted path of element local names from the root ("Envelope.Body.X.Y"); "@name" reads an attribute.</summary>
    public static string? XmlPath(XDocument doc, string path)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(path);
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        XElement? current = doc.Root;
        var start = current is not null && segments.Length > 0 && segments[0] == current.Name.LocalName ? 1 : 0;
        for (var i = start; i < segments.Length && current is not null; i++)
        {
            if (segments[i].StartsWith('@'))
            {
                return current.Attributes().FirstOrDefault(a => a.Name.LocalName == segments[i][1..])?.Value;
            }

            current = current.Elements().FirstOrDefault(e => e.Name.LocalName == segments[i]);
        }

        return current?.Value;
    }

    /// <summary>Body for display: JSON and XML indented, anything else as is.</summary>
    public static string Pretty(string body, string? contentType)
    {
        ArgumentNullException.ThrowIfNull(body);
        var trimmed = body.Trim();
        if (trimmed.Length > 0 && trimmed[0] is '{' or '[' && TryParseJson(trimmed) is { } json)
        {
            using (json)
            {
                return JsonSerializer.Serialize(json.RootElement, IndentedJson);
            }
        }

        if (trimmed.Length > 0 && trimmed[0] == '<' && contentType?.Contains("html", StringComparison.OrdinalIgnoreCase) != true && TryParseXml(trimmed) is { } xml)
        {
            return xml.Declaration is null ? xml.ToString() : xml.Declaration + Environment.NewLine + xml;
        }

        return body;
    }

    public static string Excerpt(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var single = text.ReplaceLineEndings(" ").Trim();
        return single.Length <= ExcerptLength ? single : single[..ExcerptLength] + "...";
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }

    private static string Join(IEnumerable<NamedValue> values) => string.Join(" · ", values.Select(v => $"{v.Name}: {v.Value}"));

    private static string? Scalar(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null)
            ? FieldValues.TextOf(value)
            : null;

    private static JsonDocument? TryParseJson(string text)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static XDocument? TryParseXml(string text)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
    }
}

/// <summary>Maps transport failures (no HTTP answer) to the texts of command-format.md.</summary>
public static class TransportErrors
{
    public static string Describe(Exception ex, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var seconds = string.Create(CultureInfo.InvariantCulture, $"{timeout.TotalSeconds:0}");
        switch (ex)
        {
            case OperationCanceledException:
            case TimeoutException:
                return $"Timeout after {seconds} s";
        }

        if (ex.GetType().Name == "CertificateChangedException")
        {
            return "TLS/certificate error: the device certificate changed. Accept the new certificate on the Devices page first.";
        }

        if (ex.GetType().Name == "VapixAuthenticationException")
        {
            return "Unauthorized - HTTP 401 (check credentials)";
        }

        if (ex is AuthenticationException auth)
        {
            return "TLS/certificate error: " + Innermost(auth).Message;
        }

        if (ex is HttpRequestException http)
        {
            var socket = FindInner<SocketException>(http);
            switch (http.HttpRequestError)
            {
                case HttpRequestError.NameResolutionError:
                    return "Host not found (name resolution failed)";
                case HttpRequestError.SecureConnectionError:
                    return "TLS/certificate error: " + Innermost(http).Message;
                case HttpRequestError.ConnectionError when socket is not null:
                    return Socket(socket);
                case HttpRequestError.ResponseEnded:
                    return "Connection closed by the device before it answered";
            }

            if (FindInner<AuthenticationException>(http) is { } inner)
            {
                return "TLS/certificate error: " + Innermost(inner).Message;
            }

            return socket is not null ? Socket(socket) : "Connection failed: " + Innermost(http).Message;
        }

        if (ex is SocketException se)
        {
            return Socket(se);
        }

        if (ex is IOException io)
        {
            return "Connection lost: " + Innermost(io).Message;
        }

        return ex.Message;
    }

    private static string Socket(SocketException ex) => ex.SocketErrorCode switch
    {
        SocketError.ConnectionRefused => "Connection refused",
        SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.HostDown or SocketError.NetworkDown => "Host unreachable",
        SocketError.TimedOut => "Host unreachable (connection timed out)",
        SocketError.HostNotFound or SocketError.NoData => "Host not found (name resolution failed)",
        SocketError.ConnectionReset => "Connection reset by the device",
        _ => "Connection failed: " + ex.Message,
    };

    private static T? FindInner<T>(Exception ex)
        where T : Exception
    {
        for (var current = ex.InnerException; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static Exception Innermost(Exception ex)
    {
        var current = ex;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return current;
    }
}
