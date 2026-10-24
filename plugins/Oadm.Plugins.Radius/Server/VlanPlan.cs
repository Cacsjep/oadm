using Oadm.Plugins.Radius.Api;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Radius.Server;

/// <summary>
/// The VLAN rules compiled for the certificate check (docs/spec/radius-server.md "VLANs"): off by default; in order, the
/// first rule whose site or tag the device has wins, else the default VLAN, else none. Immutable; a save replaces it, so it
/// applies to the next authentication of each device. Tag rules apply only while the host protects their tags
/// (<see cref="IProtectedTags"/>: only users with all devices add or remove them); on a host without it they match no
/// device (fail closed).
/// </summary>
public sealed class VlanPlan
{
    private readonly IReadOnlyList<(bool Site, Guid? SiteId, string Tag, int Vlan)> _rules;
    private readonly int? _default;

    public VlanPlan(VlanSettings settings, bool tagsProtected = true)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Enabled = settings.Enabled;
        _default = settings.DefaultVlan;
        _rules = [.. (settings.Rules ?? []).Where(r => tagsProtected || r.Kind == VlanRuleKinds.Site).Select(r => r.Kind == VlanRuleKinds.Site
            ? (true, Guid.TryParse(r.RefId, out var id) && id != Guid.Empty ? id : (Guid?)null, string.Empty, r.Vlan)
            : (false, (Guid?)null, r.RefId.Trim(), r.Vlan))];
    }

    /// <summary>Assign VLANs is off: no VLAN for anyone.</summary>
    public static VlanPlan Off { get; } = new(new VlanSettings());

    public bool Enabled { get; }

    /// <summary>The tag names of the rules (the tags the host protects), case-insensitive, distinct.</summary>
    public static IReadOnlyList<string> TagsOf(VlanSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return [.. (settings.Rules ?? []).Where(r => r.Kind == VlanRuleKinds.Tag && !string.IsNullOrWhiteSpace(r.RefId))
            .Select(r => r.RefId.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The VLAN of <paramref name="device"/>, null for none.</summary>
    public int? For(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!Enabled)
        {
            return null;
        }

        foreach (var (site, siteId, tag, vlan) in _rules)
        {
            if (site ? device.SiteId == siteId : device.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                return vlan;
            }
        }

        return _default;
    }
}
