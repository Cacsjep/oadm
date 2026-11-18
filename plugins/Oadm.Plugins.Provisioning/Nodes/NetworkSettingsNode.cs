using System.Globalization;
using System.Text.Json.Nodes;

using Oadm.Plugins.Network.Model;
using Oadm.Plugins.Provisioning.Model;

namespace Oadm.Plugins.Provisioning.Nodes;

/// <summary>
/// Network settings (spec node 5): the host name from a pattern ({model}, {serial}, {mac}, {n} counting from the start
/// number in the order the cameras arrive), the DNS servers and the search domain, written by the Network plugin's task
/// "Network settings" (<c>oadm.network</c>) with its checks. Left out parts stay as they are.
/// </summary>
public sealed class NetworkSettingsNode : TaskNode<NetworkSettingsSettings>
{
    public const string NetworkTaskId = "oadm.network";

    /// <summary>Most DNS servers (the Network task's limit).</summary>
    public const int MaxDns = 2;

    private static readonly string[] Unique = ["{n}", "{serial}", "{mac}"];

    public override NodeKind Kind => NodeKind.NetworkSettings;

    public override string Name => "Network settings";

    public override string TaskPluginId => NetworkTaskId;

    public override object DefaultSettings => new NetworkSettingsSettings("{model}-{serial}", 1, [], null);

    protected override string? CheckSettings(NetworkSettingsSettings settings, ProfileCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var pattern = settings.HostName?.Trim() ?? string.Empty;
        var dns = Dns(settings);
        var domain = settings.SearchDomain?.Trim() ?? string.Empty;
        if (pattern.Length == 0 && dns.Count == 0 && domain.Length == 0)
        {
            return "Enter a host name or DNS servers.";
        }

        if (pattern.Length > 0)
        {
            if (!Unique.Any(u => pattern.Contains(u, StringComparison.OrdinalIgnoreCase)))
            {
                return "Use {n}, {serial} or {mac} in the host name, else every camera gets the same name.";
            }

            if (settings.StartNumber is < 0 or > 99_999)
            {
                return "The first number is 0 to 99999.";
            }

            // The longest a name gets: a long model, the serial and a five-digit number.
            var sample = Render(pattern, "M5000-G-ABCDE", "ACCC8E012345", 99_999);
            if (sample is null || !NodeRules.IsHostName(sample))
            {
                return "Use letters, digits, hyphens and {model}, {serial}, {mac}, {n}: at most 63 characters.";
            }
        }

        if (dns.Count > MaxDns)
        {
            return $"Enter at most {MaxDns} DNS servers.";
        }

        if (dns.FirstOrDefault(d => !Ipv4.TryParse(d, out _)) is { } bad)
        {
            return $"{bad} is not an IPv4 address.";
        }

        if (domain.Length > 0)
        {
            if (!NodeRules.IsDomainName(domain))
            {
                return $"{domain} is not a domain name.";
            }

            if (dns.Count == 0)
            {
                return "Enter the DNS servers for the search domain.";
            }
        }

        return null;
    }

    protected override async Task<string> PayloadAsync(NetworkSettingsSettings settings, NodeRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(run);
        var payload = new JsonObject();
        if (await HostNameAsync(settings, run, ct).ConfigureAwait(false) is { } name)
        {
            payload["hostName"] = new JsonObject { ["useDhcp"] = false };
            payload["devices"] = new JsonObject { [run.Camera.DeviceId!.Value.ToString()] = new JsonObject { ["hostName"] = name } };
        }

        var dns = Dns(settings);
        if (dns.Count > 0)
        {
            var change = new JsonObject { ["useDhcp"] = false, ["servers"] = new JsonArray([.. dns.Select(d => JsonValue.Create(d))]) };
            if (settings.SearchDomain?.Trim() is { Length: > 0 } domain)
            {
                change["searchDomains"] = new JsonArray(JsonValue.Create(domain));
            }
            else
            {
                change["keepDomains"] = true;
            }

            payload["dns"] = change;
        }

        return payload.ToJsonString();
    }

    protected override string Payload(NetworkSettingsSettings settings, NodeRun run) =>
        throw new NotSupportedException("The host name needs the camera's number.");

    protected override string Done(NetworkSettingsSettings settings, NodeRun run)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(run);
        var parts = new List<string>();
        if (run.Detail is { } name)
        {
            parts.Add("Host name " + name);
        }

        var dns = Dns(settings);
        if (dns.Count > 0)
        {
            parts.Add("DNS " + string.Join(", ", dns) + (settings.SearchDomain?.Trim() is { Length: > 0 } domain ? ", search domain " + domain : string.Empty));
        }

        return string.Join(", ", parts) + ".";
    }

    /// <summary>The camera's host name of the pattern, or null without a pattern. Throws when it is no host name.</summary>
    private static async Task<string?> HostNameAsync(NetworkSettingsSettings settings, NodeRun run, CancellationToken ct)
    {
        var pattern = settings.HostName?.Trim() ?? string.Empty;
        if (pattern.Length == 0)
        {
            return null;
        }

        var number = settings.StartNumber + await run.Host.NumberAsync(run.Camera.Mac, ct).ConfigureAwait(false);
        var model = run.Camera.Model ?? (run.Camera.DeviceId is { } id ? (await run.Host.Devices.FindAsync(id, ct).ConfigureAwait(false))?.Model : null);
        var name = Render(pattern, model, run.Camera.Serial, number);
        if (name is null || !NodeRules.IsHostName(name))
        {
            throw new InvalidOperationException($"{name ?? pattern} is not a host name (letters, digits and hyphens, at most 63 characters).");
        }

        run.Detail = name;
        return name;
    }

    /// <summary>The pattern with its placeholders filled in; null for an unknown placeholder.</summary>
    public static string? Render(string pattern, string? model, string serial, int number)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var text = pattern
            .Replace("{model}", NodeRules.ModelPart(model), StringComparison.OrdinalIgnoreCase)
            .Replace("{serial}", serial, StringComparison.OrdinalIgnoreCase)
            .Replace("{mac}", serial, StringComparison.OrdinalIgnoreCase)
            .Replace("{n}", number.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        return text.Contains('{', StringComparison.Ordinal) || text.Contains('}', StringComparison.Ordinal) ? null : text;
    }

    private static List<string> Dns(NetworkSettingsSettings settings) =>
        [.. (settings.DnsServers ?? []).Select(d => d?.Trim() ?? string.Empty).Where(d => d.Length > 0)];
}
