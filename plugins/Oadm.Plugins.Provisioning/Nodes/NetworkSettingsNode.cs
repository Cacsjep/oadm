using System.Text.Json.Nodes;

using Oadm.Plugins.Network.Model;
using Oadm.Plugins.Provisioning.Model;

namespace Oadm.Plugins.Provisioning.Nodes;

/// <summary>
/// Network settings (spec node 5): the DNS servers and the search domain, written by the Network plugin's task
/// "Network settings" (<c>oadm.network</c>) with its checks. Provisioning never sets a camera's host name.
/// </summary>
public sealed class NetworkSettingsNode : TaskNode<NetworkSettingsSettings>
{
    public const string NetworkTaskId = "oadm.network";

    /// <summary>Most DNS servers (the Network task's limit).</summary>
    public const int MaxDns = 2;

    /// <summary>The error of a node without DNS servers (the editor shows it below the field).</summary>
    public const string EnterDns = "Enter the DNS servers.";

    public override NodeKind Kind => NodeKind.NetworkSettings;

    public override string Name => "Network settings";

    public override string TaskPluginId => NetworkTaskId;

    public override object DefaultSettings => new NetworkSettingsSettings([], null);

    protected override string? CheckSettings(NetworkSettingsSettings settings, ProfileCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var dns = Dns(settings);
        var domain = settings.SearchDomain?.Trim() ?? string.Empty;
        if (dns.Count == 0)
        {
            return EnterDns;
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

        return null;
    }

    protected override string Payload(NetworkSettingsSettings settings, NodeRun run)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var change = new JsonObject { ["useDhcp"] = false, ["servers"] = new JsonArray([.. Dns(settings).Select(d => JsonValue.Create(d))]) };
        if (settings.SearchDomain?.Trim() is { Length: > 0 } domain)
        {
            change["searchDomains"] = new JsonArray(JsonValue.Create(domain));
        }
        else
        {
            change["keepDomains"] = true;
        }

        return new JsonObject { ["dns"] = change }.ToJsonString();
    }

    protected override string Done(NetworkSettingsSettings settings, NodeRun run)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return "DNS " + string.Join(", ", Dns(settings))
            + (settings.SearchDomain?.Trim() is { Length: > 0 } domain ? ", search domain " + domain : string.Empty) + ".";
    }

    private static List<string> Dns(NetworkSettingsSettings settings) =>
        [.. (settings.DnsServers ?? []).Select(d => d?.Trim() ?? string.Empty).Where(d => d.Length > 0)];
}
