namespace Oadm.Sdk.Events;

/// <summary>
/// Stateless events are one-shot pulses ("rollout failed"); stateful events are on or off ("blur detected" until it
/// clears). The server keeps the current state of every stateful event per device.
/// </summary>
public enum EventKind
{
    /// <summary>Happens once ("Rollout failed").</summary>
    Stateless = 0,
    /// <summary>Is on or off ("Blur detected").</summary>
    Stateful = 1,
}

/// <summary>Device: one state (or pulse) per managed device. Server: one for the whole server.</summary>
public enum EventScope
{
    /// <summary>One state per device.</summary>
    Device = 0,
    /// <summary>One state for the server.</summary>
    Server = 1,
}

/// <summary>Type of an event data field, for the condition filter of a rule.</summary>
public enum EventFieldType
{
    /// <summary>Any text.</summary>
    Text = 0,
    /// <summary>A number.</summary>
    Number = 1,
    /// <summary>One of <see cref="EventField.Values"/>.</summary>
    Choice = 2,
}

/// <summary>What kind of value a data field holds, as the device or plugin declares it (a dashboard picks its widget by it).</summary>
public enum EventValueKind
{
    /// <summary>Any text (also when nothing is declared).</summary>
    Text = 0,
    /// <summary>A number (invariant culture).</summary>
    Number = 1,
    /// <summary>Yes or no ("true", "1").</summary>
    YesNo = 2,
}

/// <summary>A data field an event carries ("detection" = "blur"); a rule can filter on it (equals).</summary>
/// <param name="Name">The key in the event's data, also its placeholder ({port}).</param>
/// <param name="DisplayName">What the rule dialog shows.</param>
/// <param name="Type">How a condition compares it.</param>
/// <param name="Values">The choices of a <see cref="EventFieldType.Choice"/> field.</param>
public sealed record EventField(string Name, string DisplayName, EventFieldType Type = EventFieldType.Text, IReadOnlyList<string>? Values = null)
{
    /// <summary>The kind of value as declared; <see cref="EventValueKind.Text"/> when not known.</summary>
    public EventValueKind Kind { get; init; }

    /// <summary>False: a value only (a placeholder, a dashboard value), no filter in the rule dialog. Default true.</summary>
    public bool Filter { get; init; } = true;
}

/// <summary>
/// An event a source can publish. <paramref name="Id"/> is stable and unique ("oadm.device-triggers.&lt;id&gt;");
/// <paramref name="DisplayName"/> names the event in plain language ("Blur detected"), for a stateful event its "on"
/// state; <paramref name="Group"/> groups the picker of the rule dialog ("Image Health").
/// </summary>
/// <param name="Id">Stable, unique id.</param>
/// <param name="DisplayName">The event in plain words; for an on and off event its on state.</param>
/// <param name="Group">The group in the event list of the rule dialog.</param>
/// <param name="Kind">One-shot or on and off.</param>
/// <param name="Scope">Per device or for the server.</param>
/// <param name="Fields">The data fields a condition can filter on.</param>
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

    /// <summary><see cref="Fields"/>, never null.</summary>
    public IReadOnlyList<EventField> DataFields => Fields ?? [];
}

/// <summary>
/// A core plugin that publishes events for the event rules implements this next to <c>ICorePlugin</c> and publishes
/// through <c>ICorePluginContext.EventPublisher</c>. The types are read once when the plugin is registered (see
/// <see cref="IDynamicEventSource"/> for types that change).
/// </summary>
public interface IEventSource
{
    /// <summary>The event types the plugin publishes.</summary>
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
    /// <summary>Raise after <see cref="IEventSource.EventTypes"/> changed.</summary>
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

    /// <summary>Whether an enabled rule or an event subscription uses the type: a source may skip work nobody needs.</summary>
    bool IsWatched(string typeId);
}
