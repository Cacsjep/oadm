using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Provisioning.Nodes;

namespace Oadm.Plugins.Provisioning.Engine;

/// <summary>
/// Profiles of the build of 2026-11-18 in today's nodes (docs/spec/provisioning.md "Nodes"): Add to OADM and Wait for approval
/// are dropped (adding is implicit, the tags come from the Add devices page), Set IP and Network settings become one Network
/// node at the place of the first of them: Set IP's pool, the DNS servers of Network settings (else Set IP's) and its search
/// domain. A profile without them comes back as it is.
/// </summary>
public static class ProfileUpgrade
{
    /// <summary>The profile in today's nodes; <paramref name="changed"/> says whether anything was converted.</summary>
    public static Profile Convert(Profile profile, out bool changed)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var nodes = profile.Nodes ?? [];
        changed = false;
        if (!nodes.Any(n => n.Kind is NodeKind.AddToOadm or NodeKind.WaitForApproval or NodeKind.SetIp or NodeKind.NetworkSettings))
        {
            return profile;
        }

        changed = true;
        var setIp = nodes.FirstOrDefault(n => n.Kind == NodeKind.SetIp);
        var networkSettings = nodes.FirstOrDefault(n => n.Kind == NodeKind.NetworkSettings);
        var list = new List<FlowNode>(nodes.Count);
        var merged = false;
        foreach (var node in nodes)
        {
            switch (node.Kind)
            {
                case NodeKind.AddToOadm or NodeKind.WaitForApproval:
                    break;
                case NodeKind.SetIp or NodeKind.NetworkSettings:
                    if (!merged)
                    {
                        merged = true;
                        list.Add(new FlowNode
                        {
                            Id = node.Id,
                            Kind = NodeKind.Network,
                            ContinueOnError = node.ContinueOnError,
                            Settings = NodeSettings.Write(Merge(setIp, networkSettings)),
                        });
                    }

                    break;
                default:
                    list.Add(node);
                    break;
            }
        }

        return profile with { Nodes = list };
    }

    /// <summary>The Network settings of an old Set IP and an old Network settings node (either may be missing).</summary>
    private static NetworkNodeSettings Merge(FlowNode? setIp, FlowNode? networkSettings)
    {
        var pool = setIp is null ? null : NodeSettings.Read<SetIpSettings>(setIp.Settings);
        var dns = networkSettings is null ? null : NodeSettings.Read<NetworkSettingsSettings>(networkSettings.Settings);
        var servers = dns?.DnsServers is { Count: > 0 } own ? own : pool?.DnsServers ?? [];
        return new NetworkNodeSettings(
            Ip: setIp is null ? IpMode.Keep : IpMode.Pool,
            First: pool?.First,
            Last: pool?.Last,
            SubnetMask: pool?.SubnetMask,
            Gateway: pool?.Gateway,
            DnsServers: [.. servers],
            SearchDomain: dns?.SearchDomain);
    }
}
