using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Oadm.Sdk.Tasks;

using Oadm.Plugins.Provisioning.Model;

namespace Oadm.Plugins.Provisioning.Nodes;

/// <summary>
/// Date and time (spec node 6), the worked example of a task-backed node: the time zone and the NTP servers (none: the
/// servers DHCP gives; NTS where chosen), written by the Date and time task (<c>oadm.datetime</c>) with its checks. With
/// OADM's NTP server the NTP server plugin's task "Use OADM NTP server" (<c>oadm.ntp-server.use</c>) runs after it and fills
/// in OADM's address towards the camera.
/// </summary>
public sealed partial class DateTimeNode : TaskNode<DateTimeSettings>
{
    /// <summary>Most NTP servers a camera takes (the Date and time task's limit).</summary>
    public const int MaxServers = 5;

    public override NodeKind Kind => NodeKind.DateTime;

    public override string Name => "Date and time";

    public override string StepName => "Set the date and time";

    public override string TaskPluginId => "oadm.datetime";

    /// <summary>The NTP server plugin's task that sets OADM's address towards the camera as its NTP server.</summary>
    public const string OadmNtpTaskId = "oadm.ntp-server.use";

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
        if (settings.OadmNtp)
        {
            return servers.Count > 0 ? "Choose OADM's NTP server or enter NTP servers, not both."
                : settings.Nts ? "OADM's NTP server has no NTS." : null;
        }

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
        return $"Time zone {settings.TimeZone!.Trim()}, "
            + (settings.OadmNtp ? "OADM's NTP server." : servers.Count == 0 ? "NTP servers from DHCP." : (settings.Nts ? "NTS " : "NTP ") + string.Join(", ", servers) + ".");
    }

    /// <summary>OADM's NTP server: after the time zone, the NTP server plugin's task sets OADM's address as the NTP server.</summary>
    protected override async Task<NodeResult> AfterAsync(DateTimeSettings settings, NodeRun run, Guid deviceId, NodeResult result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(result);
        if (!settings.OadmNtp || result.State == NodeState.Error)
        {
            return result;
        }

        var outcome = await TaskRuns.RunTaskAsync(run.Host, OadmNtpTaskId, deviceId, "{}", ct).ConfigureAwait(false);
        var message = string.IsNullOrWhiteSpace(outcome?.Message) ? null : outcome!.Message!.Trim();
        return outcome?.State switch
        {
            null => NodeResult.Error(NodeTexts.TaskLost(Name)),
            TaskEndState.Done => result,
            TaskEndState.DoneWithWarnings => NodeResult.Warning(message ?? result.Message),
            _ => NodeResult.Error($"Time zone {settings.TimeZone!.Trim()} set. OADM's NTP server was not set: " + (message is null ? "the task failed." : NodeTexts.Plain(message).TrimEnd('.') + ".")),
        };
    }

    private static List<string> Servers(DateTimeSettings settings) =>
        [.. (settings.NtpServers ?? []).Select(s => s?.Trim() ?? string.Empty).Where(s => s.Length > 0)];
}
