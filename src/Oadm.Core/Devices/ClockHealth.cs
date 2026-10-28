using System.Text.Json;

using Oadm.Sdk.Devices;

namespace Oadm.Core.Devices;

/// <summary>What the device's NTP client reports (<c>ntp.cgi getNTPInfo</c>, read in the full refresh).</summary>
public enum NtpState
{
    /// <summary>Not read yet, no NTP API, or the read failed.</summary>
    Unknown = 0,
    Synced = 1,

    /// <summary>NTP is on but not synchronized.</summary>
    NotSynced = 2,

    /// <summary>The device does not use NTP.</summary>
    Off = 3,
}

/// <summary>
/// Clock health of a device (docs/spec/main-window.md "Clock"): the offset from the HTTP <c>Date</c> header of the status
/// poll, the NTP state from the full refresh, and whether the device counts as off. Pure functions.
/// </summary>
public static class ClockHealth
{
    /// <summary>The NTP API of the API list (any 1.x).</summary>
    public const string NtpApiId = "ntp";

    public const string NtpPath = "axis-cgi/ntp.cgi";

    /// <summary>
    /// A new reading that differs less than this from the stored offset keeps the stored one (not written, not published):
    /// the header has whole seconds, so readings jitter by up to a second, and 30,000 devices would otherwise publish
    /// every poll.
    /// </summary>
    public const int MinChangeMs = 1000;

    /// <summary>
    /// <c>device + 0.5 s - (sent + received) / 2</c> in milliseconds (the half second: the header cuts the fraction off),
    /// clamped to the range of an int (about 24.8 days); null without a header.
    /// </summary>
    public static int? Offset(DateTimeOffset? deviceDate, DateTimeOffset sent, DateTimeOffset received)
    {
        if (deviceDate is not { } date)
        {
            return null;
        }

        var middle = sent + ((received - sent) / 2);
        var ms = Math.Round((date + TimeSpan.FromMilliseconds(500) - middle).TotalMilliseconds);
        return (int)Math.Clamp(ms, -int.MaxValue, int.MaxValue);
    }

    /// <summary>The stored offset after a reading: a change below <see cref="MinChangeMs"/> keeps the stored value.</summary>
    public static int? Next(int? stored, int? measured) =>
        measured is not { } value ? stored
        : stored is { } old && Math.Abs((long)value - old) < MinChangeMs ? old
        : value;

    /// <summary>
    /// Off: status Ok and (|offset| above the tolerance or NTP not synced). Devices that are not Ok are never off.
    /// </summary>
    public static bool IsOff(DeviceStatus status, int? offsetMs, NtpState ntp, int toleranceSeconds) =>
        status == DeviceStatus.Ok && (IsBeyondTolerance(offsetMs, toleranceSeconds) || ntp == NtpState.NotSynced);

    /// <summary>|offset| above the tolerance whatever the status (false while unmeasured): proto clock_beyond_tolerance.</summary>
    public static bool IsBeyondTolerance(int? offsetMs, int toleranceSeconds) =>
        offsetMs is { } offset && Math.Abs((long)offset) > toleranceSeconds * 1000L;

    public static bool IsBeyondTolerance(Device device, int toleranceSeconds)
    {
        ArgumentNullException.ThrowIfNull(device);
        return IsBeyondTolerance(device.ClockOffsetMs, toleranceSeconds);
    }

    public static bool IsOff(Device device, int toleranceSeconds)
    {
        ArgumentNullException.ThrowIfNull(device);
        return IsOff(device.Status, device.ClockOffsetMs, device.NtpState, toleranceSeconds);
    }

    /// <summary>The <c>getNTPInfo</c> request body for the device's API version.</summary>
    public static string NtpInfoRequest(string apiVersion) =>
        JsonSerializer.Serialize(new { apiVersion, context = "oadm", method = "getNTPInfo" });

    /// <summary>
    /// <c>getNTPInfo</c> answer -> state: <c>data.client.enabled</c> false = Off, else <c>synced</c> true = Synced, false =
    /// NotSynced; an error answer or missing fields Unknown. Throws <see cref="JsonException"/> for an answer that is not JSON.
    /// </summary>
    public static NtpState ParseNtpInfo(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _)
            || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            return NtpState.Unknown;
        }

        var client = data.TryGetProperty("client", out var c) && c.ValueKind == JsonValueKind.Object ? c : data;
        var enabled = Bool(client, "enabled");
        if (enabled == false)
        {
            return NtpState.Off;
        }

        return (enabled, Bool(client, "synced")) switch
        {
            (true, true) => NtpState.Synced,
            (true, false) => NtpState.NotSynced,
            _ => NtpState.Unknown,
        };
    }

    private static bool? Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
}
