using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Oadm.Plugins.Network.Vapix;

/// <summary>One write request to the device. Built by <see cref="NetworkPlanner"/>, sent by <see cref="NetworkSettingsClient"/>.</summary>
public abstract record NetworkRequest
{
    public abstract string Path { get; }

    public abstract string ContentType { get; }

    /// <summary>Exact request body; also what the tests compare.</summary>
    public abstract string Body { get; }
}

/// <summary>A network_settings.cgi JSON method call.</summary>
public sealed record JsonMethodRequest(string ApiVersion, string Method, string ParamsJson) : NetworkRequest
{
    public const string Context = "oadm";

    public override string Path => NetworkApis.NetworkSettingsPath;

    public override string ContentType => "application/json";

    public override string Body
    {
        get
        {
            var body = new JsonObject
            {
                ["apiVersion"] = ApiVersion,
                ["context"] = Context,
                ["method"] = Method,
                ["params"] = JsonNode.Parse(ParamsJson),
            };
            return body.ToJsonString();
        }
    }

    public static JsonMethodRequest Create(string apiVersion, string method, JsonObject parameters) =>
        new(apiVersion, method, parameters.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
}

/// <summary>A param.cgi action=update POST (form body, never in the URL).</summary>
public sealed record ParamUpdateRequest(IReadOnlyList<KeyValuePair<string, string>> Values) : NetworkRequest
{
    public override string Path => NetworkApis.ParamPath;

    public override string ContentType => "application/x-www-form-urlencoded";

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
