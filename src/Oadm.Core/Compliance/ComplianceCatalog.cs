using Oadm.Core.Plugins;
using Oadm.Sdk.Compliance;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Compliance;

/// <summary>A check type with where it comes from and whether that source is on.</summary>
/// <param name="Type">The type.</param>
/// <param name="SourceName">"Built in", "Applications (ACAP)".</param>
/// <param name="Owner">The core plugin that contributed it; null for the host's own types.</param>
/// <param name="Origin">Its plugin package; null for the host's own types.</param>
/// <param name="Available">False while its plugin is off.</param>
public sealed record CatalogComplianceType(IComplianceCheckType Type, string SourceName, ICorePlugin? Owner, PluginOrigin? Origin, bool Available)
{
    public string Id => Type.Id;

    /// <summary>"Not available: Edge storage is turned off".</summary>
    public string UnavailableText => $"Not available: {SourceName} is turned off";
}

/// <summary>The host's check types plus the types of the registered core plugins (<see cref="IComplianceSource"/>).</summary>
public sealed class ComplianceCatalog
{
    public ComplianceCatalog(PluginRegistry registry)
    {
        Registry = registry ?? throw new ArgumentNullException(nameof(registry));
        Registry.EnabledChanged += (_, _) =>
        {
            _types = null;
            Changed?.Invoke(this, EventArgs.Empty);
        };
    }

    private volatile IReadOnlyList<CatalogComplianceType>? _types;

    public PluginRegistry Registry { get; }

    /// <summary>A plugin package was turned on or off: types may have become available or not.</summary>
    public event EventHandler? Changed;

    /// <summary>Every type, host types first; built again after a package was turned on or off.</summary>
    public IReadOnlyList<CatalogComplianceType> Types => _types ??= Build();

    private List<CatalogComplianceType> Build()
    {
        var types = BuiltInComplianceTypes.All
            .Select(t => new CatalogComplianceType(t, BuiltInComplianceTypes.SourceName, null, null, true))
            .ToList();
        foreach (var registered in Registry.AllComplianceTypes)
        {
            if (types.Exists(t => string.Equals(t.Id, registered.Id, StringComparison.OrdinalIgnoreCase)))
            {
                continue; // host ids are reserved
            }

            types.Add(new CatalogComplianceType(registered.Type, registered.Origin.DisplayName, registered.Owner, registered.Origin, Registry.IsEnabled(registered.Origin)));
        }

        return types;
    }

    /// <summary>A type by id; an unknown id reads the registry again (a plugin registered since).</summary>
    public CatalogComplianceType? Find(string? id)
    {
        if (id is null)
        {
            return null;
        }

        foreach (var type in Types)
        {
            if (string.Equals(type.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return type;
            }
        }

        _types = null;
        return Types.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
    }
}
