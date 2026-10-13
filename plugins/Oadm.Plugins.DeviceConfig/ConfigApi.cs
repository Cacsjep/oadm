using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.DeviceConfig;

/// <summary>The device answered with an error; the message is the user text.</summary>
public class ConfigDeviceException : Exception
{
    public ConfigDeviceException()
    {
    }

    public ConfigDeviceException(string message)
        : base(message)
    {
    }

    public ConfigDeviceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// What the device said about an import (AXIS OS 12.11.118, M3098-LV and Q3546-LVE): HTTP 200 <c>"success"</c> in every case
/// below, so the texts decide.
/// <list type="bullet">
/// <item>A value of the wrong type (schema): <c>warnings</c> "Failed to import time.v2: Trying to import 'time.v2' with invalid
/// data: "x" is not of type "boolean" (path: /timeZone/posix/dstEnabled)" and nothing of the area is imported
/// (<see cref="Refused"/>).</item>
/// <item>A parameter the device refuses: <c>warnings</c> "Failed to set property: root.Network.DNSUpdate.Enabled"; the other
/// values of the area are set (<see cref="Warnings"/>).</item>
/// <item>A property the device does not know: <c>information</c> "Field 'x' has been removed from the request"; the rest is
/// imported (<see cref="RemovedFields"/>).</item>
/// </list>
/// </summary>
public sealed record ImportAnswer(IReadOnlyList<string> Warnings, IReadOnlyList<string> Information)
{
    /// <summary>Why the device imported nothing of the area ("\"x\" is not of type \"boolean\" (path: ...)"), else null.</summary>
    public string? Refused { get; init; }

    /// <summary>Names of the properties the device does not know and left out.</summary>
    public IReadOnlyList<string> RemovedFields { get; init; } = [];
}

/// <summary>
/// The device configuration API (AXIS OS 12.5+): <c>GET /config/rest/$export</c>, <c>GET /config/discover</c> and
/// <c>PATCH /config/rest/$import</c>. Each import sends one settings area in the export's own format
/// (<c>{"data":{"time.v2":{...}}}</c>), always as a merge.
/// </summary>
public static class ConfigApi
{
    public const string ExportPath = "/config/rest/$export";
    public const string DiscoverPath = "/config/discover";
    public const string ImportPath = "/config/rest/$import?importType=merge";

    /// <summary>An import of the large parameter area can take a while on the device (8 s on a Q3546-LVE).</summary>
    public static readonly TimeSpan ImportTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a request is retried after a connection error. Right after a parameter import the device's web server resets
    /// connections for under a second (AXIS Q3546-LVE and M3098-LV, AXIS OS 12.11.118), so the next request can fail.
    /// </summary>
    public static readonly TimeSpan RetryFor = TimeSpan.FromSeconds(30);

    /// <summary>Wait between retries; tests set it to zero.</summary>
    public static TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The whole configuration, exactly as the device sends it.</summary>
    public static async Task<string> ExportAsync(IVapixClient vapix, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        var (status, body) = await SendAsync(vapix, () => new HttpRequestMessage(HttpMethod.Get, ExportPath), ct).ConfigureAwait(false);
        if (status == HttpStatusCode.NotFound)
        {
            throw new ConfigDeviceException(DeviceConfigIds.FeatureOff);
        }

        ThrowOnError(status, body);
        return body;
    }

    /// <summary>The settings areas the device has, as section keys ("param.v2").</summary>
    public static async Task<IReadOnlySet<string>> DiscoverAsync(IVapixClient vapix, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        var (status, body) = await SendAsync(vapix, () => new HttpRequestMessage(HttpMethod.Get, DiscoverPath), ct).ConfigureAwait(false);
        if (status == HttpStatusCode.NotFound)
        {
            throw new ConfigDeviceException(DeviceConfigIds.FeatureOff);
        }

        ThrowOnError(status, body);
        return ParseDiscover(body);
    }

    public static IReadOnlySet<string> ParseDiscover(string body)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (JsonNode.Parse(body) is JsonObject root && root["apis"] is JsonObject apis)
            {
                foreach (var (name, versions) in apis)
                {
                    if (versions is JsonObject byVersion)
                    {
                        foreach (var (version, _) in byVersion)
                        {
                            result.Add(name + "." + version);
                        }
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            throw new ConfigDeviceException("The device sent an unreadable list of settings areas.", ex);
        }

        return result;
    }

    /// <summary>Imports one settings area (merge). Throws <see cref="ConfigDeviceException"/> with the device's text.</summary>
    public static async Task<ImportAnswer> ImportAsync(IVapixClient vapix, string key, JsonNode data, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        ArgumentNullException.ThrowIfNull(data);
        var json = new JsonObject { ["data"] = new JsonObject { [key] = data.DeepClone() } }.ToJsonString();

        // A merge of the same data can be sent again safely.
        var (status, text) = await SendAsync(vapix, () =>
        {
            var request = new HttpRequestMessage(HttpMethod.Patch, ImportPath) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            request.Options.Set(VapixRequestOptions.Timeout, ImportTimeout);
            return request;
        }, ct).ConfigureAwait(false);
        ThrowOnError(status, text);
        return ParseAnswer(text);
    }

    /// <summary>Sends the request, retrying connection errors for <see cref="RetryFor"/>; returns status and body.</summary>
    private static async Task<(HttpStatusCode Status, string Body)> SendAsync(IVapixClient vapix, Func<HttpRequestMessage> create, CancellationToken ct)
    {
        var until = DateTime.UtcNow + RetryFor;
        while (true)
        {
            using var request = create();
            try
            {
                using var response = await vapix.SendAsync(request, ct).ConfigureAwait(false);
                return (response.StatusCode, await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            }
            catch (HttpRequestException ex) when (ex.StatusCode is null && DateTime.UtcNow < until && !ct.IsCancellationRequested)
            {
                await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
            }
        }
    }

    private const string FailedToImport = "Failed to import ";

    /// <summary>"Failed to import time.v2: Trying to import 'time.v2' with invalid data: X" -> "X".</summary>
    private static string RefusedText(string warning)
    {
        const string Marker = "with invalid data: ";
        var at = warning.IndexOf(Marker, StringComparison.Ordinal);
        if (at >= 0)
        {
            return warning[(at + Marker.Length)..].Trim();
        }

        var colon = warning.IndexOf(": ", StringComparison.Ordinal);
        return colon >= 0 ? warning[(colon + 2)..].Trim() : warning;
    }

    /// <summary>"Field 'unknownProp' has been removed from the request" -> "unknownProp".</summary>
    private static string? RemovedField(string information)
    {
        const string Start = "Field '";
        const string End = "' has been removed from the request";
        return information.StartsWith(Start, StringComparison.Ordinal) && information.EndsWith(End, StringComparison.Ordinal) && information.Length > Start.Length + End.Length
            ? information[Start.Length..^End.Length]
            : null;
    }

    public static ImportAnswer ParseAnswer(string body)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(body) && JsonNode.Parse(body) is JsonObject root)
            {
                var warnings = Strings(root["warnings"]);
                var information = Strings(root["information"]);
                var refused = warnings.FirstOrDefault(w => w.StartsWith(FailedToImport, StringComparison.Ordinal));
                return new ImportAnswer([.. warnings.Where(w => !ReferenceEquals(w, refused))], information)
                {
                    Refused = refused is null ? null : RefusedText(refused),
                    RemovedFields = [.. information.Select(RemovedField).OfType<string>()],
                };
            }
        }
        catch (JsonException)
        {
            // an empty or plain answer to a successful import: nothing to report
        }

        return new ImportAnswer([], []);
    }

    /// <summary>
    /// Throws for an error status or <c>"status":"error"</c>: the device's <c>error.message</c>, else the shared HTTP text.
    /// </summary>
    private static void ThrowOnError(HttpStatusCode statusCode, string body)
    {
        var deviceText = ErrorMessage(body, out var isError);
        var status = (int)statusCode;
        var success = status is >= 200 and <= 299;
        if (success && !isError)
        {
            return;
        }

        var http = success ? null : DeviceMessages.ForHttpStatus(status) ?? $"HTTP {status}";
        throw new ConfigDeviceException(deviceText is null ? http ?? "The device reported an error" : http is null ? deviceText : http + ": " + deviceText);
    }

    /// <summary><c>{"status":"error","error":{"code":n,"message":"..."}}</c> -> the message.</summary>
    internal static string? ErrorMessage(string body, out bool isError)
    {
        isError = false;
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(body) is not JsonObject root)
            {
                return null;
            }

            isError = root["status"] is JsonValue status && status.TryGetValue<string>(out var s) && string.Equals(s, "error", StringComparison.OrdinalIgnoreCase);
            var error = root["error"];
            var message = error is JsonObject obj ? obj["message"] : error;
            return message is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> Strings(JsonNode? node) =>
        node is JsonArray array
            ? [.. array.Select(item => item is JsonValue value && value.TryGetValue<string>(out var text) ? text : item?.ToJsonString())
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => text!)]
            : [];
}
