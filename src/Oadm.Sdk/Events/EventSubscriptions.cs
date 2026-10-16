namespace Oadm.Sdk.Events;

/// <summary>What happened in a <see cref="PublishedEvent"/>.</summary>
public enum PublishedEventKind
{
    /// <summary>A stateful event turned on, or is on (also when only its data changed).</summary>
    On = 0,

    /// <summary>A stateful event turned off.</summary>
    Off = 1,

    /// <summary>A one-shot event happened.</summary>
    Fired = 2,
}

/// <summary>
/// An event as it was published to the server: by a plugin through <see cref="IEventPublisher"/> or by OADM itself
/// (Device unreachable, Certificate expires soon). Received through <see cref="IEventSubscriptions"/>.
/// </summary>
/// <param name="TypeId">The event type (<see cref="EventTypeInfo.Id"/>).</param>
/// <param name="DeviceId">The device, or null for an event of the server.</param>
/// <param name="Kind">On, off or fired.</param>
/// <param name="Data">The event's values as received ("temperature" = "41.5"); empty when it has none.</param>
/// <param name="Time">When the server received it (for <see cref="FromSnapshot"/> events: when the subscription started).</param>
/// <param name="FromSnapshot">Part of the snapshot of the events that were on when the subscription started.</param>
public sealed record PublishedEvent(
    string TypeId,
    Guid? DeviceId,
    PublishedEventKind Kind,
    IReadOnlyDictionary<string, string> Data,
    DateTimeOffset Time,
    bool FromSnapshot = false);

/// <summary>
/// Lets a core plugin receive every event published to the server, the same ones the event rules see
/// (<c>ICorePluginContext.EventSubscriptions</c>). A subscription starts with every stateful event that is on now
/// (<see cref="PublishedEvent.FromSnapshot"/>), then gets each event as it is published. The server never waits for a
/// subscriber: each subscription has its own queue of 10,000 events, and when the handler cannot keep up the oldest are
/// dropped (logged). The handler runs on the subscription's own loop, one event at a time; an exception is logged and
/// the subscription goes on. Subscriptions end when they are disposed or when the plugin stops.
/// </summary>
public interface IEventSubscriptions
{
    /// <summary>Starts a subscription; dispose the result to end it.</summary>
    IDisposable Subscribe(Action<PublishedEvent> handler);
}
