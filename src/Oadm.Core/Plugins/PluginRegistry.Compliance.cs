using Oadm.Sdk.Compliance;
using Oadm.Sdk.Plugins;

namespace Oadm.Core.Plugins;

/// <summary>
/// A compliance check type: standalone in a plugin's server part, or contributed by a core plugin
/// (<see cref="IComplianceSource"/>, then <see cref="Owner"/> is set).
/// </summary>
public sealed record RegisteredComplianceType(IComplianceCheckType Type, ICorePlugin? Owner, PluginOrigin Origin)
{
    public string Id => Type.Id;
}

/// <summary>Compliance check types of the core plugins.</summary>
public sealed partial class PluginRegistry
{
    private readonly List<RegisteredComplianceType> _complianceTypes = [];

    /// <summary>Every registered compliance check type, enabled or not.</summary>
    public IReadOnlyList<RegisteredComplianceType> AllComplianceTypes
    {
        get
        {
            lock (_sync)
            {
                return [.. _complianceTypes];
            }
        }
    }

    private void RegisterComplianceTypes(ICorePlugin plugin, IComplianceSource source, PluginOrigin origin)
    {
        IReadOnlyList<IComplianceCheckType> types;
        try
        {
            types = source.ComplianceCheckTypes ?? [];
        }
#pragma warning disable CA1031 // Plugin code is untrusted.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            AddError(new PluginLoadError(origin.PackageId, $"Compliance checks of {plugin.Id} could not be read: {ex.Message}", ex));
            return;
        }

        foreach (var type in types.Where(t => t is not null))
        {
            RegisterCompliance(type, plugin, origin);
        }
    }

    /// <summary>Registers a standalone check type. Returns false (and records an error) on a duplicate or invalid id.</summary>
    public bool RegisterComplianceType(IComplianceCheckType type, PluginOrigin origin) => RegisterCompliance(type, owner: null, origin);

    private bool RegisterCompliance(IComplianceCheckType type, ICorePlugin? owner, PluginOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(origin);
        string id;
        try
        {
            id = type.Id;
            _ = type.DisplayName;
            _ = type.Fields;
        }
#pragma warning disable CA1031 // Plugin code is untrusted.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            AddError(new PluginLoadError(origin.PackageId, $"Compliance check {type.GetType().FullName} could not be inspected: {ex.Message}", ex));
            return false;
        }

        lock (_sync)
        {
            if (!IsValidId(id) || _complianceTypes.Exists(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                AddErrorLocked(new PluginLoadError(origin.PackageId, $"Compliance check id '{id}' is invalid or already registered."));
                return false;
            }

            _complianceTypes.Add(new RegisteredComplianceType(type, owner, origin));
            return true;
        }
    }
}
