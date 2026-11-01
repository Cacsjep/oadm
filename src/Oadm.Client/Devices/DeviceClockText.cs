using Oadm.Contracts.V1;
using Oadm.Sdk.Devices;

namespace Oadm.Client.Devices;

/// <summary>
/// The Clock column (docs/spec/main-window.md "Clock"): the device clock against the server's: the warning icon alone while
/// the clock is off (beyond the allowed difference or NTP not synced), else "OK"; how far and the NTP state in the tooltip,
/// the export and the search ("50 s ahead", "NTP not synced"); empty until measured. The texts are the shared
/// <see cref="ClockTexts"/> of the Clock compliance check.
/// </summary>
public static class DeviceClockText
{
    /// <summary>
    /// The cell: empty until measured; the warning icon without text while the server counts the clock as off; else
    /// <see cref="Detail"/> as plain text ("OK", or the last measured state of a device that is not Ok).
    /// </summary>
    public static ChipInfo ToChip(int? offsetMs, NtpState ntp, bool clockOff, bool beyondTolerance) => offsetMs is null ? ChipInfo.Empty
        : clockOff ? new ChipInfo("", PillKind.Warning)
        : new ChipInfo(Detail(offsetMs, ntp, beyondTolerance), PillKind.Ok);

    /// <summary>
    /// The export's Clock value: the offset while it is beyond the allowed difference ("50 s ahead"), else "NTP not synced"
    /// or "OK"; empty until measured.
    /// </summary>
    public static string Detail(int? offsetMs, NtpState ntp, bool beyondTolerance) => offsetMs switch
    {
        null => "",
        { } offset when beyondTolerance => ClockTexts.OffsetText(offset),
        _ when ntp == NtpState.NotSynced => ClockTexts.NtpNotSynced,
        _ => ClockTexts.Ok,
    };

    /// <summary>The cell's tooltip: the offset and the NTP state ("50 s ahead, NTP not synced"); null until measured.</summary>
    public static string? Tooltip(int? offsetMs, NtpState ntp)
    {
        if (offsetMs is not { } offset)
        {
            return null;
        }

        string clock = ClockTexts.IsWithinOneSecond(offset) ? "Within 1 s of the server" : ClockTexts.OffsetText(offset);
        string? state = ntp switch
        {
            NtpState.Synced => "NTP synced",
            NtpState.NotSynced => ClockTexts.NtpNotSynced,
            NtpState.Off => ClockTexts.NtpOff,
            _ => null,
        };
        return state is null ? clock : clock + ", " + state;
    }

    /// <summary>Sort key: the absolute offset; unmeasured devices last.</summary>
    public static long SortKey(int? offsetMs) => offsetMs is { } offset ? Math.Abs((long)offset) : long.MaxValue;
}
