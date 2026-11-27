using System.Text;

using Oadm.Plugins.DeviceTriggers;
using Oadm.Plugins.DeviceTriggers.Device;

namespace Oadm.Plugins.EventStream;

/// <summary>
/// The trigger filters of Device triggers on the shared event stream JSON (<c>plugins/Shared/EventStream</c>): one entry
/// per topic, the value filters of its triggers joined with "or".
/// </summary>
public static partial class EventStreamJson
{
    /// <summary>The filter list for the triggers of one device: one entry per topic, their value filters joined with "or".</summary>
    public static string Configure(IEnumerable<TriggerDefinition> triggers) => Configure(Entries(triggers));

    /// <summary>The filter list entries for the triggers of one device: one per topic, ordered by topic.</summary>
    public static IReadOnlyList<FilterEntry> Entries(IEnumerable<TriggerDefinition> triggers)
    {
        ArgumentNullException.ThrowIfNull(triggers);
        var entries = new List<FilterEntry>();
        foreach (var topic in triggers.GroupBy(t => t.Topic, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            string? content = null;
            if (topic.All(t => t.Filters.Count > 0))
            {
                var parts = topic.Select(t => ContentFilter(t.Filters)).Distinct(StringComparer.Ordinal).ToList();
                content = parts.Count == 1 ? parts[0] : string.Join(" or ", parts.Select(p => "(" + p + ")"));
            }

            entries.Add(new FilterEntry(topic.Key, content, [.. topic]));
        }

        return entries;
    }

    /// <summary>
    /// The <c>events:configure</c> message with these entries (a new one replaces the device's filter list). The device
    /// echoes <paramref name="context"/> in its answer, so an answer to an older list is told apart.
    /// </summary>
    public static string Configure(IEnumerable<FilterEntry> entries, string context = "oadm")
    {
        ArgumentNullException.ThrowIfNull(entries);
        return ConfigureTopics(entries.Select(e => new KeyValuePair<string, string?>(e.Topic, e.ContentFilter)), context);
    }

    /// <summary>
    /// <c>boolean(//SimpleItem[@Name="port" and @Value="3"]) and ...</c>; a value with " is quoted with ', one with both
    /// is an XPath <c>concat()</c>.
    /// </summary>
    public static string ContentFilter(IEnumerable<TriggerFilter> filters) =>
        string.Join(" and ", filters.Select(f => $"boolean(//SimpleItem[@Name={Quote(f.Name)} and @Value={Quote(f.Value)}])"));

    internal static string Quote(string value)
    {
        if (!value.Contains('"', StringComparison.Ordinal))
        {
            return "\"" + value + "\"";
        }

        if (!value.Contains('\'', StringComparison.Ordinal))
        {
            return "'" + value + "'";
        }

        // XPath 1.0 has no escape inside a literal: concat("it's ", '"', "x", '"').
        return "concat(" + string.Join(", '\"', ", value.Split('"').Select(part => "\"" + part + "\"")) + ")";
    }

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
}
