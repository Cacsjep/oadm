using Oadm.Sdk.Events;

namespace Oadm.Plugins.Dashboards;

/// <summary>
/// The rows of the table widgets, in memory only: every event of a recorded value adds one row, at most
/// <see cref="DashboardsPluginInfo.MaxTableRows"/> per table with the oldest dropped first, each text cut to
/// <see cref="DashboardsPluginInfo.MaxTableText"/> characters. Only values a stored table widget names are recorded; two
/// widgets of the same value, device and split value share their rows. O(1) per event; thread-safe (events arrive on the subscription's loop, reads
/// come from page calls).
/// </summary>
internal sealed class TableStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<TableKey, Table> _tables = [];
    private readonly Dictionary<ValueKey, List<Table>> _byEvent = [];

    /// <summary>The tables there are now.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _tables.Count;
            }
        }
    }

    /// <summary>Records these values from now on; tables of other values go with their rows, the others keep theirs.</summary>
    public void Keep(IEnumerable<TableKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var wanted = keys.ToHashSet();
        lock (_gate)
        {
            foreach (var gone in _tables.Keys.Where(k => !wanted.Contains(k)).ToList())
            {
                _tables.Remove(gone);
            }

            foreach (var key in wanted)
            {
                if (!_tables.ContainsKey(key))
                {
                    _tables[key] = new Table(key);
                }
            }

            _byEvent.Clear();
            foreach (var table in _tables.Values)
            {
                var key = new ValueKey(table.Key.TypeId, table.Key.DeviceId, table.Key.Split);
                if (!_byEvent.TryGetValue(key, out var tables))
                {
                    tables = [];
                    _byEvent[key] = tables;
                }

                tables.Add(table);
            }
        }
    }

    /// <summary>
    /// An event, stored under <paramref name="key"/> (its device and split value): one row in each table of its value. An
    /// event without that value adds none (the off event of a trigger whose text came with the on event).
    /// </summary>
    public void Apply(PublishedEvent published, ValueKey key)
    {
        ArgumentNullException.ThrowIfNull(published);
        lock (_gate)
        {
            if (!_byEvent.TryGetValue(key, out var tables))
            {
                return;
            }

            foreach (var table in tables)
            {
                if (TextOf(published, table.Key.Field) is { } text)
                {
                    table.Add(published.Time, FieldValues.TableText(text));
                }
            }
        }
    }

    /// <summary>The rows the reader does not have; null when no table records the value.</summary>
    public TableRows? Read(TableRead read)
    {
        ArgumentNullException.ThrowIfNull(read);
        lock (_gate)
        {
            if (!_tables.TryGetValue(read.Key, out var table))
            {
                return null;
            }

            var after = read.Ring == table.Ring ? read.After : long.MinValue;
            return new TableRows(read.WidgetId, table.Ring, [.. table.Rows.Where(r => r.Seq > after)]);
        }
    }

    /// <summary>Forgets every row (the plugin stopped).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _tables.Clear();
            _byEvent.Clear();
        }
    }

    private static string? TextOf(PublishedEvent published, string field)
    {
        if (field != DashboardsPluginInfo.ActiveField)
        {
            return published.Data.GetValueOrDefault(field);
        }

        return published.Kind switch
        {
            PublishedEventKind.On => DashboardTexts.On,
            PublishedEventKind.Off => DashboardTexts.Off,
            _ => null,
        };
    }

    private sealed class Table(TableKey key)
    {
        private long _seq;

        public TableKey Key { get; } = key;

        /// <summary>Tells a reader that this is not the table it read before (see <see cref="TableRead"/>).</summary>
        public string Ring { get; } = Guid.NewGuid().ToString("N");

        public Queue<TableRow> Rows { get; } = new();

        public void Add(DateTimeOffset time, string text)
        {
            if (Rows.Count >= DashboardsPluginInfo.MaxTableRows)
            {
                Rows.Dequeue();
            }

            Rows.Enqueue(new TableRow(++_seq, time, text));
        }
    }
}
