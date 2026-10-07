using System.Globalization;
using System.Text.Json.Nodes;

using Oadm.Plugins.DateAndTime.Model;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.DateAndTime.Vapix;

public enum SectionKind
{
    TimeZone = 0,
    Ntp = 1,
    DateTime = 2,
}

/// <summary>One write section: its step name, what it does, and its requests (one step per request).</summary>
/// <param name="Kind">Section.</param>
/// <param name="StepName">"Set time zone", "Set NTP configuration", "Turn off NTP", "Set date and time".</param>
/// <param name="Description">"Time zone Europe/Vienna", "NTP servers 10.0.0.17".</param>
/// <param name="Requests">Requests in order; empty for <see cref="SectionKind.DateTime"/> (built when it is sent).</param>
/// <param name="SkipReason">Non-null: nothing to write ("Already Europe/Vienna").</param>
/// <param name="SkipIsWarning">The section cannot be written although the user asked for it: the step ends as Warning.</param>
public sealed record PlannedSection(SectionKind Kind, string StepName, string Description, IReadOnlyList<TimeRequest> Requests, string? SkipReason = null, bool SkipIsWarning = false);

/// <summary>What the device should report afterwards (verify steps).</summary>
public sealed record ExpectedState
{
    public string? TimeZone { get; init; }

    public string? PosixTimeZone { get; init; }

    public bool? DstEnabled { get; init; }

    public bool? NtpEnabled { get; init; }

    public NtpSource? NtpSource { get; init; }

    public IReadOnlyList<string>? NtpServers { get; init; }

    public bool? NtsEnabled { get; init; }
}

/// <summary>The writes for one device, decided from the payload, the fresh API list and the current settings.</summary>
public sealed record TimePlan(IReadOnlyList<PlannedSection> Sections, ExpectedState Expected)
{
    /// <summary>Manual mode: the UTC time to set (wall time converted in the device's target time zone).</summary>
    public DateTimeOffset? ManualUtc { get; init; }

    /// <summary>Server time mode: send the OADM server's UTC time at the moment of the request.</summary>
    public bool UseServerTime { get; init; }

    /// <summary>API version for setDateTime.</summary>
    public string? TimeServiceVersion { get; init; }

    /// <summary>Sections that write something.</summary>
    public IEnumerable<PlannedSection> Writes => Sections.Where(s => s.SkipReason is null);

    public PlannedSection? Find(SectionKind kind) => Sections.FirstOrDefault(s => s.Kind == kind);

    /// <summary>setDateTime for <paramref name="utc"/> (seconds precision, "Z").</summary>
    public TimeRequest DateTimeRequest(DateTimeOffset utc)
    {
        var version = TimeServiceVersion ?? throw new InvalidOperationException("The device has no Time API.");
        return JsonMethodRequest.Time(version, "setDateTime", new JsonObject { ["dateTime"] = FormatUtc(utc) });
    }

    public static string FormatUtc(DateTimeOffset utc) => utc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

/// <summary>
/// Builds the per-device plan. All <c>Require(...)</c> checks of the methods the plan uses happen here (Validate
/// step), so an unsupported method or a value the device cannot take fails before the first write with
/// "Nothing was changed".
/// </summary>
public static class TimePlanner
{
    public const string StepSetTimeZone = "Set time zone";
    public const string StepSetNtp = "Set NTP configuration";
    public const string StepTurnOffNtp = "Turn off NTP";
    public const string StepSetDateTime = "Set date and time";

    /// <param name="payload">Validated payload.</param>
    /// <param name="apis">Fresh API list.</param>
    /// <param name="current">Settings read from the device.</param>
    /// <param name="serverTimeZone">IANA id of the OADM server's time zone (server time mode), null when unknown.</param>
    /// <param name="year">Year of the POSIX rule (current year).</param>
    public static TimePlan Build(DateTimePayload payload, IReadOnlyList<DeviceApi> apis, CurrentTimeSettings current, string? serverTimeZone, int year)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(apis);
        ArgumentNullException.ThrowIfNull(current);
        PayloadValidator.ThrowIfInvalid(payload, current.MaxYear ?? PayloadValidator.DefaultMaxYear);

        var sections = new List<PlannedSection>();
        var expected = new ExpectedState();
        var hasTimeService = TimeApis.HasTimeService(apis);
        var timeVersion = hasTimeService ? apis.Require(TimeApis.TimeService, TimeApis.TimeServiceBase).Version : null;

        // Time zone (always, like ADM): the chosen one, or the server's in server time mode.
        var zone = payload.Mode == TimeMode.ServerTime ? serverTimeZone : payload.TimeZone;
        var dst = payload.Mode == TimeMode.ServerTime || payload.DaylightSaving;
        if (payload.TimeZoneUnchanged && payload.Mode != TimeMode.ServerTime)
        {
            // Callers that change only the time source (e.g. "Use OADM as NTP server") keep the device's zone.
            sections.Add(new(SectionKind.TimeZone, StepSetTimeZone, "Time zone", [], "Time zone unchanged"));
        }
        else
        {
            if (zone is null || !TimeZoneCatalog.IsKnown(zone))
            {
                sections.Add(new(SectionKind.TimeZone, StepSetTimeZone, "Time zone", [],
                    "The OADM server's time zone is not an AXIS time zone; the device keeps its time zone.", SkipIsWarning: true));
            }
            else
            {
                var (section, state) = PlanTimeZone(zone, dst, apis, current, timeVersion, year);
                sections.Add(section);
                expected = expected with { TimeZone = state.TimeZone, PosixTimeZone = state.PosixTimeZone, DstEnabled = state.DstEnabled };
            }
        }

        // Time mode (always): NTP on with its servers, or off for server time and manual.
        {
            var (section, state) = PlanNtp(payload, apis, current);
            sections.Add(section);
            expected = expected with { NtpEnabled = state.NtpEnabled, NtpSource = state.NtpSource, NtpServers = state.NtpServers, NtsEnabled = state.NtsEnabled };
        }

        DateTimeOffset? manualUtc = null;
        if (payload.Mode is TimeMode.Manual or TimeMode.ServerTime)
        {
            if (!hasTimeService)
            {
                apis.Require(TimeApis.TimeService, TimeApis.TimeServiceBase);
            }

            if (payload.Mode == TimeMode.Manual)
            {
                manualUtc = ToUtc(payload.ParsedManualDateTime!.Value, zone ?? current.TimeZone, dst, current);
                sections.Add(new(SectionKind.DateTime, StepSetDateTime, $"Date and time {payload.ParsedManualDateTime:yyyy-MM-dd HH:mm:ss}", []));
            }
            else
            {
                sections.Add(new(SectionKind.DateTime, StepSetDateTime, "Date and time of the OADM server", []));
            }
        }

        return new TimePlan(sections, expected)
        {
            ManualUtc = manualUtc,
            UseServerTime = payload.Mode == TimeMode.ServerTime,
            TimeServiceVersion = timeVersion,
        };
    }

    private static (PlannedSection Section, ExpectedState State) PlanTimeZone(
        string zone, bool dst, IReadOnlyList<DeviceApi> apis, CurrentTimeSettings current, string? timeVersion, int year)
    {
        var description = dst ? $"Time zone {zone}" : $"Time zone {zone} without daylight saving time";
        if (timeVersion is not null && dst)
        {
            var state = new ExpectedState { TimeZone = zone };
            return string.Equals(current.TimeZone, zone, StringComparison.Ordinal)
                ? (new(SectionKind.TimeZone, StepSetTimeZone, description, [], $"Already {zone}"), state)
                : (new(SectionKind.TimeZone, StepSetTimeZone, description, [JsonMethodRequest.Time(timeVersion, "setTimeZone", new JsonObject { ["timeZone"] = zone })]), state);
        }

        // POSIX: daylight saving off on the Time API, or the legacy parameters.
        var posix = PosixTimeZone.FromIana(zone, year)
            ?? throw new ArgumentException($"The time zone {zone} is not known to this server, so it cannot be converted for this device. Nothing was changed.");
        var expected = new ExpectedState { PosixTimeZone = posix, DstEnabled = dst };
        var same = current.TimeZone is null && string.Equals(current.PosixTimeZone, posix, StringComparison.Ordinal) && current.DstEnabled == dst;
        if (same)
        {
            return (new(SectionKind.TimeZone, StepSetTimeZone, description, [], "Already " + posix + (dst ? string.Empty : ", daylight saving off")), expected);
        }

        if (timeVersion is not null)
        {
            return (new(SectionKind.TimeZone, StepSetTimeZone, description,
                [JsonMethodRequest.Time(timeVersion, "setPosixTimeZone", new JsonObject { ["posixTimeZone"] = posix, ["enableDst"] = dst })]), expected);
        }

        apis.Require(TimeApis.ParamCgi, TimeApis.ParamCgiBase);
        return (new(SectionKind.TimeZone, StepSetTimeZone, description,
            [ParamUpdateRequest.Of(("Time.POSIXTimeZone", posix), ("Time.DST.Enabled", dst ? "yes" : "no"))]), expected);
    }

    private static (PlannedSection Section, ExpectedState State) PlanNtp(DateTimePayload payload, IReadOnlyList<DeviceApi> apis, CurrentTimeSettings current)
    {
        var turnOn = payload.Mode == TimeMode.Ntp;
        var ntp = payload.Ntp;
        var source = turnOn ? ntp!.Source : current.NtpSource ?? NtpSource.Static;
        var nts = turnOn && ntp!.Nts;
        var servers = turnOn && (ntp!.Source == NtpSource.Static || nts)
            ? ntp.Servers.Select(s => s.Trim()).ToList()
            : null;
        var stepName = turnOn ? StepSetNtp : StepTurnOffNtp;
        var description = !turnOn
            ? "NTP off"
            : nts
                ? "NTS KE servers " + string.Join(", ", servers!)
                : source == NtpSource.Dhcp ? "NTP servers from DHCP" : "NTP servers " + string.Join(", ", servers!);

        var expected = new ExpectedState
        {
            NtpEnabled = turnOn,
            NtpSource = turnOn ? (nts ? NtpSource.Static : source) : null,
            NtpServers = turnOn && !nts ? servers : null,
            NtsEnabled = turnOn ? nts : null,
        };

        if (TimeApis.HasNtpApi(apis))
        {
            var api = apis.Require(TimeApis.Ntp, TimeApis.NtpBase);
            if (nts)
            {
                api = apis.Require(TimeApis.Ntp, TimeApis.NtpNts);
                if (!current.SupportsNts)
                {
                    throw new DeviceNotCompatibleException("This device does not report NTS settings, so NTS KE servers cannot be set. Nothing was changed.");
                }
            }

            if (servers is not null && current.MaxStaticServers is { } max && servers.Count > max)
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"This device takes at most {max} NTP servers. Nothing was changed."));
            }

            if (IsSameNtp(expected, current, nts))
            {
                return (new(SectionKind.Ntp, stepName, description, [], "Already set"), expected);
            }

            var parameters = new JsonObject
            {
                ["enabled"] = turnOn,
                ["serversSource"] = (turnOn ? (nts ? NtpSource.Static : source) : current.NtpSource ?? NtpSource.Static) == NtpSource.Dhcp ? "DHCP" : "static",
                ["staticServers"] = ToArray(turnOn && !nts && servers is not null ? servers : current.NtpServers),
            };
            if (current.SupportsNts && TimeApis.SupportsNts(apis))
            {
                parameters["NTSEnabled"] = nts;
                parameters["staticNTSKEServers"] = ToArray(nts ? servers! : current.NtsServers);
            }

            return (new(SectionKind.Ntp, stepName, description, [JsonMethodRequest.Ntp(api.Version, "setNTPClientConfiguration", parameters)]), expected);
        }

        // Legacy param.cgi: one NTP server, no NTS.
        apis.Require(TimeApis.ParamCgi, TimeApis.ParamCgiBase);
        if (nts)
        {
            apis.Require(TimeApis.Ntp, TimeApis.NtpNts);
        }

        if (servers is { Count: > 1 })
        {
            throw new ArgumentException("This device takes one NTP server (no NTP API, param.cgi only). Nothing was changed.");
        }

        if (IsSameNtp(expected, current, nts: false))
        {
            return (new(SectionKind.Ntp, stepName, description, [], "Already set"), expected);
        }

        var values = new List<(string, string)> { ("Time.SyncSource", turnOn ? "NTP" : "None") };
        if (turnOn)
        {
            values.Add(("Time.ObtainFromDHCP", source == NtpSource.Dhcp ? "yes" : "no"));
            if (servers is { Count: 1 })
            {
                values.Add(("Time.NTP.Server", servers[0]));
            }
        }

        return (new(SectionKind.Ntp, stepName, description, [ParamUpdateRequest.Of([.. values])]), expected);
    }

    private static bool IsSameNtp(ExpectedState expected, CurrentTimeSettings current, bool nts)
    {
        if (current.NtpEnabled != expected.NtpEnabled)
        {
            return false;
        }

        if (expected.NtpEnabled == false)
        {
            return true;
        }

        if (current.NtpSource != expected.NtpSource || (current.NtsEnabled ?? false) != nts)
        {
            return false;
        }

        return expected.NtpServers is null || current.NtpServers.SequenceEqual(expected.NtpServers, StringComparer.OrdinalIgnoreCase);
    }

    private static JsonArray ToArray(IEnumerable<string> values) => [.. values.Select(v => (JsonNode)JsonValue.Create(v))];

    /// <summary>
    /// The UTC time for a wall-clock value of the device: in <paramref name="zone"/> (IANA) when known, standard time only
    /// without daylight saving, else with the device's current UTC offset.
    /// </summary>
    public static DateTimeOffset ToUtc(System.DateTime wall, string? zone, bool dst, CurrentTimeSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var unspecified = System.DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        if (zone is not null && TimeZoneCatalog.TryGetSystemZone(zone) is { } info)
        {
            if (!dst)
            {
                return new DateTimeOffset(unspecified, info.BaseUtcOffset).ToUniversalTime();
            }

            if (info.IsInvalidTime(unspecified))
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                    $"{wall:yyyy-MM-dd HH:mm} does not exist in {zone} (clocks change for daylight saving time). Nothing was changed."));
            }

            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, info), TimeSpan.Zero);
        }

        var offset = current.DeviceLocal?.Offset ?? TimeSpan.Zero;
        return new DateTimeOffset(unspecified, offset).ToUniversalTime();
    }
}
