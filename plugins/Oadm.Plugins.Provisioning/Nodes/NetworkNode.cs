using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Plugins.Network.Model;
using Oadm.Plugins.Provisioning.Model;
using Oadm.Sdk.Network;
using Oadm.Sdk.Tasks;

namespace Oadm.Plugins.Provisioning.Nodes;

/// <summary>
/// Network (spec node 2): the camera's address, DNS servers and search domain, each optional, at least one set. The DNS
/// servers and the search domain go first with the Network plugin's task "Network settings" (<c>oadm.network</c>); the
/// address last: the next free address of the node's own pool (<see cref="INodeHost.TakeAddressAsync"/>, under a short lock)
/// with the Network plugin's task "Assign IP address" (<c>oadm.network.assign-ip</c>: the API first, the address free, the
/// camera at the new address within its wait, OADM follows it; on failure OADM keeps the camera at the address that answers).
/// Keep never writes an address. Never the host name. The DHCP server is never changed; a pool that overlaps its range is a
/// warning.
/// </summary>
public sealed class NetworkNode : IProvisioningNode
{
    /// <summary>The Network plugin's task that writes the address.</summary>
    public const string AssignIpTaskId = "oadm.network.assign-ip";

    /// <summary>The Network plugin's task that writes the DNS servers and the search domain.</summary>
    public const string NetworkTaskId = "oadm.network";

    /// <summary>Most addresses in one pool.</summary>
    public const int MaxPoolSize = 4_096;

    /// <summary>Most DNS servers (the Network task's limit).</summary>
    public const int MaxDns = 2;

    public const string PoolUsedUp = "No free address left in the pool.";

    public const string SetOnePart = "Choose an address pool, or enter DNS servers or a search domain.";

    public const string DnsForDomain = "Enter the DNS servers for the search domain.";

    public NodeKind Kind => NodeKind.Network;

    public string Name => "Network";

    public string StepName => "Set the network settings";

    public object DefaultSettings => new NetworkNodeSettings(DnsServers: []);

    /// <summary>
    /// The settings a new node starts with: with OADM's DHCP server set up, the first three numbers of its range ("192.168.15.")
    /// and its subnet mask; else empty with 255.255.255.0.
    /// </summary>
    public static NetworkNodeSettings DefaultsFor(DhcpServerSummary? dhcp)
    {
        var start = dhcp?.RangeStart?.Trim();
        var prefix = start is not null && Ipv4.TryParse(start, out _) ? start[..(start.LastIndexOf('.') + 1)] : null;
        return new NetworkNodeSettings(
            First: prefix,
            Last: prefix,
            SubnetMask: prefix is not null && !string.IsNullOrWhiteSpace(dhcp?.SubnetMask) ? dhcp!.SubnetMask!.Trim() : "255.255.255.0",
            DnsServers: []);
    }

    public string? Check(JsonElement? settings, ProfileCheckContext context) =>
        NodeSettings.Read<NetworkNodeSettings>(settings) is { } read ? Problem(read, out _) : TaskNode<NetworkNodeSettings>.Unreadable;

    public string? Warning(JsonElement? settings, ProfileCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return NodeSettings.Read<NetworkNodeSettings>(settings) is { Ip: IpMode.Pool } read && Problem(read, out var pool) is null && pool is not null
            ? OverlapWarning(pool, context.Dhcp)
            : null;
    }

    /// <summary>"The pool overlaps the DHCP server's range 10.0.0.100 to 10.0.0.200." or null.</summary>
    public static string? OverlapWarning(Pool pool, DhcpServerSummary? dhcp)
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
        if (run.SettingsAs<NetworkNodeSettings>() is not { } settings)
        {
            return NodeResult.Error(TaskNode<NetworkNodeSettings>.Unreadable);
        }

        if (Problem(settings, out var pool) is { } error)
        {
            return NodeResult.Error(error);
        }

        if (run.Camera.DeviceId is not { } deviceId)
        {
            return NodeResult.Error(NodeTexts.NotInOadm);
        }

        var dns = Dns(settings);
        var domain = settings.SearchDomain?.Trim() is { Length: > 0 } d ? d : null;
        var parts = new List<string>();
        if (dns.Count > 0 || domain is not null)
        {
            var outcome = await TaskRuns.RunTaskAsync(run.Host, NetworkTaskId, deviceId, DnsPayload(dns, domain), ct).ConfigureAwait(false);
            if (outcome is null || outcome.State is not (TaskEndState.Done or TaskEndState.DoneWithWarnings))
            {
                // The address stays as it is: nothing more is written.
                return NodeResult.Error(NodeRules.Sentence(outcome?.Message, NodeTexts.TaskLost(Name)));
            }

            parts.Add(DnsText(dns, domain));
            if (outcome.State == TaskEndState.DoneWithWarnings && pool is null)
            {
                return NodeResult.Warning(NodeRules.Sentence(outcome.Message, DnsText(dns, domain)));
            }
        }

        if (pool is null)
        {
            return NodeResult.Success(string.Join(" ", parts));
        }

        var take = await run.Host.TakeAddressAsync(run.Camera, pool, ct).ConfigureAwait(false);
        if (take.Address is not { } address)
        {
            return NodeResult.Error(take.InUse.Count == 0 ? PoolUsedUp : InUseText(take.InUse) + ". " + PoolUsedUp);
        }

        if (string.Equals(run.Camera.Address, address, StringComparison.Ordinal))
        {
            parts.Insert(0, $"The camera is at {address} already.");
            return NodeResult.Success(string.Join(" ", parts));
        }

        var assigned = (take.InUse.Count == 0 ? string.Empty : InUseText(take.InUse) + ", ") + $"{address} assigned.";
        var written = await TaskRuns.RunTaskAsync(run.Host, AssignIpTaskId, deviceId, AddressPayload(pool, deviceId, address, dns), ct).ConfigureAwait(false);
        var message = string.IsNullOrWhiteSpace(written?.Message) ? null : written!.Message!.Trim();
        parts.Insert(0, assigned);
        return written?.State switch
        {
            TaskEndState.Done => NodeResult.Success(string.Join(" ", parts)),
            TaskEndState.DoneWithWarnings => NodeResult.Warning(string.Join(" ", parts) + (message is null ? string.Empty : " " + message)),
            _ => NodeResult.Error($"The camera did not take {address}" + (message is null ? "." : ": " + message.TrimEnd('.') + ".")
                + $" OADM keeps it at {run.Camera.Address}."),
        };
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

    /// <summary>"DNS 10.0.0.1, search domain site.local." / "Search domain site.local.".</summary>
    private static string DnsText(List<string> dns, string? domain) =>
        (dns.Count > 0 ? "DNS " + string.Join(", ", dns) + (domain is null ? string.Empty : ", search domain " + domain) : "Search domain " + domain) + ".";

    /// <summary>The payload of "Network settings": the DNS servers and the search domain (none: the camera keeps its domains).</summary>
    public static string DnsPayload(IReadOnlyList<string> dns, string? domain)
    {
        ArgumentNullException.ThrowIfNull(dns);
        var change = new JsonObject { ["useDhcp"] = false, ["servers"] = new JsonArray([.. dns.Select(d => JsonValue.Create(d))]) };
        if (domain is not null)
        {
            change["searchDomains"] = new JsonArray(JsonValue.Create(domain));
        }
        else
        {
            change["keepDomains"] = true;
        }

        return new JsonObject { ["dns"] = change }.ToJsonString();
    }

    /// <summary>The payload of "Assign IP address" for one device: static IPv4 with the pool's mask and router, the DNS servers (domains kept).</summary>
    public static string AddressPayload(Pool pool, Guid deviceId, string address, IReadOnlyList<string> dns)
    {
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentNullException.ThrowIfNull(dns);
        var payload = new JsonObject
        {
            ["ipv4"] = new JsonObject { ["mode"] = "static", ["prefixLength"] = pool.PrefixLength, ["gateway"] = pool.Gateway },
            ["devices"] = new JsonObject { [deviceId.ToString()] = new JsonObject { ["ipv4Address"] = address } },
        };
        if (dns.Count > 0)
        {
            payload["dns"] = new JsonObject
            {
                ["useDhcp"] = false,
                ["servers"] = new JsonArray([.. dns.Select(d => JsonValue.Create(d))]),
                ["keepDomains"] = true,
            };
        }

        return payload.ToJsonString();
    }

    private static List<string> Dns(NetworkNodeSettings settings) =>
        [.. (settings.DnsServers ?? []).Select(d => d?.Trim() ?? string.Empty).Where(d => d.Length > 0)];

    /// <summary>The one error of the settings, or null; <paramref name="pool"/> is the checked pool when the address comes from one.</summary>
    public static string? Problem(NetworkNodeSettings settings, out Pool? pool)
    {
        ArgumentNullException.ThrowIfNull(settings);
        pool = null;
        if (!Enum.IsDefined(settings.Ip))
        {
            return "Choose where the address comes from.";
        }

        var dns = Dns(settings);
        var domain = settings.SearchDomain?.Trim() ?? string.Empty;
        if (settings.Ip == IpMode.Pool && Pool.Problem(settings, out pool) is { } poolError)
        {
            return poolError;
        }

        if (dns.Count > MaxDns)
        {
            return $"Enter at most {MaxDns} DNS servers.";
        }

        if (dns.FirstOrDefault(d => !Ipv4.TryParse(d, out _)) is { } bad)
        {
            return $"{bad} is not an IPv4 address.";
        }

        if (domain.Length > 0 && !NodeRules.IsDomainName(domain))
        {
            return $"{domain} is not a domain name.";
        }

        if (domain.Length > 0 && dns.Count == 0)
        {
            return DnsForDomain;
        }

        return settings.Ip == IpMode.Keep && dns.Count == 0 && domain.Length == 0 ? SetOnePart : null;
    }

    /// <summary>A checked pool: first and last address, prefix length and router.</summary>
    public sealed record Pool(uint First, uint Last, int PrefixLength, string Gateway)
    {
        public long Size => (long)Last - First + 1;

        /// <summary>Whether <paramref name="address"/> is in the pool.</summary>
        public bool Contains(string? address) => Ipv4.TryParse(address, out var value) && value >= First && value <= Last;

        /// <summary>The pool of the settings, or null with the one error.</summary>
        public static Pool? Of(NetworkNodeSettings settings, out string? error)
        {
            ArgumentNullException.ThrowIfNull(settings);
            error = Problem(settings, out var pool);
            return error is null ? pool : null;
        }

        internal static string? Problem(NetworkNodeSettings s, out Pool? pool)
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

            pool = new Pool(first, last, prefix, Ipv4.Format(gateway));
            return null;
        }
    }
}
