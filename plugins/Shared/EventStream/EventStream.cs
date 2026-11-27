using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Sdk.Devices;

namespace Oadm.Plugins.EventStream;

/// <summary>One <c>events:notify</c> message of the WebSocket event stream.</summary>
public sealed record StreamNotification(string Topic, DateTimeOffset? Time, IReadOnlyDictionary<string, string> Source, IReadOnlyDictionary<string, string> Data);

/// <summary>What the device answered to a message: a notification, the reply to our filter, or something else.</summary>
public sealed record StreamMessage(StreamNotification? Notification, bool IsConfigureReply, string? Error)
{
    /// <summary>The <c>context</c> the device echoes from the request (null when the answer has none).</summary>
    public string? Context { get; init; }
}

/// <summary>
/// The JSON of the WebSocket event stream (verified on AXIS OS 12.11, 2026-10-10): one <c>events:configure</c> sets the
/// filter list (a new one replaces it; an event that matches two filters arrives twice, so a plugin sends one filter per
/// topic); the device answers <c>data: {}</c> or <c>error</c> (2104 "Could not use supplied event filter") and then
/// sends the current state of every matching on/off event, followed by <c>events:notify</c> per event. Shared by Device
/// triggers and License Plate Center (<c>plugins/Shared/EventStream</c>); Device triggers adds its trigger filters in its
/// own part of this class.
/// </summary>
public static partial class EventStreamJson
{
    /// <summary>
    /// The <c>events:configure</c> message with these filters (topic and an optional value filter, null = the whole topic;
    /// a new list replaces the device's). The device echoes <paramref name="context"/> in its answer, so an answer to an
    /// older list is told apart.
    /// </summary>
    public static string ConfigureTopics(IEnumerable<KeyValuePair<string, string?>> filters, string context = "oadm")
    {
        ArgumentNullException.ThrowIfNull(filters);
        var list = new JsonArray();
        foreach (var (topic, content) in filters)
        {
            var entry = new JsonObject { ["topicFilter"] = topic };
            if (content is not null)
            {
                entry["contentFilter"] = content;
            }

            list.Add(entry);
        }

        return new JsonObject
        {
            ["apiVersion"] = "1.0",
            ["context"] = context,
            ["method"] = "events:configure",
            ["params"] = new JsonObject { ["eventFilterList"] = list },
        }.ToJsonString();
    }

    public static StreamMessage Parse(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return new StreamMessage(null, false, null);
        }

        var method = root?["method"] is JsonValue m && m.TryGetValue<string>(out var name) ? name : null;
        if (method == "events:configure")
        {
            var error = root?["error"];
            var text = error is null ? null : error["message"]?.GetValue<string>() ?? "The device refused the event filter.";
            return new StreamMessage(null, true, text) { Context = root?["context"] is JsonValue c && c.TryGetValue<string>(out var context) ? context : null };
        }

        if (method != "events:notify" || root?["params"]?["notification"] is not JsonObject notification)
        {
            return new StreamMessage(null, false, null);
        }

        var topic = notification["topic"] is JsonValue t && t.TryGetValue<string>(out var topicText) ? topicText : string.Empty;
        DateTimeOffset? time = notification["timestamp"] is JsonValue stamp && stamp.TryGetValue<long>(out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
        var message = notification["message"] as JsonObject;
        return new StreamMessage(new StreamNotification(topic, time, Values(message?["source"]), Values(message?["data"])), false, null);
    }

    /// <summary>Whether a state value means on ("1", "true", "active", "yes", "on").</summary>
    public static bool IsOn(string? value) =>
        value is not null && (value.Trim().ToUpperInvariant() is "1" or "TRUE" or "ACTIVE" or "YES" or "ON");

    private static Dictionary<string, string> Values(JsonNode? node)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node is JsonObject obj)
        {
            foreach (var (name, value) in obj)
            {
                if (value is JsonValue v)
                {
                    values[name] = v.TryGetValue<string>(out var s) ? s : v.ToJsonString();
                }
            }
        }

        return values;
    }
}

/// <summary>
/// The parts of one permanent event connection every plugin with one shares: the stream path and its API, the keep-alive,
/// the waits between reconnects (1, 2, 4, 8, then 10 s), which devices are not contacted, the user text of a failed session
/// and the receive of one whole message.
/// </summary>
public static class EventStreamSocket
{
    /// <summary>The API the event stream needs (<c>apidiscovery.cgi</c>).</summary>
    public const string ApiId = "event-streaming-over-websocket";

    /// <summary>Its version.</summary>
    public const string ApiVersion = "1.0";

    /// <summary>The WebSocket of the events.</summary>
    public const string StreamPath = "/vapix/ws-data-stream?sources=events";

    /// <summary>The largest message read (a bigger one breaks the session).</summary>
    public const int MaxMessageBytes = 1024 * 1024;

    /// <summary>The WebSocket's ping interval and pong timeout (10 s).</summary>
    public static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(10);

    /// <summary>Waits between reconnects: 1, 2, 4, 8, then 10 s.</summary>
    public static IReadOnlyList<TimeSpan> Backoff { get; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(10)];

    /// <summary>The wait before reconnect number <paramref name="failures"/> (0 = the first).</summary>
    public static TimeSpan WaitAfter(int failures, IReadOnlyList<TimeSpan>? backoff = null)
    {
        var waits = backoff is { Count: > 0 } ? backoff : Backoff;
        return waits[Math.Clamp(failures, 0, waits.Count - 1)];
    }

    /// <summary>
    /// Devices not contacted: the refused statuses, and devices the status poll found not answering (in an outage no
    /// connection attempts; the device connects again once a poll sees it Ok).
    /// </summary>
    public static string? RefusalFor(DeviceStatus status) => DeviceMessages.Refusal(status, refuseUnreachable: true);

    /// <summary>Why <paramref name="device"/> is not contacted ("Site offline: Building B" while its site is offline).</summary>
    public static string? RefusalFor(IDeviceInfo device) => DeviceMessages.Refusal(device, refuseUnreachable: true);

    /// <summary>A short user text for a failed session.</summary>
    public static string Describe(Exception ex, TimeSpan timeout) => ex switch
    {
        UnauthorizedAccessException { Message: DeviceMessages.Forbidden } => DeviceMessages.Forbidden,
        UnauthorizedAccessException => DeviceMessages.Unauthorized,
        TaskCanceledException or TimeoutException => DeviceMessages.Timeout(timeout),
        HttpRequestException http => DeviceMessages.Unreachable(http.InnerException?.Message ?? http.Message),
        WebSocketException => "The connection to the device broke",
        _ => ex.Message,
    };

    /// <summary>One whole text message, null when the device closed the connection.</summary>
    public static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(socket);
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes)
            {
                throw new InvalidOperationException("The device sent a message larger than 1 MB.");
            }

            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            }
        }
    }
}
