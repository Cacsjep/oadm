using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Oadm.Plugins.DeviceTriggers.Device;

/// <summary>One <c>events:notify</c> message of the WebSocket event stream.</summary>
public sealed record StreamNotification(string Topic, DateTimeOffset? Time, IReadOnlyDictionary<string, string> Source, IReadOnlyDictionary<string, string> Data);

/// <summary>What the device answered to a message: a notification, the reply to our filter, or something else.</summary>
public sealed record StreamMessage(StreamNotification? Notification, bool IsConfigureReply, string? Error);

/// <summary>
/// The JSON of the WebSocket event stream (verified on AXIS OS 12.11, 2026-10-10): one <c>events:configure</c> sets the
/// filter list (a new one replaces it; an event that matches two filters arrives twice, so OADM sends one filter per
/// topic); the device answers <c>data: {}</c> or <c>error</c> (2104 "Could not use supplied event filter") and then
/// sends the current state of every matching on/off event, followed by <c>events:notify</c> per event.
/// </summary>
public static class EventStreamJson
{
    /// <summary>The filter list for the triggers of one device: one entry per topic, their value filters joined with "or".</summary>
    public static string Configure(IEnumerable<TriggerDefinition> triggers)
    {
        ArgumentNullException.ThrowIfNull(triggers);
        var filters = new JsonArray();
        foreach (var topic in triggers.GroupBy(t => t.Topic, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var entry = new JsonObject { ["topicFilter"] = topic.Key };
            if (topic.All(t => t.Filters.Count > 0))
            {
                var parts = topic.Select(t => ContentFilter(t.Filters)).Distinct(StringComparer.Ordinal).ToList();
                entry["contentFilter"] = parts.Count == 1 ? parts[0] : string.Join(" or ", parts.Select(p => "(" + p + ")"));
            }

            filters.Add(entry);
        }

        return new JsonObject
        {
            ["apiVersion"] = "1.0",
            ["context"] = "oadm",
            ["method"] = "events:configure",
            ["params"] = new JsonObject { ["eventFilterList"] = filters },
        }.ToJsonString();
    }

    /// <summary><c>boolean(//SimpleItem[@Name="port" and @Value="3"]) and ...</c>; a value with " is quoted with '.</summary>
    public static string ContentFilter(IEnumerable<TriggerFilter> filters) =>
        string.Join(" and ", filters.Select(f => $"boolean(//SimpleItem[@Name={Quote(f.Name)} and @Value={Quote(f.Value)}])"));

    private static string Quote(string value) => value.Contains('"', StringComparison.Ordinal) ? "'" + value.Replace("'", string.Empty, StringComparison.Ordinal) + "'" : "\"" + value + "\"";

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

        var method = root?["method"]?.GetValue<string>();
        if (method == "events:configure")
        {
            var error = root?["error"];
            var text = error is null ? null : error["message"]?.GetValue<string>() ?? "The device refused the event filter.";
            return new StreamMessage(null, true, text);
        }

        if (method != "events:notify" || root?["params"]?["notification"] is not JsonObject notification)
        {
            return new StreamMessage(null, false, null);
        }

        var topic = notification["topic"]?.GetValue<string>() ?? string.Empty;
        DateTimeOffset? time = notification["timestamp"] is JsonValue stamp && stamp.TryGetValue<long>(out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
        var message = notification["message"] as JsonObject;
        return new StreamMessage(new StreamNotification(topic, time, Values(message?["source"]), Values(message?["data"])), false, null);
    }

    /// <summary>Whether a state value means on ("1", "true", "active", "yes", "on").</summary>
    public static bool IsOn(string? value) =>
        value is not null && (value.Trim().ToUpperInvariant() is "1" or "TRUE" or "ACTIVE" or "YES" or "ON");

    /// <summary>Whether a notification is one of the trigger's: same topic, every narrowed value equal.</summary>
    public static bool Matches(TriggerDefinition trigger, StreamNotification notification)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentNullException.ThrowIfNull(notification);
        return string.Equals(trigger.Topic, notification.Topic, StringComparison.Ordinal)
            && trigger.Filters.All(f => (notification.Source.TryGetValue(f.Name, out var v) || notification.Data.TryGetValue(f.Name, out v)) && string.Equals(v, f.Value, StringComparison.Ordinal));
    }

    /// <summary>Key of the source a state belongs to ("port=3"): with "Any" each port has its own state.</summary>
    public static string SourceKey(StreamNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var key = new StringBuilder();
        foreach (var (name, value) in notification.Source.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            key.Append(name).Append('=').Append(value).Append('\n');
        }

        return key.ToString();
    }

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
