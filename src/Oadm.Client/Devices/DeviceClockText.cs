using Oadm.Contracts.V1;
using Oadm.Sdk.Devices;

namespace Oadm.Client.Devices;

/// <summary>
/// The Clock column (docs/spec/main-window.md "Clock"): the device clock against the server's: "OK", the offset
/// ("50 s ahead", "2 min behind", "3 h ahead", "4 days behind") or "NTP not synced"; a warning chip while off; empty until
/// measured. The texts are the shared <see cref="ClockTexts"/> of the Clock compliance check.
/// </summary>
public static class DeviceClockText
{
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

        string text = beyondTolerance ? ClockTexts.OffsetText(offset)
            : ntp == NtpState.NotSynced ? ClockTexts.NtpNotSynced
            : ClockTexts.Ok;
        return new ChipInfo(text, clockOff ? PillKind.Warning : PillKind.Ok);
    }

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
