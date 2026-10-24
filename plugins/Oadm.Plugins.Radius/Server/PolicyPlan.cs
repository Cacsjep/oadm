using Oadm.Plugins.Radius.Api;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Radius.Server;

/// <summary>
/// The policies compiled for the certificate check (docs/spec/radius-server.md "Policies"): in order, the first VLAN policy
/// whose devices hold the device gives its VLAN, else the default VLAN (only while a VLAN policy exists), else none; any
/// block policy that holds it refuses it, any revoke policy refuses it and revokes its certificate. Immutable; a save
/// replaces it, so it applies to the next authentication of each device. The tag parts of VLAN policies apply only while
/// the host protects the tags (<see cref="IProtectedTags"/>); without it they match no device (fail closed: a group user
/// could tag a device into a VLAN). Block policies keep their tag parts (fail closed the other way). Revoke policies never
/// match by tag, also stored ones from before the rule: a tag could revoke the certificates of every device that gets it
/// later, without undo.
/// </summary>
public sealed class PolicyPlan
{
    private readonly IReadOnlyList<Compiled> _policies;

    public PolicyPlan(PolicySettings settings, bool tagsProtected = true)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _policies = [.. (settings.Policies ?? []).Select(p => new Compiled(p, UsesTags(p, tagsProtected)))];
        VlansEnabled = _policies.Any(p => p.Policy.Kind == PolicyKinds.Vlan);
        DefaultVlan = VlansEnabled ? settings.DefaultVlan : null;
        RevokePolicies = [.. _policies.Where(p => p.Policy.Kind == PolicyKinds.Revoke).Select(p => p.Policy)];
    }

    /// <summary>No policies: no VLAN, nobody blocked.</summary>
    public static PolicyPlan Off { get; } = new(new PolicySettings());

    /// <summary>A VLAN policy exists (VLANs stay off until then).</summary>
    public bool VlansEnabled { get; }

    /// <summary>The VLAN of devices no VLAN policy holds (null while no VLAN policy exists).</summary>
    public int? DefaultVlan { get; }

    public IReadOnlyList<RadiusPolicy> RevokePolicies { get; }

    /// <summary>The tag names of the policies (the tags the host protects), case-insensitive, distinct.</summary>
    public static IReadOnlyList<string> TagsOf(PolicySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return [.. (settings.Policies ?? []).SelectMany(p => p.Devices?.Tags ?? []).Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The VLAN of <paramref name="device"/> (null: none); a device OADM does not manage gets the default VLAN.</summary>
    public int? VlanFor(IDeviceInfo? device)
    {
        if (!VlansEnabled)
        {
            return null;
        }

        if (device is not null)
        {
            foreach (var compiled in _policies)
            {
                if (compiled.Policy.Kind == PolicyKinds.Vlan && compiled.Holds(device))
                {
                    return compiled.Policy.Vlan;
                }
            }
        }

        return DefaultVlan;
    }

    /// <summary>The first block or revoke policy that holds <paramref name="device"/>, null when none does.</summary>
    public RadiusPolicy? RefusalFor(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        foreach (var compiled in _policies)
        {
            if (compiled.Policy.Kind is PolicyKinds.Block or PolicyKinds.Revoke && compiled.Holds(device))
            {
                return compiled.Policy;
            }
        }

        return null;
    }

    /// <summary><paramref name="policy"/>'s devices hold <paramref name="device"/> (used for revoke policies: never by tag).</summary>
    public static bool Holds(RadiusPolicy policy, IDeviceInfo device) => new Compiled(policy, UsesTags(policy, tagsProtected: true)).Holds(device);

    /// <summary>A revoke policy never matches by tag; a VLAN policy only while the host protects the tags.</summary>
    private static bool UsesTags(RadiusPolicy policy, bool tagsProtected) =>
        policy.Kind != PolicyKinds.Revoke && (tagsProtected || policy.Kind != PolicyKinds.Vlan);

    private sealed class Compiled
    {
        private readonly bool _thisServer;
        private readonly HashSet<Guid> _sites;
        private readonly HashSet<string> _tags;
        private readonly HashSet<string> _serials;
        private readonly HashSet<string> _except;

        public Compiled(RadiusPolicy policy, bool useTags)
        {
            Policy = policy;
            var devices = policy.Devices ?? new PolicyDevices();
            _thisServer = devices.ThisServer;
            _sites = [.. devices.SiteIds ?? []];
            _tags = useTags ? new HashSet<string>((devices.Tags ?? []).Select(t => t.Trim()), StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _serials = new HashSet<string>((devices.Serials ?? []).Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);
            _except = new HashSet<string>((devices.ExceptSerials ?? []).Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);
        }

        public RadiusPolicy Policy { get; }

        public bool Holds(IDeviceInfo device)
        {
            if (_except.Contains(device.Serial))
            {
                return false;
            }

            if (_serials.Contains(device.Serial) || (device.SiteId is { } site ? _sites.Contains(site) : _thisServer))
            {
                return true;
            }

            if (_tags.Count == 0)
            {
                return false;
            }

            foreach (var tag in device.Tags)
            {
                if (_tags.Contains(tag))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
