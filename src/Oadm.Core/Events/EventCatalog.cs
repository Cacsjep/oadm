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

/// <summary>Built-in events plus the event types and actions of the registered plugins (read live from the registry).</summary>
public sealed class EventCatalog(PluginRegistry registry)
{
    public PluginRegistry Registry { get; } = registry;

    public IReadOnlyList<CatalogEventType> EventTypes
    {
        get
        {
            var list = BuiltInEvents.Types.Select(t => new CatalogEventType(t, BuiltInEvents.SourceId, BuiltInEvents.SourceName, true)).ToList();
            var names = Registry.AllCorePlugins.ToDictionary(c => c.Id, c => c.Origin.DisplayName, StringComparer.OrdinalIgnoreCase);
            foreach (var type in Registry.AllEventTypes)
            {
                if (list.Exists(t => string.Equals(t.Id, type.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    continue; // built-in ids are reserved
                }

                list.Add(new CatalogEventType(type.Type, type.SourceId, names.GetValueOrDefault(type.SourceId) ?? type.Origin.DisplayName, Registry.IsEnabled(type.Origin)));
            }

            return list;
        }
    }

    public IReadOnlyList<CatalogAction> Actions =>
        [.. Registry.AllActionPlugins.Select(a => new CatalogAction(a, Registry.IsEnabled(a.Origin)))];

    public CatalogEventType? FindEventType(string? id) =>
        id is null ? null : EventTypes.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    public CatalogAction? FindAction(string? id) =>
        id is null ? null : Actions.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
}
