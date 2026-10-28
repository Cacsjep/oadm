using System.Globalization;

namespace Oadm.Sdk.Devices;

/// <summary>
/// The shared texts of a device clock against the server's (docs/spec/main-window.md "Clock"): the client's Clock column,
/// the Clock compliance check and every plugin say the same thing.
/// </summary>
public static class ClockTexts
{
    /// <summary>A clock less than a second off (the word of the OK status).</summary>
    public const string Ok = "OK";

    /// <summary>The device's NTP client is on but not synchronized.</summary>
    public const string NtpNotSynced = "NTP not synced";

    /// <summary>The device does not use NTP.</summary>
    public const string NtpOff = "NTP off";

    /// <summary>True below one second either way: the offset reads <see cref="Ok"/>, no number.</summary>
    public static bool IsWithinOneSecond(long offsetMs) => offsetMs is > -1000 and < 1000;

    /// <summary>
    /// <see cref="Ok"/> below one second, else "50 s ahead", "2 min behind", "3 h ahead", "1 day behind", "4 days behind":
    /// seconds rounded up (an off clock never reads as within the allowed difference), the larger units rounded down.
    /// Positive = the device clock is ahead of the server's.
    /// </summary>
    public static string OffsetText(long offsetMs)
    {
        if (IsWithinOneSecond(offsetMs))
        {
            return Ok;
        }

        // |long.MinValue| does not fit a long; an offset in ms that large is far beyond any clock anyway.
        var abs = offsetMs == long.MinValue ? long.MaxValue : Math.Abs(offsetMs);
        var seconds = (abs / 1000) + (abs % 1000 == 0 ? 0 : 1); // rounded up
        var amount = seconds switch
        {
            < 60 => string.Create(CultureInfo.InvariantCulture, $"{seconds} s"),
            < 3600 => string.Create(CultureInfo.InvariantCulture, $"{seconds / 60} min"),
            < 86_400 => string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600} h"),
            < 172_800 => "1 day",
            _ => string.Create(CultureInfo.InvariantCulture, $"{seconds / 86_400} days"),
        };
        return amount + (offsetMs > 0 ? " ahead" : " behind");
    }
}
