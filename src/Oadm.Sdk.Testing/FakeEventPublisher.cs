using System.Collections.Concurrent;

using Oadm.Sdk.Events;

namespace Oadm.Sdk.Testing;

/// <summary>An event a source fired or switched (<see cref="FakeEventPublisher"/>).</summary>
/// <param name="TypeId">The event type.</param>
/// <param name="DeviceId">The device; null for a server event.</param>
/// <param name="IsOn">On or off for a stateful event; null for a stateless one (fired).</param>
/// <param name="Data">The event's values.</param>
public sealed record FakePublishedRuleEvent(string TypeId, Guid? DeviceId, bool? IsOn, IReadOnlyDictionary<string, string> Data);

/// <summary>
/// The event rules as an event source sees them (<see cref="Plugins.ICorePluginContext.EventPublisher"/>): records every event the
/// plugin fires or switches, in order, and answers <see cref="IsWatched"/> and <see cref="IsUsedByRule"/> as the test sets
/// them (<see cref="UseInRule"/>; by default no rule uses anything and everything is watched).
/// </summary>
public sealed class FakeEventPublisher : IEventPublisher
{
    private readonly ConcurrentDictionary<string, byte> _usedByRule = new(StringComparer.Ordinal);

    /// <summary>Every event fired or switched, in order.</summary>
    public ConcurrentQueue<FakePublishedRuleEvent> Published { get; } = new();

    /// <summary>The last full picture per type (<see cref="ReplaceStates"/>).</summary>
    public ConcurrentDictionary<string, IReadOnlyCollection<Guid>> States { get; } = new(StringComparer.Ordinal);

    /// <summary>What <see cref="IsWatched"/> answers; true by default (a source then does all its work).</summary>
    public Func<string, bool> Watched { get; set; } = _ => true;

    /// <summary>An enabled rule uses <paramref name="typeId"/> from now on (<see cref="IsUsedByRule"/> answers true).</summary>
    public void UseInRule(string typeId) => _usedByRule[typeId] = 0;

    /// <summary>No enabled rule uses <paramref name="typeId"/> any more.</summary>
    public void StopUsingInRule(string typeId) => _usedByRule.TryRemove(typeId, out _);

    /// <inheritdoc />
    public void SetState(string typeId, Guid? deviceId, bool isOn, IReadOnlyDictionary<string, string>? data = null) =>
        Published.Enqueue(new FakePublishedRuleEvent(typeId, deviceId, isOn, data ?? new Dictionary<string, string>()));

    /// <inheritdoc />
    public void Fire(string typeId, Guid? deviceId, IReadOnlyDictionary<string, string>? data = null) =>
        Published.Enqueue(new FakePublishedRuleEvent(typeId, deviceId, null, data ?? new Dictionary<string, string>()));

    /// <inheritdoc />
    public void ReplaceStates(string typeId, IReadOnlyCollection<Guid> onDevices)
    {
        ArgumentNullException.ThrowIfNull(onDevices);
        States[typeId] = [.. onDevices];
    }

    /// <summary>What <see cref="Watched"/> answers, and true for a type a rule uses.</summary>
    public bool IsWatched(string typeId) => Watched(typeId) || IsUsedByRule(typeId);

    /// <summary>True for the types of <see cref="UseInRule"/>.</summary>
    public bool IsUsedByRule(string typeId) => _usedByRule.ContainsKey(typeId);
}
