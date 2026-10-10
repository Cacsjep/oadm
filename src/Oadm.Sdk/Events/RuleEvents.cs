namespace Oadm.Sdk.Events;

/// <summary>
/// Stateless events are one-shot pulses ("rollout failed"); stateful events are on or off ("blur detected" until it
/// clears). The server keeps the current state of every stateful event per device.
/// </summary>
public enum EventKind
{
    Stateless = 0,
    Stateful = 1,
}

/// <summary>Device: one state (or pulse) per managed device. Server: one for the whole server.</summary>
public enum EventScope
{
    Device = 0,
    Server = 1,
}

/// <summary>Type of an event data field, for the condition filter of a rule.</summary>
public enum EventFieldType
{
    Text = 0,
    Number = 1,
    Choice = 2,
}

/// <summary>A data field an event carries ("detection" = "blur"); a rule can filter on it (equals).</summary>
public sealed record EventField(string Name, string DisplayName, EventFieldType Type = EventFieldType.Text, IReadOnlyList<string>? Values = null);

/// <summary>
/// An event a source can publish. <paramref name="Id"/> is stable and unique ("oadm.image-health.blur");
/// <paramref name="DisplayName"/> names the event in plain language ("Blur detected"), for a stateful event its "on"
/// state; <paramref name="Group"/> groups the picker of the rule dialog ("Image Health").
/// </summary>
public sealed record EventTypeInfo(
    string Id,
    string DisplayName,
    string Group,
    EventKind Kind,
    EventScope Scope = EventScope.Device,
    IReadOnlyList<EventField>? Fields = null)
{
    /// <summary>One short sentence for the rule dialog (optional).</summary>
    public string? Description { get; init; }

    public IReadOnlyList<EventField> DataFields => Fields ?? [];
}

/// <summary>
/// A core plugin that publishes events for the event rules implements this next to <c>ICorePlugin</c> and publishes
/// through <c>ICorePluginContext.EventPublisher</c>. The types are read once when the plugin is registered (see
/// <see cref="IDynamicEventSource"/> for types that change).
/// </summary>
public interface IEventSource
{
    IReadOnlyList<EventTypeInfo> EventTypes { get; }
}

/// <summary>
/// An event source whose types change while it runs (types the user creates, e.g. device triggers). Raise
/// <see cref="EventTypesChanged"/> after <see cref="IEventSource.EventTypes"/> changed: the server reads the list again,
/// new stateful types wait for their first <see cref="IEventPublisher.ReplaceStates"/> like after a start, and removed
/// types count as off (their rules turn off and show "Not available").
/// </summary>
public interface IDynamicEventSource : IEventSource
{
    event EventHandler? EventTypesChanged;
}

/// <summary>
/// Publishes events to the server's event rules. Fire and forget: calls only queue (never block, never throw for a
/// full queue) and may come from any thread. Only the source's own event types are accepted.
/// <para>Contract for stateful types: off is the default, no initial value is declared. After every start (and after
/// being turned on) a source sends one <see cref="ReplaceStates"/> per stateful type it serves: that is its "ready";
/// until then the server keeps the states it stored before the restart and runs nothing for the type. Later it may send
/// <see cref="SetState"/> per change or <see cref="ReplaceStates"/> again at any time (e.g. after each check round): the
/// server works out what changed. A source that is turned off counts as <c>ReplaceStates(type, [])</c>.</para>
/// </summary>
public interface IEventPublisher
{
    /// <summary>Stateful: the event turned on or off for a device (null device for server-scoped types).</summary>
    void SetState(string typeId, Guid? deviceId, bool isOn, IReadOnlyDictionary<string, string>? data = null);

    /// <summary>Stateless: the event happened.</summary>
    void Fire(string typeId, Guid? deviceId, IReadOnlyDictionary<string, string>? data = null);

    /// <summary>
    /// Stateful: the full picture of one type. Every device in <paramref name="onDevices"/> is on, every other device
    /// is off. Server-scoped types: an empty list is off, a list with <see cref="Guid.Empty"/> is on.
    /// </summary>
    void ReplaceStates(string typeId, IReadOnlyCollection<Guid> onDevices);

    /// <summary>Whether an enabled rule uses the type: a source may skip work nobody needs.</summary>
    bool IsWatched(string typeId);
}
