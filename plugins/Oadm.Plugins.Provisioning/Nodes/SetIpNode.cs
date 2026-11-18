using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Plugins.Network.Model;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Sdk.Tasks;

namespace Oadm.Plugins.Provisioning.Nodes;

/// <summary>
/// Set IP (spec node 15): takes and reserves the next free address of the node's own pool (<see cref="INodeHost.TakeAddressAsync"/>,
/// under a short lock), then writes the fixed IP, mask, router and DNS servers with the Network plugin's task "Assign IP
/// address" (its checks: API first, the address free, the device at the new address, OADM follows it). The DHCP server's
/// settings are never touched; a pool that overlaps its range is a warning.
/// </summary>
public sealed class SetIpNode : IProvisioningNode
{
    /// <summary>The Network plugin's task that writes the address.</summary>
    public const string TaskPluginId = "oadm.network.assign-ip";

    /// <summary>Most addresses in one pool (the session keeps a reservation per camera).</summary>
    public const int MaxPoolSize = 4_096;

    public const string PoolUsedUp = "No free address left in the pool.";

    public NodeKind Kind => NodeKind.SetIp;

    public string Name => "Set IP";

    public object DefaultSettings => new SetIpSettings(DnsServers: []);

    public string? Check(JsonElement? settings, ProfileCheckContext context) =>
        NodeSettings.Read<SetIpSettings>(settings) is { } read ? Pool.Of(read, out var error) is null ? error : null : TaskNode<SetIpSettings>.Unreadable;

    public string? Warning(JsonElement? settings, ProfileCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return NodeSettings.Read<SetIpSettings>(settings) is { } read && Pool.Of(read, out _) is { } pool ? OverlapWarning(pool, context.Dhcp) : null;
    }

    /// <summary>"The pool overlaps the DHCP server's range 10.0.0.100 to 10.0.0.200." or null.</summary>
    public static string? OverlapWarning(Pool pool, Sdk.Network.DhcpServerSummary? dhcp)
    {
        ArgumentNullException.ThrowIfNull(pool);
        if (dhcp is null || !Ipv4.TryParse(dhcp.RangeStart, out var start) || !Ipv4.TryParse(dhcp.RangeEnd, out var end))
        {
            return null;
        }

        return pool.First <= end && start <= pool.Last
            ? $"The pool overlaps the DHCP server's range {dhcp.RangeStart} to {dhcp.RangeEnd}."
            : null;
    }

    public async Task<NodeResult> RunAsync(NodeRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.SettingsAs<SetIpSettings>() is not { } settings)
        {
            return NodeResult.Error(TaskNode<SetIpSettings>.Unreadable);
        }

        if (Pool.Of(settings, out var error) is not { } pool)
        {
            return NodeResult.Error(error!);
        }

        if (run.Camera.DeviceId is not { } deviceId)
        {
            return NodeResult.Error(NodeTexts.NotInOadm);
        }

        var take = await run.Host.TakeAddressAsync(run.Camera.Mac, settings, ct).ConfigureAwait(false);
        if (take.Address is not { } address)
        {
            return NodeResult.Error(take.InUse.Count == 0 ? PoolUsedUp : InUseText(take.InUse) + ". " + PoolUsedUp);
        }

        var assigned = (take.InUse.Count == 0 ? string.Empty : InUseText(take.InUse) + ", ") + $"{address} assigned.";
        if (string.Equals(run.Camera.Address, address, StringComparison.Ordinal))
        {
            return NodeResult.Success($"The camera is at {address} already.");
        }

        var outcome = await TaskRuns.RunTaskAsync(run.Host, TaskPluginId, deviceId, Payload(pool, deviceId, address), ct).ConfigureAwait(false);
        var message = string.IsNullOrWhiteSpace(outcome?.Message) ? null : outcome!.Message!.Trim();
        switch (outcome?.State)
        {
            case TaskEndState.Done:
                run.Host.AddressChanged(run.Camera.Mac, address);
                return NodeResult.Success(assigned);
            case TaskEndState.DoneWithWarnings:
                run.Host.AddressChanged(run.Camera.Mac, address);
                return NodeResult.Warning(assigned + (message is null ? string.Empty : " " + message));
            default:
                return NodeResult.Error($"The camera did not take {address}" + (message is null ? "." : ": " + message.TrimEnd('.') + ".")
                    + $" OADM keeps it at {run.Camera.Address}.");
        }
    }

    /// <summary>"192.168.15.12 is in use", "192.168.15.10 and 192.168.15.11 are in use", "5 addresses are in use"; empty for none.</summary>
    public static string InUseText(IReadOnlyList<string> inUse)
    {
        ArgumentNullException.ThrowIfNull(inUse);
        return inUse.Count switch
        {
            0 => string.Empty,
            1 => $"{inUse[0]} is in use",
            2 or 3 => $"{string.Join(", ", inUse.Take(inUse.Count - 1))} and {inUse[^1]} are in use",
            _ => $"{inUse.Count} addresses are in use",
        };
    }

    /// <summary>The payload of "Assign IP address" for one device: static IPv4 with the pool's mask and router, the DNS servers.</summary>
    public static string Payload(Pool pool, Guid deviceId, string address)
    {
        ArgumentNullException.ThrowIfNull(pool);
        var payload = new JsonObject
        {
            ["ipv4"] = new JsonObject { ["mode"] = "static", ["prefixLength"] = pool.PrefixLength, ["gateway"] = pool.Gateway },
            ["devices"] = new JsonObject { [deviceId.ToString()] = new JsonObject { ["ipv4Address"] = address } },
        };
        if (pool.Dns.Count > 0)
        {
            payload["dns"] = new JsonObject
            {
                ["useDhcp"] = false,
                ["servers"] = new JsonArray([.. pool.Dns.Select(d => JsonValue.Create(d))]),
                ["keepDomains"] = true,
            };
        }

        return payload.ToJsonString();
    }

    /// <summary>A checked pool: first and last address, prefix length, router and DNS servers.</summary>
    public sealed record Pool(uint First, uint Last, int PrefixLength, string Gateway, IReadOnlyList<string> Dns)
    {
        public long Size => (long)Last - First + 1;

        /// <summary>The pool of the settings, or null with the one error.</summary>
        public static Pool? Of(SetIpSettings settings, out string? error)
        {
            ArgumentNullException.ThrowIfNull(settings);
            error = Problem(settings, out var pool);
            return error is null ? pool : null;
        }

        private static string? Problem(SetIpSettings s, out Pool? pool)
        {
            pool = null;
            if (!Ipv4.TryParse(s.First, out var first))
            {
                return "Enter the first address of the pool, e.g. 192.168.15.10.";
            }

            if (!Ipv4.TryParse(s.Last, out var last))
            {
                return "Enter the last address of the pool, e.g. 192.168.15.50.";
            }

            if (!Ipv4.TryParsePrefix(s.SubnetMask, out var prefix) || prefix is < 1 or > 30 || !(s.SubnetMask ?? string.Empty).Contains('.', StringComparison.Ordinal))
            {
                return "Enter a subnet mask, e.g. 255.255.255.0.";
            }

            if (last < first)
            {
                return "The last address must not be below the first.";
            }

            if (!Ipv4.SameSubnet(first, last, prefix))
            {
                return "The first and last address must be in the same subnet.";
            }

            if ((long)last - first + 1 > MaxPoolSize)
            {
                return $"A pool has at most {MaxPoolSize:N0} addresses.";
            }

            if (Ipv4.HostAddressProblem(first, prefix) is { } firstProblem)
            {
                return $"{s.First!.Trim()} {firstProblem}.";
            }

            if (Ipv4.HostAddressProblem(last, prefix) is { } lastProblem)
            {
                return $"{s.Last!.Trim()} {lastProblem}.";
            }

            if (!Ipv4.TryParse(s.Gateway, out var gateway))
            {
                return "Enter the default router, e.g. 192.168.15.1.";
            }

            if (!Ipv4.SameSubnet(first, gateway, prefix) || Ipv4.HostAddressProblem(gateway, prefix) is not null)
            {
                return "The default router must be in the subnet of the pool.";
            }

            if (gateway >= first && gateway <= last)
            {
                return "The default router must not be in the pool.";
            }

            var dns = (s.DnsServers ?? []).Select(d => d?.Trim() ?? string.Empty).Where(d => d.Length > 0).ToList();
            if (dns.Count > 2)
            {
                return "Enter at most 2 DNS servers.";
            }

            if (dns.FirstOrDefault(d => !Ipv4.TryParse(d, out _)) is { } bad)
            {
                return $"{bad} is not an IPv4 address.";
            }

            pool = new Pool(first, last, prefix, Ipv4.Format(gateway), dns);
            return null;
        }
    }
}
