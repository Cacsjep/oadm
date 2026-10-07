using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oadm.Plugins.DateAndTime.Model;

/// <summary>
/// How the devices get their time (ADM "Time mode"). Like ADM there is no "keep": OK always writes the mode. A payload
/// without a mode (0) is invalid.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TimeMode>))]
public enum TimeMode
{
    /// <summary>"Synchronize with NTP server": the NTP client on, servers from DHCP or the list.</summary>
    Ntp = 1,

    /// <summary>"Synchronize with server computer time": NTP off, the OADM server's UTC time is sent per device at execution time.</summary>
    ServerTime = 2,

    /// <summary>"Set manually": NTP off, the entered date and time (device local time).</summary>
    Manual = 3,
}

/// <summary>Where the NTP client gets its servers.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NtpSource>))]
public enum NtpSource
{
    /// <summary>NTP servers advertised by DHCP (option 42).</summary>
    Dhcp = 0,

    /// <summary>The <see cref="NtpSettings.Servers"/> list.</summary>
    Static = 1,
}

/// <summary>NTP settings of <see cref="TimeMode.Ntp"/>.</summary>
/// <param name="Source">DHCP or the manual list.</param>
/// <param name="Servers">Host names or IP addresses (manual NTP servers; NTS-KE servers when <paramref name="Nts"/>).</param>
/// <param name="Nts">Network Time Security: the servers are NTS-KE servers (ntp 1.5 or later, AXIS OS 11.x+).</param>
public sealed record NtpSettings(NtpSource Source, IReadOnlyList<string> Servers, bool Nts = false);

/// <summary>
/// Payload of the "Date and time" task. Exactly like ADM, OK always writes the time zone and the time mode to every
/// device (no optional sections); the steps skip values a device already has ("Already ..."). Never contains secrets.
/// </summary>
/// <param name="TimeZone">IANA id ("Europe/Vienna"); required except in <see cref="TimeMode.ServerTime"/>, where the OADM server's zone is used.</param>
/// <param name="Mode">Time mode, always written.</param>
/// <param name="Ntp">NTP settings when <paramref name="Mode"/> is <see cref="TimeMode.Ntp"/>.</param>
/// <param name="ManualDateTime">Local date and time of the device ("yyyy-MM-ddTHH:mm:ss") when <paramref name="Mode"/> is <see cref="TimeMode.Manual"/>.</param>
/// <param name="DaylightSaving">
/// "Automatically adjust for daylight saving time changes" for <paramref name="TimeZone"/>: true sets the IANA zone, false sets
/// its standard-time POSIX rule with daylight saving off (setPosixTimeZone enableDst=false).
/// </param>
/// <param name="TimeZoneUnchanged">
/// Name hint from the dialog: the time zone equals what the (single) device already has, so the task is named after
/// the time mode ("Set NTP servers 10.0.0.17") instead of "Change date and time". Changes nothing that is written.
/// </param>
public sealed record DateTimePayload(
    string? TimeZone,
    TimeMode Mode,
    NtpSettings? Ntp = null,
    string? ManualDateTime = null,
    bool DaylightSaving = true,
    bool TimeZoneUnchanged = false)
{
    public const string ManualFormat = "yyyy-MM-ddTHH:mm:ss";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Parses the payload; throws <see cref="ArgumentException"/> ("Nothing was changed") for missing or unreadable JSON.</summary>
    public static DateTimePayload Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("The task has no date and time settings. Nothing was changed.");
        }

        try
        {
            return JsonSerializer.Deserialize<DateTimePayload>(json, JsonOptions)
                ?? throw new ArgumentException("The date and time settings are empty. Nothing was changed.");
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The date and time settings cannot be read: " + ex.Message + " Nothing was changed.", ex);
        }
    }

    /// <summary>The manual date and time as a wall-clock value, or null when it does not parse.</summary>
    public System.DateTime? ParsedManualDateTime =>
        System.DateTime.TryParseExact(ManualDateTime, ManualFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value
            : null;
}
