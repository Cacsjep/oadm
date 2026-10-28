using System.Globalization;

using Oadm.Contracts.V1;

namespace Oadm.Client.Devices;

/// <summary>
/// The Clock column (docs/spec/main-window.md "Clock"): the device clock against the server's: "OK", the offset
/// ("50 s ahead", "2 min behind", "3 h ahead", "4 days behind") or "NTP not synced"; a warning chip while off; empty until
/// measured. Rounding: seconds round up (a clock 2.1 s off reads "3 s", so an off clock never reads as within the allowed
/// difference), minutes, hours and days are whole units rounded down ("at least"). Below 1 s the cell says "OK".
/// </summary>
public static class DeviceClockText
{
    public const string Ok = "OK"; // the word of the OK status (DeviceStatusInfo)
    public const string NtpNotSynced = "NTP not synced";

    /// <summary>
    /// The cell: empty until measured; the offset while it is beyond the allowed difference, else "NTP not synced" while
    /// the device's NTP is not synchronized, else "OK". A warning chip only while the server counts the clock as off (a
    /// device that is not Ok shows its last measured offset as plain text).
    /// </summary>
    public static ChipInfo ToChip(int? offsetMs, NtpState ntp, bool clockOff, bool beyondTolerance)
    {
        if (offsetMs is not { } offset)
        {
            return ChipInfo.Empty;
        }

        string text = beyondTolerance ? OffsetText(offset) ?? Ok
            : ntp == NtpState.NotSynced ? NtpNotSynced
            : Ok;
        return new ChipInfo(text, clockOff ? PillKind.Warning : PillKind.Ok);
    }

    /// <summary>
    /// "50 s ahead", "1 min behind", "3 h ahead", "1 day behind"; null below a second (a clock that close needs no number).
    /// Positive = the device clock is ahead of the server's.
    /// </summary>
    public static string? OffsetText(long offsetMs)
    {
        long abs = Math.Abs(offsetMs);
        if (abs < 1000)
        {
            return null;
        }

        long seconds = (abs + 999) / 1000; // rounded up
        string amount = seconds switch
        {
            < 60 => Format($"{seconds} s"),
            < 3600 => Format($"{seconds / 60} min"),
            < 86400 => Format($"{seconds / 3600} h"),
            < 2 * 86400 => "1 day",
            _ => Format($"{seconds / 86400} days"),
        };
        return amount + (offsetMs > 0 ? " ahead" : " behind");

        static string Format(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The cell's tooltip: the offset and the NTP state ("50 s ahead, NTP not synced"); null until measured.</summary>
    public static string? Tooltip(int? offsetMs, NtpState ntp)
    {
        if (offsetMs is not { } offset)
        {
            return null;
        }

        string clock = OffsetText(offset) ?? "Within 1 s of the server";
        string? state = ntp switch
        {
            NtpState.Synced => "NTP synced",
            NtpState.NotSynced => NtpNotSynced,
            NtpState.Off => "NTP off",
            _ => null,
        };
        return state is null ? clock : clock + ", " + state;
    }

    /// <summary>Sort key: the absolute offset; unmeasured devices last.</summary>
    public static long SortKey(int? offsetMs) => offsetMs is { } offset ? Math.Abs((long)offset) : long.MaxValue;
}
