using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Oadm.Plugins.DateAndTime.Vapix;

/// <summary>One request to the device. Built by <see cref="TimePlanner"/>, sent by <see cref="TimeClient"/>.</summary>
public abstract record TimeRequest
{
    public abstract string Path { get; }

    public abstract string ContentType { get; }

    /// <summary>Exact request body; also what the tests compare.</summary>
    public abstract string Body { get; }

    /// <summary>Short name for errors ("setTimeZone", "param.cgi update").</summary>
    public abstract string Name { get; }
}

/// <summary>A time.cgi or ntp.cgi JSON method call (<c>{"apiVersion","context","method","params"}</c>).</summary>
public sealed record JsonMethodRequest(string Endpoint, string ApiVersion, string Method, string? ParamsJson) : TimeRequest
{
    public const string Context = "oadm";

    /// <summary>Compact, and POSIX zones ("&lt;UTC1&gt;-1...") sent as they are, not as < escapes.</summary>
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public override string Path => Endpoint;

    public override string ContentType => "application/json";

    public override string Name => Method;

    public override string Body
    {
        get
        {
            var body = new JsonObject
            {
                ["apiVersion"] = ApiVersion,
                ["context"] = Context,
                ["method"] = Method,
            };
            if (ParamsJson is not null)
            {
                body["params"] = JsonNode.Parse(ParamsJson);
            }

            return body.ToJsonString(Compact);
        }
    }

    public static JsonMethodRequest Time(string apiVersion, string method, JsonObject? parameters = null) =>
        new(TimeApis.TimePath, apiVersion, method, parameters?.ToJsonString(Compact));

    public static JsonMethodRequest Ntp(string apiVersion, string method, JsonObject? parameters = null) =>
        new(TimeApis.NtpPath, apiVersion, method, parameters?.ToJsonString(Compact));
}

/// <summary>A param.cgi action=update POST (form body, never in the URL).</summary>
public sealed record ParamUpdateRequest(IReadOnlyList<KeyValuePair<string, string>> Values) : TimeRequest
{
    public override string Path => TimeApis.ParamPath;

    public override string ContentType => "application/x-www-form-urlencoded";

    public override string Name => "param.cgi update";

    public override string Body
    {
        get
        {
            var sb = new StringBuilder("action=update");
            foreach (var (key, value) in Values)
            {
                sb.Append('&').Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
            }

            return sb.ToString();
        }
    }

    public static ParamUpdateRequest Of(params (string Key, string Value)[] values) =>
        new(values.Select(v => KeyValuePair.Create(v.Key, v.Value)).ToList());
}
