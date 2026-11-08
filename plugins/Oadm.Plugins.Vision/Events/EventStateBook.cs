using System.Text.Json.Nodes;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Model;
using Oadm.Sdk.Events;

namespace Oadm.Plugins.Vision.Events;

/// <summary>
/// The on and off events of the event rules for the Device event node (docs/spec/vision-center.md "Nodes"): followed
/// through <see cref="IEventSubscriptions"/> (the snapshot of what is on, then each change, on the subscription's own
/// queue) only while a pipeline or a preview has a Device event node, kept per type and device in memory. Vision Center's
/// own events never come back into its pipelines (no loop).
/// </summary>
internal sealed class EventStateBook(Func<IEventSubscriptions?> subscriptions) : IEventStates, IDisposable
{
    /// <summary>The prefix of Vision Center's own event types.</summary>
    public const string OwnPrefix = VisionInfo.PluginId + ".";

    /// <summary>The refusal of one of Vision Center's own events in a Device event node.</summary>
    public const string OwnEventRefused = "Choose an event of another plugin or a device.";

    private readonly Lock _sync = new();
    private readonly HashSet<(string Type, Guid? Device)> _on = [];
    private readonly Dictionary<string, int> _onPerType = new(StringComparer.Ordinal);
    private IDisposable? _subscription;

    /// <summary>A subscription runs now (tests).</summary>
    public bool Following
    {
        get
        {
            lock (_sync)
            {
                return _subscription is not null;
            }
        }
    }

    /// <summary>On now for <paramref name="deviceId"/>; null: the server's event, or the type on for any device.</summary>
    public bool IsOn(string typeId, Guid? deviceId)
    {
        lock (_sync)
        {
            return deviceId is null ? _onPerType.GetValueOrDefault(typeId) > 0 : _on.Contains((typeId, deviceId));
        }
    }

    /// <summary>Starts following when <paramref name="wanted"/> (a Device event node exists), ends and forgets otherwise.</summary>
    public void Follow(bool wanted)
    {
        IDisposable? ending = null;
        lock (_sync)
        {
            if (wanted == (_subscription is not null))
            {
                return;
            }

            Clear();
            if (!wanted)
            {
                ending = _subscription;
                _subscription = null;
            }
            else if (subscriptions() is { } source)
            {
                _subscription = source.Subscribe(Handle);
            }
        }

        ending?.Dispose();
    }

    /// <summary>One published event (the subscription's loop, one at a time).</summary>
    internal void Handle(PublishedEvent published)
    {
        ArgumentNullException.ThrowIfNull(published);
        if (IsOwn(published.TypeId) || published.Kind == PublishedEventKind.Fired)
        {
            return;
        }

        var key = (published.TypeId, published.DeviceId);
        lock (_sync)
        {
            if (published.Kind == PublishedEventKind.On)
            {
                if (_on.Add(key))
                {
                    _onPerType[published.TypeId] = _onPerType.GetValueOrDefault(published.TypeId) + 1;
                }
            }
            else if (_on.Remove(key) && --_onPerType[published.TypeId] == 0)
            {
                _onPerType.Remove(published.TypeId);
            }
        }
    }

    /// <summary>A type of Vision Center itself.</summary>
    public static bool IsOwn(string? typeId) => typeId is not null && typeId.StartsWith(OwnPrefix, StringComparison.Ordinal);

    /// <summary>The event a Device event node follows: {"type": ..., "device": ...}; null type when none is chosen.</summary>
    public static (string? Type, Guid? Device) ChoiceOf(PipelineNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!node.Settings.TryGetPropertyValue(NodeKinds.EventKey, out var value) || value is not JsonObject choice)
        {
            return (null, null);
        }

        var type = choice["type"] is JsonValue t && t.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;
        var device = choice["device"] is JsonValue d && d.TryGetValue<string>(out var id) && Guid.TryParse(id, out var guid) ? guid : (Guid?)null;
        return (type, device);
    }

    /// <summary>Whether <paramref name="pipeline"/> has a Device event node.</summary>
    public static bool Uses(Pipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        return pipeline.Nodes.Any(n => n.Kind == NodeKinds.DeviceEvent);
    }

    public void Dispose() => Follow(false);

    private void Clear()
    {
        _on.Clear();
        _onPerType.Clear();
    }
}
