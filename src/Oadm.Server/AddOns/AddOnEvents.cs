using System.Globalization;

using Oadm.Core.Events;
using Oadm.Sdk.Events;

namespace Oadm.Server.AddOns;

/// <summary>
/// The event "Optional plugin not loaded" of the event rules (docs/spec/add-ons.md): one stateful server event, on while an
/// installed optional plugin is not loaded (a version that may not run here, after a restore, a load that failed). The host
/// publishes it because a plugin that is not loaded cannot send anything.
/// </summary>
public sealed class AddOnEventSource : IHostEventSource
{
    public const string EventSourceId = "oadm.add-ons";

    public const string EventSourceName = "Optional plugins";

    /// <summary>"Optional plugin not loaded".</summary>
    public const string NotLoadedTypeId = "oadm.add-on.not-loaded";

    /// <summary>The names of the plugins, separated by a comma.</summary>
    public const string PluginField = "plugin";

    /// <summary>How many plugins are not loaded.</summary>
    public const string CountField = "count";

    private readonly Func<IReadOnlyList<string>> _notLoaded;
    private readonly Func<IEventPublisher> _publisher;
    private readonly Lock _sync = new();
    private string? _reported;

    /// <summary>The source.</summary>
    /// <param name="notLoaded">The names of the optional plugins that are not loaded now.</param>
    /// <param name="publisher">The event rules' publisher of this source.</param>
    public AddOnEventSource(Func<IReadOnlyList<string>> notLoaded, Func<IEventPublisher> publisher)
    {
        _notLoaded = notLoaded ?? throw new ArgumentNullException(nameof(notLoaded));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
    }

    string IHostEventSource.SourceId => EventSourceId;

    string IHostEventSource.SourceName => EventSourceName;

    /// <summary>The one type never changes.</summary>
    event EventHandler? IHostEventSource.EventTypesChanged
    {
        add { }
        remove { }
    }

    public IReadOnlyList<EventTypeInfo> EventTypes { get; } =
    [
        new(NotLoadedTypeId, "Optional plugin not loaded", EventSourceName, EventKind.Stateful, EventScope.Server,
            [
                new EventField(CountField, "Plugins") { Kind = EventValueKind.Number, Filter = false },
                new EventField(PluginField, "Plugin") { Filter = false },
            ])
        {
            Description = "An optional plugin must be installed again on the Plugins page.\n\nValues:\n• {" + CountField + "}\n• {" + PluginField + "}",
        },
    ];

    /// <summary>Reports the state when it changed; the first report makes the type ready.</summary>
    public void Report()
    {
        lock (_sync)
        {
            var names = _notLoaded();
            var text = string.Join(", ", names);
            if (text == _reported)
            {
                return;
            }

            var first = _reported is null;
            _reported = text;
            var publisher = _publisher();
            if (names.Count > 0)
            {
                publisher.SetState(NotLoadedTypeId, null, true, new Dictionary<string, string>
                {
                    [CountField] = names.Count.ToString(CultureInfo.InvariantCulture),
                    [PluginField] = text,
                });
            }

            if (first || names.Count == 0)
            {
                // The full report: makes the type ready (and turns it off).
                publisher.ReplaceStates(NotLoadedTypeId, names.Count > 0 ? [Guid.Empty] : []);
            }
        }
    }
}
