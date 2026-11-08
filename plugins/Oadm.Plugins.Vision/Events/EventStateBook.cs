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

    /// <summary>A per-device event chosen without its device: the node would follow none.</summary>
    public const string DeviceMissing = "Choose a device.";

    private readonly Lock _sync = new();
    private readonly HashSet<(string Type, Guid? Device)> _on = [];
    private IDisposable? _subscription;
    private int _generation;

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

    /// <summary>
    /// On now for <paramref name="deviceId"/>; null: the server's event only. A per-device event without a device is never on
    /// (the save and the preview ask for a device): a node never follows every device by itself.
    /// </summary>
    public bool IsOn(string typeId, Guid? deviceId)
    {
        lock (_sync)
        {
            return _on.Contains((typeId, deviceId));
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

            // A late event of an older subscription never lands after this (its generation is no longer the current one).
            _generation++;
            _on.Clear();
            if (!wanted)
            {
                ending = _subscription;
                _subscription = null;
            }
            else if (subscriptions() is { } source)
            {
                var generation = _generation;
                _subscription = source.Subscribe(e => Handle(e, generation));
            }
        }

        ending?.Dispose();
    }

    /// <summary>One published event of the current subscription (tests).</summary>
    internal void Handle(PublishedEvent published)
    {
        int generation;
        lock (_sync)
        {
            generation = _generation;
        }

        Handle(published, generation);
    }

    /// <summary>One published event of the subscription of <paramref name="generation"/> (its loop, one at a time).</summary>
    private void Handle(PublishedEvent published, int generation)
    {
        ArgumentNullException.ThrowIfNull(published);
        if (IsOwn(published.TypeId) || published.Kind == PublishedEventKind.Fired)
        {
            return;
        }

        var key = (published.TypeId, published.DeviceId);
        lock (_sync)
        {
            if (generation != _generation)
            {
                return;
            }

            if (published.Kind == PublishedEventKind.On)
            {
                _on.Add(key);
            }
            else
            {
                _on.Remove(key);
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
}
