using Oadm.Core.Plugins;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Events;

namespace Oadm.Core.Events;

/// <summary>
/// The events OADM publishes itself: device status from polling and the HTTPS certificate (user decision 2026-10-10:
/// built in is only what OADM already knows; every other device event comes from an opt-in plugin).
/// </summary>
public static class BuiltInEvents
{
    public const string SourceId = "oadm.device";
    public const string SourceName = "Built in";
    public const string Group = "Device status";

    public const string Unreachable = "oadm.device.unreachable";
    public const string CredentialsRequired = "oadm.device.credentials-required";
    public const string CertificateChanged = "oadm.device.certificate-changed";
    public const string CertificateExpiresSoon = "oadm.device.certificate-expires-soon";
    public const string CertificateExpired = "oadm.device.certificate-expired";

    /// <summary>"Certificate expires soon": 30 days or less left (fixed, like the Devices grid).</summary>
    public const int ExpiresSoonDays = 30;

    public static readonly IReadOnlyList<EventTypeInfo> Types =
    [
        new(Unreachable, "Device unreachable", Group, EventKind.Stateful) { Description = "The device does not answer." },
        new(CredentialsRequired, "Credentials required", Group, EventKind.Stateful) { Description = "The device rejects the stored login." },
        new(CertificateChanged, "Certificate changed", Group, EventKind.Stateful) { Description = "The device presents another HTTPS certificate." },
        new(CertificateExpiresSoon, "Certificate expires soon", Group, EventKind.Stateful) { Description = "30 days or less left." },
        new(CertificateExpired, "Certificate expired", Group, EventKind.Stateful),
    ];
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

    public EventCatalog(PluginRegistry registry)
    {
        Registry = registry ?? throw new ArgumentNullException(nameof(registry));
        Registry.EnabledChanged += (_, _) => Invalidate();
        Registry.EventTypesChanged += (_, _) => Invalidate();
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
