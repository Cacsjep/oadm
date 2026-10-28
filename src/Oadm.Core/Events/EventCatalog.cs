using Oadm.Core.Plugins;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Events;

namespace Oadm.Core.Events;

/// <summary>
/// The events OADM publishes itself: device status from polling and the HTTPS certificate
/// (built in is only what OADM already knows; every other device event comes from an opt-in plugin).
/// </summary>
public static class BuiltInEvents
{
    public const string SourceId = "oadm.device";
    public const string SourceName = "Built in";
    public const string Group = "Device status";

    public const string Unreachable = "oadm.device.unreachable";
    public const string CredentialsRequired = "oadm.device.credentials-required";
    public const string CertificateChanged = "oadm.device.certificate-changed";

    /// <summary>
    /// "Certificate expires soon": since OADM 4.5 an event of PKI (group "Certificates", the days of its Expiry warning
    /// policies, docs/spec/pki.md); the id stays, so stored rules keep working.
    /// </summary>
    public const string CertificateExpiresSoon = "oadm.device.certificate-expires-soon";

    /// <summary>"Certificate expired": since OADM 4.5 an event of PKI; the id stays.</summary>
    public const string CertificateExpired = "oadm.device.certificate-expired";

    /// <summary>The group of the "Any device" events: one state for the server, on while any device has the condition.</summary>
    public const string AnyGroup = "Any device";

    public const string AnyUnreachable = "oadm.device.any-unreachable";
    public const string AnyCredentialsRequired = "oadm.device.any-credentials-required";
    public const string AnyCertificateChanged = "oadm.device.any-certificate-changed";

    /// <summary>The Any device twin of <see cref="CertificateExpiresSoon"/> (PKI's since OADM 4.5, the id stays).</summary>
    public const string AnyCertificateExpiresSoon = "oadm.device.any-certificate-expires-soon";

    /// <summary>The Any device twin of <see cref="CertificateExpired"/> (PKI's since OADM 4.5, the id stays).</summary>
    public const string AnyCertificateExpired = "oadm.device.any-certificate-expired";

    /// <summary>
    /// On while at least one device's clock is off (<see cref="Devices.ClockHealth.IsOff(Devices.Device, int)"/>), value
    /// <see cref="CountField"/>. No per-device twin.
    /// </summary>
    public const string AnyClockOff = "oadm.device.any-clock-off";

    /// <summary>The value of an "Any device" event: how many devices have the condition.</summary>
    public const string CountField = "count";

    /// <summary>Each device event and its "Any device" twin.</summary>
    public static readonly IReadOnlyList<(string Device, string Any)> AnyOf =
    [
        (Unreachable, AnyUnreachable),
        (CredentialsRequired, AnyCredentialsRequired),
        (CertificateChanged, AnyCertificateChanged),
    ];

    public static readonly IReadOnlyList<EventTypeInfo> Types =
    [
        new(Unreachable, "Device unreachable", Group, EventKind.Stateful) { Description = "The device does not answer." },
        new(CredentialsRequired, "Credentials required", Group, EventKind.Stateful) { Description = "The device rejects the stored login." },
        new(CertificateChanged, "Certificate changed", Group, EventKind.Stateful) { Description = "The device presents another HTTPS certificate." },
        Any(AnyUnreachable, "Any device: unreachable", "At least one device does not answer."),
        Any(AnyCredentialsRequired, "Any device: credentials required", "At least one device rejects the stored login."),
        Any(AnyCertificateChanged, "Any device: certificate changed", "At least one device presents another HTTPS certificate."),
        Any(AnyClockOff, "Any device: clock off", "The clock of at least one device differs from the server's or is not synced."),
    ];

    private static EventTypeInfo Any(string id, string name, string description) =>
        new(id, name, AnyGroup, EventKind.Stateful, EventScope.Server, [new EventField(CountField, "Devices") { Kind = EventValueKind.Number, Filter = false }])
        {
            Description = description + "\n\nValues:\n• {" + CountField + "}",
        };
}

/// <summary>
/// Events the host publishes besides <see cref="BuiltInEvents"/> (the compliance checks): types that change while the
/// server runs. Raise <see cref="EventTypesChanged"/> after <see cref="EventTypes"/> changed, then report their states.
/// </summary>
public interface IHostEventSource
{
    string SourceId { get; }

    string SourceName { get; }

    IReadOnlyList<EventTypeInfo> EventTypes { get; }

    event EventHandler? EventTypesChanged;
}

/// <summary>An event type with where it comes from and whether that source is on.</summary>
public sealed record CatalogEventType(EventTypeInfo Info, string SourceId, string SourceName, bool Available)
{
    public string Id => Info.Id;

    public bool Stateful => Info.Kind == EventKind.Stateful;

    public bool DeviceScope => Info.Scope == EventScope.Device;
}

/// <summary>An action with its package name and whether the package is on.</summary>
public sealed record CatalogAction(RegisteredActionPlugin Registered, bool Available)
{
    public string Id => Registered.Id;

    public IActionPlugin Plugin => Registered.Plugin;

    public string SourceName => Registered.Origin.DisplayName;

    /// <summary>"Not available: VAPIX Commander is turned off".</summary>
    public string UnavailableText => $"Not available: {SourceName} is turned off";
}

/// <summary>
/// Built-in events plus the event types and actions of the registered plugins. Lookups by id are O(1) on a snapshot that
/// is rebuilt when packages are turned on or off or a source's event types change (registry events), when a list is read
/// (catalog for the dialog, source sync of the engine), and when an id is not found (a plugin registered later).
/// </summary>
public sealed class EventCatalog
{
    private readonly Lock _sync = new();
    private volatile Snapshot? _snapshot;
    private long _version;
    private int _builds;

    private readonly IReadOnlyList<IHostEventSource> _hostSources;

    public EventCatalog(PluginRegistry registry, IEnumerable<IHostEventSource>? hostSources = null)
    {
        Registry = registry ?? throw new ArgumentNullException(nameof(registry));
        Registry.EnabledChanged += (_, _) => Invalidate();
        Registry.EventTypesChanged += (_, _) => Invalidate();
        _hostSources = [.. hostSources ?? []];
        foreach (var source in _hostSources)
        {
            source.EventTypesChanged += (_, _) => Invalidate();
        }
    }

    public PluginRegistry Registry { get; }

    /// <summary>Every event type, read fresh from the registry.</summary>
    public IReadOnlyList<CatalogEventType> EventTypes => Rebuild().Types;

    /// <summary>Every action, read fresh from the registry.</summary>
    public IReadOnlyList<CatalogAction> Actions => Rebuild().Actions;

    /// <summary>How often the snapshot was built (tests).</summary>
    public int Builds => Volatile.Read(ref _builds);

    public CatalogEventType? FindEventType(string? id)
    {
        if (id is null)
        {
            return null;
        }

        if (Current().TypesById.TryGetValue(id, out var type))
        {
            return type;
        }

        // Not known: a plugin may have registered since (no registry event for that); rarely taken.
        return Rebuild().TypesById.GetValueOrDefault(id);
    }

    public CatalogAction? FindAction(string? id)
    {
        if (id is null)
        {
            return null;
        }

        if (Current().ActionsById.TryGetValue(id, out var action))
        {
            return action;
        }

        return Rebuild().ActionsById.GetValueOrDefault(id);
    }

    /// <summary>Drops the snapshot; the next lookup reads the registry again.</summary>
    public void Invalidate()
    {
        lock (_sync)
        {
            _version++;
            _snapshot = null;
        }
    }

    private Snapshot Current() => _snapshot ?? Rebuild();

    private Snapshot Rebuild()
    {
        long version;
        lock (_sync)
        {
            version = _version;
        }

        var types = BuiltInEvents.Types.Select(t => new CatalogEventType(t, BuiltInEvents.SourceId, BuiltInEvents.SourceName, true)).ToList();
        var typesById = types.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var source in _hostSources)
        {
            foreach (var type in source.EventTypes)
            {
                var entry = new CatalogEventType(type, source.SourceId, source.SourceName, true);
                if (typesById.TryAdd(entry.Id, entry))
                {
                    types.Add(entry);
                }
            }
        }

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var core in Registry.AllCorePlugins)
        {
            names.TryAdd(core.Id, core.Origin.DisplayName);
        }

        foreach (var type in Registry.AllEventTypes)
        {
            if (typesById.ContainsKey(type.Id))
            {
                continue; // built-in ids are reserved
            }

            var entry = new CatalogEventType(type.Type, type.SourceId, names.GetValueOrDefault(type.SourceId) ?? type.Origin.DisplayName, Registry.IsEnabled(type.Origin));
            types.Add(entry);
            typesById[entry.Id] = entry;
        }

        var actions = Registry.AllActionPlugins.Select(a => new CatalogAction(a, Registry.IsEnabled(a.Origin))).ToList();
        var actionsById = new Dictionary<string, CatalogAction>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in actions)
        {
            actionsById.TryAdd(action.Id, action);
        }

        var snapshot = new Snapshot(types, typesById, actions, actionsById);
        lock (_sync)
        {
            if (version == _version)
            {
                _snapshot = snapshot; // a change while building leaves the snapshot empty: the next lookup builds again
            }
        }

        Interlocked.Increment(ref _builds);
        return snapshot;
    }

    private sealed record Snapshot(
        IReadOnlyList<CatalogEventType> Types,
        Dictionary<string, CatalogEventType> TypesById,
        IReadOnlyList<CatalogAction> Actions,
        Dictionary<string, CatalogAction> ActionsById);
}
