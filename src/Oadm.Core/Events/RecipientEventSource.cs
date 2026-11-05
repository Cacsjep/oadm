using System.Globalization;

using Oadm.Sdk.Events;

namespace Oadm.Core.Events;

/// <summary>
/// The event Recipient not connected (docs/spec/recipient-sessions.md): one stateful server event, on while a recipient that
/// rules use cannot be reached, with the values {count} and {recipient}.
/// </summary>
public sealed class RecipientEventSource : IHostEventSource
{
    public const string EventSourceId = "oadm.recipients";

    public const string EventSourceName = "Recipients";

    public const string NotConnectedTypeId = "oadm.recipient.not-connected";

    public const string CountField = "count";

    public const string RecipientField = "recipient";

    private readonly Func<RecipientSessionPool> _pool;
    private readonly Func<IEventPublisher> _publisher;
    private readonly Lock _gate = new();
    private string? _reported;

    public RecipientEventSource(RecipientSessionPool pool, Func<IEventPublisher> publisher)
        : this(() => pool, publisher)
    {
        ArgumentNullException.ThrowIfNull(pool);
    }

    /// <param name="pool">Read at <see cref="Start"/>: the pool needs the event catalog, which holds this source.</param>
    /// <param name="publisher">Read at the first report.</param>
    public RecipientEventSource(Func<RecipientSessionPool> pool, Func<IEventPublisher> publisher)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
    }

    public string SourceId => EventSourceId;

    public string SourceName => EventSourceName;

    public IReadOnlyList<EventTypeInfo> EventTypes { get; } =
    [
        new EventTypeInfo(NotConnectedTypeId, "Recipient not connected", EventSourceName, EventKind.Stateful, EventScope.Server,
            [
                new EventField(CountField, "Recipients") { Kind = EventValueKind.Number, Filter = false },
                new EventField(RecipientField, "Recipient") { Filter = false },
            ])
        {
            Description = "A recipient cannot be reached.\n\nValues:\n• {" + CountField + "}\n• {" + RecipientField + "}",
        },
    ];

#pragma warning disable CS0067 // The one type never changes.
    public event EventHandler? EventTypesChanged;
#pragma warning restore CS0067

    /// <summary>The first full report (the rule engine runs); then the event follows the pool.</summary>
    public void Start()
    {
        _pool().Changed += (_, _) => Report();
        Report();
    }

    private void Report()
    {
        lock (_gate)
        {
            var names = _pool().Statuses()
                .Where(s => s.State == RecipientConnectionState.NotConnected)
                .Select(s => s.Name)
                .Order(StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            var text = string.Join(", ", names);
            if (string.Equals(text, _reported, StringComparison.Ordinal))
            {
                return;
            }

            var publisher = _publisher();
            if (names.Count > 0)
            {
                publisher.SetState(NotConnectedTypeId, null, true, new Dictionary<string, string>
                {
                    [CountField] = names.Count.ToString(CultureInfo.InvariantCulture),
                    [RecipientField] = text,
                });
            }

            if (_reported is null || names.Count == 0)
            {
                publisher.ReplaceStates(NotConnectedTypeId, names.Count > 0 ? [Guid.Empty] : []);
            }

            _reported = text;
        }
    }
}
