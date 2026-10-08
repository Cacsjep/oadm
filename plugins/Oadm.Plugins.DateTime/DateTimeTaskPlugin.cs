using System.Globalization;

using Oadm.Plugins.DateAndTime.Model;
using Oadm.Plugins.DateAndTime.Vapix;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.DateAndTime;

/// <summary>
/// "Date and time" task: a clone of the ADM / AXIS Camera Station "Set date and time" dialog. Time zone (IANA,
/// "Automatically adjust for daylight saving time changes"), time mode (Synchronize with server computer time,
/// Synchronize with NTP server: from DHCP or manual servers incl. NTS, Set manually) for any number of devices.
/// Compatibility is checked on the cached API list in <see cref="CanRun"/> and per method on a fresh list before the
/// first write. Like ADM, the time zone and the time mode are always written; values a device already has are skipped
/// (see <see cref="DateTimeTaskRunner"/> for the steps).
/// </summary>
public sealed class DateTimeTaskPlugin : ITaskPlugin, ITaskPluginQuery
{
    public const string PluginId = "oadm.datetime";
    public const string QueryGetTimeSettings = "getTimeSettings";

    /// <summary>Longest task name (tasks pane column); longer server lists are shortened with "+n".</summary>
    internal const int MaxTaskNameLength = 100;

    private readonly DateTimeTaskRunner _runner;
    private readonly TimeProvider _time;
    private readonly Func<TimeZoneInfo> _serverZone;

    public DateTimeTaskPlugin()
        : this(TimeProvider.System, () => TimeZoneInfo.Local)
    {
    }

    public DateTimeTaskPlugin(TimeProvider time, Func<TimeZoneInfo> serverZone)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(serverZone);
        _time = time;
        _serverZone = serverZone;
        _runner = new DateTimeTaskRunner(time, serverZone);
    }

    public string Id => PluginId;

    public string DisplayName => "Date and time";

    public string Group => TaskGroups.Maintenance;

    public string? IconKey => "clock";

    public bool ShowInToolbar => false;

    public bool RequiresDialog => true;

    /// <summary>Working credentials and time-service 1.0 or param.cgi in the cached API list.</summary>
    public bool CanRun(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Status is DeviceStatus.Ok or DeviceStatus.Unknown && TimeApis.CanConfigure(device.Apis);
    }

    /// <summary>Plain-language reason for the greyed menu entry when <see cref="CanRun"/> is false (cached data only).</summary>
    public string? NotSupportedReason(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return TaskSupportReasons.ForStatus(device.Status) ?? TaskSupportReasons.NeedsApi("the Time API", device);
    }

    public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);

        // Validate the input before touching the device.
        var payload = DateTimePayload.Parse(payloadJson);
        PayloadValidator.ThrowIfInvalid(payload, int.MaxValue);
        await _runner.RunAsync(ctx, device, payload, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Read-only: "getTimeSettings" returns <see cref="CurrentTimeSettings"/> of the device (getDateTimeInfo + getNTPInfo,
    /// or param.cgi Time) plus the OADM server's UTC time and time zone, for the dialog's "Device time" card.
    /// </summary>
    public async Task<string?> QueryAsync(ITaskQueryContext ctx, IDeviceInfo device, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);
        if (!string.Equals(method, QueryGetTimeSettings, StringComparison.Ordinal))
        {
            throw new NotSupportedException($"Unknown query \"{method}\".");
        }

        var apis = await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false);
        var current = await TimeClient.ReadAllAsync(ctx.Vapix, apis, _time, ct).ConfigureAwait(false);
        var zone = _serverZone();
        return (current with
        {
            ServerUtc = current.ServerUtc ?? _time.GetUtcNow(),
            ServerTimeZone = ServerTimeZone.Resolve(zone),
            ServerTimeZoneName = zone.DisplayName,
        }).ToJson();
    }

    /// <summary>
    /// Exact task name for the tasks pane: "Change date and time" (time zone and time mode are written), or the most
    /// specific name when only the time mode differs from the device (<see cref="DateTimePayload.TimeZoneUnchanged"/>):
    /// "Set NTP servers 10.0.0.17, pool.ntp.org", "Set NTP servers from DHCP", "Set NTS KE servers ...", "Set date and
    /// time 2026-10-07 18:00"; "Sync with server time" for server time mode. Falls back to <see cref="DisplayName"/>
    /// for an unreadable payload.
    /// </summary>
    public string GetTaskName(string? payloadJson)
    {
        DateTimePayload payload;
        try
        {
            payload = DateTimePayload.Parse(payloadJson);
        }
        catch (ArgumentException)
        {
            return DisplayName;
        }

        var name = TaskName(payload) ?? DisplayName;
        return name.Length <= MaxTaskNameLength ? name : name[..(MaxTaskNameLength - 1)] + "…";
    }

    internal static string? TaskName(DateTimePayload payload)
    {
        if (payload.Mode == TimeMode.ServerTime)
        {
            return "Sync with server time";
        }

        if (!payload.TimeZoneUnchanged)
        {
            return "Change date and time";
        }

        return payload.Mode switch
        {
            TimeMode.Ntp when payload.Ntp is { Nts: true } nts => "Set NTS KE servers " + ServerList(nts.Servers),
            TimeMode.Ntp when payload.Ntp is { Source: NtpSource.Dhcp } => "Set NTP servers from DHCP",
            TimeMode.Ntp when payload.Ntp is { } ntp => "Set NTP servers " + ServerList(ntp.Servers),
            TimeMode.Manual when payload.ParsedManualDateTime is { } manual => "Set date and time " + manual.ToString(
                manual.Second == 0 ? "yyyy-MM-dd HH:mm" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            _ => "Change date and time",
        };
    }

    /// <summary>"a, b, c" or "a, b +3" so the name stays short.</summary>
    private static string ServerList(IReadOnlyList<string> servers)
    {
        var list = servers.Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        var shown = list.Take(3).ToList();
        var text = string.Join(", ", shown);
        return list.Count > shown.Count ? string.Create(CultureInfo.InvariantCulture, $"{text} +{list.Count - shown.Count}") : text;
    }
}
