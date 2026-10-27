namespace Oadm.Sdk.Plugins;

/// <summary>
/// One live event of a core plugin for its client page (e.g. a new request log line, a status change).
/// <paramref name="Topic"/> is chosen by the plugin ("status", "requests"); the payload is plugin-defined JSON.
/// </summary>
/// <param name="Topic">What kind of event, chosen by the plugin.</param>
/// <param name="PayloadJson">The event's JSON.</param>
public sealed record PluginEvent(string Topic, string? PayloadJson);

/// <summary>
/// Pushes live events from a core plugin to every client page watching it (gRPC <c>PluginService.Watch</c>).
/// Fire and forget: <see cref="Publish(string, string?)"/> never blocks and never throws for slow or missing watchers; a watcher
/// that falls behind loses the oldest events (pages re-read their state through <c>InvokeAsync</c> after
/// reconnecting). Throttle high-rate sources in the plugin (e.g. batch log lines every 500 ms).
/// </summary>
public interface IPluginEvents
{
    /// <summary>Largest payload a single event may carry (UTF-16 characters); larger events are dropped and logged.</summary>
    const int MaxPayloadLength = 1024 * 1024;

    /// <summary>Sends an event to every page that watches; never blocks. <paramref name="topic"/> is yours ("status", "rows").</summary>
    void Publish(string topic, string? payloadJson);

    /// <summary>
    /// Sends an event to the watchers <paramref name="audience"/> covers (SDK 0.4.0): an event about devices reaches only
    /// users who can see them. On OADM 4.5 and later <see cref="Publish(string, string?)"/> reaches only watchers with all
    /// devices. The default (older hosts, which have no device groups) is <see cref="Publish(string, string?)"/>.
    /// </summary>
    void Publish(string topic, string? payloadJson, PluginEventAudience audience) => Publish(topic, payloadJson);
}
