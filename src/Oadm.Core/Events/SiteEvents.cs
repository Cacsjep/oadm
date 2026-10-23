using System.Globalization;
using System.Threading.Channels;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Persistence;
using Oadm.Core.Sites;
using Oadm.Sdk.Events;

namespace Oadm.Core.Events;

/// <summary>
/// The site events of the event rules (docs/spec/sites.md, Event rules): one stateful server event per site "Site offline:
/// &lt;site&gt;" (values {site}, {address}) and "Any site offline" (values {count}, {site} = the names of the offline sites),
/// on while the site's node is not online (after the waiting time of <see cref="ISiteStates"/>). The types follow the
/// sites like Device triggers' dynamic types: a bound, renamed or removed site changes them.
/// </summary>
public sealed partial class SiteEventSource : IHostEventSource, IAsyncDisposable
{
    public const string EventSourceId = "oadm.sites";

    public const string EventSourceName = "Sites";

    public const string EventGroup = "Sites";

    /// <summary>"Any site offline".</summary>
    public const string AnyOfflineTypeId = "oadm.site.any-offline";

    public const string SiteField = "site";

    public const string AddressField = "address";

    public const string CountField = "count";

    private const string OfflinePrefix = "oadm.site.offline.";

    private readonly IDbContextFactory<OadmDbContext> _db;
    private readonly ISiteStates _states;
    private readonly Func<IEventPublisher>? _publisher;
    private readonly ILogger _logger;
    private readonly Lock _sync = new();
    private readonly Channel<bool> _wake = Channel.CreateUnbounded<bool>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<string, (bool On, IReadOnlyDictionary<string, string>? Data)> _reported = new(StringComparer.Ordinal);
    private IReadOnlyList<SiteRow> _sites = [];
    private IReadOnlyList<EventTypeInfo> _types = [];
    private Task _loop = Task.CompletedTask;
    private int _started;

    public SiteEventSource(
        IDbContextFactory<OadmDbContext> db,
        ISiteStates states,
        Func<IEventPublisher>? publisher = null,
        ILogger<SiteEventSource>? logger = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _states = states ?? throw new ArgumentNullException(nameof(states));
        _publisher = publisher;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    string IHostEventSource.SourceId => EventSourceId;

    string IHostEventSource.SourceName => EventSourceName;

    /// <summary>The types changed (a site bound, renamed or removed): the event catalog reads them again.</summary>
    public event EventHandler? EventTypesChanged;

    /// <summary>Raised after the states were reported (tests).</summary>
    public event EventHandler? Reported;

    /// <summary>One type per site plus "Any site offline" while at least one site exists.</summary>
    public IReadOnlyList<EventTypeInfo> EventTypes
    {
        get
        {
            lock (_sync)
            {
                return _types;
            }
        }
    }

    /// <summary>The event type id of a site.</summary>
    public static string OfflineTypeId(Guid siteId) => OfflinePrefix + siteId.ToString("N");

    /// <summary>Reads the sites (before the event rules start, so the types are known).</summary>
    public async Task LoadAsync(CancellationToken ct) => await ReloadAsync(ct).ConfigureAwait(false);

    /// <summary>Reports the states and follows the sites (after the event rules started).</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        _states.Changed += OnChanged;
        _wake.Writer.TryWrite(true);
        _loop = Task.Run(LoopAsync);
    }

    private void OnChanged(object? sender, SiteStatesChangedEventArgs e) => _wake.Writer.TryWrite(true);

    private async Task LoopAsync()
    {
        while (await _wake.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (_wake.Reader.TryRead(out _))
            {
            }

            try
            {
                if (await ReloadAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    EventTypesChanged?.Invoke(this, EventArgs.Empty);
                }

                Report();
            }
#pragma warning disable CA1031 // The loop never ends on a failed read; the next change tries again.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogFailed(ex);
            }
        }
    }

    /// <summary>Reads the sites again; true when the types changed.</summary>
    private async Task<bool> ReloadAsync(CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Sites.AsNoTracking().Select(s => new SiteRow(s.Id, s.Name, s.Address)).ToListAsync(ct).ConfigureAwait(false);
        rows = [.. rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)];
        lock (_sync)
        {
            if (rows.SequenceEqual(_sites))
            {
                return false;
            }

            _sites = rows;
            _types = BuildTypes(rows);
            return true;
        }
    }

    private static List<EventTypeInfo> BuildTypes(IReadOnlyList<SiteRow> sites)
    {
        if (sites.Count == 0)
        {
            return [];
        }

        var types = new List<EventTypeInfo>(sites.Count + 1)
        {
            new(AnyOfflineTypeId, "Any site offline", EventGroup, EventKind.Stateful, EventScope.Server,
                [
                    new EventField(CountField, "Sites") { Kind = EventValueKind.Number, Filter = false },
                    new EventField(SiteField, "Site") { Filter = false },
                ])
            {
                Description = "At least one site node does not answer.\n\nValues:\n• {" + CountField + "}\n• {" + SiteField + "}",
            },
        };
        foreach (var site in sites)
        {
            types.Add(new EventTypeInfo(OfflineTypeId(site.Id), "Site offline: " + site.Name, EventGroup, EventKind.Stateful, EventScope.Server,
                [
                    new EventField(SiteField, "Site") { Filter = false },
                    new EventField(AddressField, "Address") { Filter = false },
                ])
            {
                Description = "The site node of " + site.Name + " does not answer.\n\nValues:\n• {" + SiteField + "}\n• {" + AddressField + "}",
            });
        }

        return types;
    }

    /// <summary>Reports every type whose state changed (all of them the first time, so each type becomes ready).</summary>
    private void Report()
    {
        if (_publisher is null)
        {
            return;
        }

        IReadOnlyList<SiteRow> sites;
        lock (_sync)
        {
            sites = _sites;
        }

        var publisher = _publisher();
        var offline = new List<string>();
        var starting = false;
        foreach (var site in sites)
        {
            if (_states.IsStarting(site.Id))
            {
                // After a server start nothing is known yet: the type waits (no edge), like a source before its first report.
                starting = true;
                continue;
            }

            var on = !_states.IsOnline(site.Id);
            var name = _states.NameOf(site.Id) ?? site.Name;
            if (on)
            {
                offline.Add(name);
            }

            Set(publisher, OfflineTypeId(site.Id), on, on
                ? new Dictionary<string, string> { [SiteField] = name, [AddressField] = site.Address ?? string.Empty }
                : null);
        }

        if (sites.Count > 0 && !starting)
        {
            Set(publisher, AnyOfflineTypeId, offline.Count > 0, offline.Count == 0 ? null : new Dictionary<string, string>
            {
                [CountField] = offline.Count.ToString(CultureInfo.InvariantCulture),
                [SiteField] = string.Join(", ", offline),
            });
        }

        // Types of removed sites are gone from the catalog; forget what was reported for them.
        var current = sites.Select(s => OfflineTypeId(s.Id)).Append(AnyOfflineTypeId).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _reported.Keys.Where(k => !current.Contains(k)).ToList())
        {
            _reported.Remove(gone);
        }

        Reported?.Invoke(this, EventArgs.Empty);
    }

    private void Set(IEventPublisher publisher, string typeId, bool on, IReadOnlyDictionary<string, string>? data)
    {
        if (_reported.TryGetValue(typeId, out var last) && last.On == on && SameData(last.Data, data))
        {
            return;
        }

        var first = !_reported.ContainsKey(typeId);
        _reported[typeId] = (on, data);
        if (on)
        {
            publisher.SetState(typeId, null, true, data);
        }

        if (first || !on)
        {
            // The full report: makes the type ready (and turns it off).
            publisher.ReplaceStates(typeId, on ? [Guid.Empty] : []);
        }
    }

    private static bool SameData(IReadOnlyDictionary<string, string>? a, IReadOnlyDictionary<string, string>? b) =>
        a is null || b is null
            ? a is null && b is null
            : a.Count == b.Count && a.All(e => b.TryGetValue(e.Key, out var value) && string.Equals(value, e.Value, StringComparison.Ordinal));

    public async ValueTask DisposeAsync()
    {
        _states.Changed -= OnChanged;
        _wake.Writer.TryComplete();
        await _loop.ConfigureAwait(false);
    }

    private sealed record SiteRow(Guid Id, string Name, string? Address);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The site events could not be reported")]
    private partial void LogFailed(Exception ex);
}
