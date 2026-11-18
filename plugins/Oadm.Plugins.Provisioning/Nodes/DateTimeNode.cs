using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Oadm.Plugins.Provisioning.Model;

namespace Oadm.Plugins.Provisioning.Nodes;

/// <summary>
/// Date and time (spec node 6), the worked example of a task-backed node: the time zone and the NTP servers (none: the
/// servers DHCP gives; NTS where chosen), written by the Date and time task (<c>oadm.datetime</c>) with its checks.
/// </summary>
public sealed partial class DateTimeNode : TaskNode<DateTimeSettings>
{
    /// <summary>Most NTP servers a camera takes (the Date and time task's limit).</summary>
    public const int MaxServers = 5;

    public override NodeKind Kind => NodeKind.DateTime;

    public override string Name => "Date and time";

    public override string TaskPluginId => "oadm.datetime";

    public override object DefaultSettings => new DateTimeSettings(NtpServers: []);

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_+\-]*(/[A-Za-z0-9_+\-]+){0,2}$")]
    private static partial Regex ZoneId();

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9\-]{0,61}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9\-]{0,61}[A-Za-z0-9])?)*$")]
    private static partial Regex HostName();

    protected override string? CheckSettings(DateTimeSettings settings, ProfileCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var zone = settings.TimeZone?.Trim() ?? string.Empty;
        if (zone.Length == 0)
        {
            return "Choose a time zone.";
        }

        if (zone.Length > 64 || !ZoneId().IsMatch(zone))
        {
            return $"{zone} is not a time zone.";
        }

        var servers = Servers(settings);
        if (servers.Count > MaxServers)
        {
            return $"Enter at most {MaxServers} NTP servers.";
        }

        if (servers.FirstOrDefault(s => s.Length > 253 || !HostName().IsMatch(s)) is { } bad)
        {
            return $"{bad} is not a host name or IP address.";
        }

        return settings.Nts && servers.Count == 0 ? "Enter the NTS servers." : null;
    }

    protected override string Payload(DateTimeSettings settings, NodeRun run)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var servers = Servers(settings);
        return new JsonObject
        {
            ["timeZone"] = settings.TimeZone!.Trim(),
            ["mode"] = "Ntp",
            ["ntp"] = new JsonObject
            {
                ["source"] = servers.Count == 0 ? "Dhcp" : "Static",
                ["servers"] = new JsonArray([.. servers.Select(s => JsonValue.Create(s))]),
                ["nts"] = settings.Nts,
            },
            ["daylightSaving"] = true,
        }.ToJsonString();
    }

    protected override string Done(DateTimeSettings settings, NodeRun run)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var servers = Servers(settings);
        return $"Time zone {settings.TimeZone!.Trim()}, " + (servers.Count == 0 ? "NTP servers from DHCP." : (settings.Nts ? "NTS " : "NTP ") + string.Join(", ", servers) + ".");
    }

    private static List<string> Servers(DateTimeSettings settings) =>
        [.. (settings.NtpServers ?? []).Select(s => s?.Trim() ?? string.Empty).Where(s => s.Length > 0)];
}
