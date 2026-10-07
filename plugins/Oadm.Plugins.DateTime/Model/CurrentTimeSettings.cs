using System.Text.Json;

namespace Oadm.Plugins.DateAndTime.Model;

/// <summary>
/// Time settings read from one device (read-only; the dialog's "Current device time" card and the task's read step).
/// Null values were not reported by the API in use.
/// </summary>
public sealed record CurrentTimeSettings
{
    /// <summary>"time-service 1.1, ntp 1.5" or "param.cgi".</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Device time in UTC at the moment of the read.</summary>
    public DateTimeOffset? DeviceUtc { get; init; }

    /// <summary>Device local time with its offset ("2026-10-07T18:24:01+02:00").</summary>
    public DateTimeOffset? DeviceLocal { get; init; }

    /// <summary>OADM server UTC time when the answer arrived (difference to <see cref="DeviceUtc"/>).</summary>
    public DateTimeOffset? ServerUtc { get; init; }

    /// <summary>IANA id of the OADM server's time zone ("Synchronize with server computer time"), null when AXIS devices do not know it.</summary>
    public string? ServerTimeZone { get; init; }

    /// <summary>The server OS name of its time zone ("(UTC+01:00) Amsterdam, Berlin, ...").</summary>
    public string? ServerTimeZoneName { get; init; }

    /// <summary>IANA time zone when one is set, null for a POSIX (custom) or DHCP time zone.</summary>
    public string? TimeZone { get; init; }

    public string? PosixTimeZone { get; init; }

    public bool? DstEnabled { get; init; }

    /// <summary>Latest year the device accepts (time-service <c>maxYearSupported</c>).</summary>
    public int? MaxYear { get; init; }

    public bool? NtpEnabled { get; init; }

    public NtpSource? NtpSource { get; init; }

    public IReadOnlyList<string> NtpServers { get; init; } = [];

    /// <summary>Servers DHCP advertised (ntp <c>advertisedServers</c>, param <c>Time.NTP.VolatileServer</c>).</summary>
    public IReadOnlyList<string> AdvertisedServers { get; init; } = [];

    public bool? NtsEnabled { get; init; }

    public IReadOnlyList<string> NtsServers { get; init; } = [];

    /// <summary>The NTP client is synchronized.</summary>
    public bool? Synced { get; init; }

    /// <summary>Offset to the NTP server in milliseconds (ntp <c>timeOffset</c>).</summary>
    public double? NtpOffsetMilliseconds { get; init; }

    /// <summary>Most static NTP servers the device takes (ntp <c>maxSupportedStaticServers</c>).</summary>
    public int? MaxStaticServers { get; init; }

    /// <summary>The device supports NTS (ntp API version with NTS fields).</summary>
    public bool SupportsNts { get; init; }

    /// <summary>Device minus server time; positive = device is ahead.</summary>
    public TimeSpan? Difference => DeviceUtc is { } d && ServerUtc is { } s ? d - s : null;

    public string ToJson() => JsonSerializer.Serialize(this, DateTimePayload.JsonOptions);

    public static CurrentTimeSettings FromJson(string json) =>
        JsonSerializer.Deserialize<CurrentTimeSettings>(json, DateTimePayload.JsonOptions)
        ?? throw new JsonException("Empty time settings.");
}
