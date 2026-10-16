using Oadm.Sdk.Events;

namespace Oadm.Plugins.Dashboards;

/// <summary>
/// The latest value of every event per device, in memory: on / off for an on / off event and the last value of each
/// field, with the time. At most <see cref="DashboardsPluginInfo.MaxKeys"/> keys; the least recently updated is dropped
/// first. Per event type it also remembers which fields were seen and their last 20 values (the type suggestion). O(1)
/// per event; thread-safe (events arrive on the subscription's loop, reads come from page calls).
/// </summary>
internal sealed class ValueStore(int maxKeys = DashboardsPluginInfo.MaxKeys)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<ValueKey, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _order = []; // least recently updated first
    private readonly Dictionary<string, Dictionary<string, Queue<string>>> _fields = new(StringComparer.Ordinal);

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Takes an event; returns its key.</summary>
    public ValueKey Apply(PublishedEvent published)
    {
        ArgumentNullException.ThrowIfNull(published);
        var key = new ValueKey(published.TypeId, published.DeviceId);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var node))
            {
                _order.Remove(node);
            }
            else
            {
                node = new LinkedListNode<Entry>(new Entry(key));
                _entries[key] = node;
            }

            _order.AddLast(node);
            var entry = node.Value;
            entry.On = published.Kind switch
            {
                PublishedEventKind.On => true,
                PublishedEventKind.Off => false,
                _ => entry.On,
            };
            entry.Time = published.Time;
            foreach (var (name, value) in published.Data)
            {
                entry.Data[name] = value; // each field keeps its last value; an off event without data keeps them
                Remember(published.TypeId, name, value);
            }

            while (_entries.Count > maxKeys && _order.First is { } oldest)
            {
                _order.RemoveFirst();
                _entries.Remove(oldest.Value.Key);
            }
        }

        return key;
    }

    /// <summary>The value of a key: null when no event came for it.</summary>
    public (bool On, IReadOnlyDictionary<string, string> Data, DateTimeOffset Time)? Get(ValueKey key)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(key, out var node)
                ? (node.Value.On, new Dictionary<string, string>(node.Value.Data, StringComparer.Ordinal), node.Value.Time)
                : null;
        }
    }

    /// <summary>Whether an on / off event is on for the key (false when nothing came).</summary>
    public bool IsOn(ValueKey key)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(key, out var node) && node.Value.On;
        }
    }

    /// <summary>The fields seen in events of a type with their recent values, in the order first seen.</summary>
    public IReadOnlyList<(string Name, IReadOnlyList<string> Recent)> FieldsOf(string typeId)
    {
        lock (_gate)
        {
            return _fields.TryGetValue(typeId, out var fields)
                ? [.. fields.Select(f => (f.Key, (IReadOnlyList<string>)[.. f.Value]))]
                : [];
        }
    }

    /// <summary>Whether this field was seen before (a new one changes the catalog).</summary>
    public bool HasField(string typeId, string field)
    {
        lock (_gate)
        {
            return _fields.TryGetValue(typeId, out var fields) && fields.ContainsKey(field);
        }
    }

    /// <summary>Forgets everything (the plugin stopped).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _order.Clear();
            _fields.Clear();
        }
    }

    private void Remember(string typeId, string field, string value)
    {
        if (!_fields.TryGetValue(typeId, out var fields))
        {
            fields = new Dictionary<string, Queue<string>>(StringComparer.Ordinal);
            _fields[typeId] = fields;
        }

        if (!fields.TryGetValue(field, out var recent))
        {
            if (fields.Count >= 64)
            {
                return; // a source that invents field names never grows the catalog without bound
            }

            recent = new Queue<string>(DashboardsPluginInfo.RecentValues);
            fields[field] = recent;
        }

        if (recent.Count >= DashboardsPluginInfo.RecentValues)
        {
            recent.Dequeue();
        }

        recent.Enqueue(value.Length > 256 ? value[..256] : value);
    }

    private sealed class Entry(ValueKey key)
    {
        public ValueKey Key { get; } = key;

        public bool On { get; set; }

        public Dictionary<string, string> Data { get; } = new(StringComparer.Ordinal);

        public DateTimeOffset Time { get; set; }
    }
}
