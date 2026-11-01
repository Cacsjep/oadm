using Oadm.Contracts.V1;
using Oadm.Sdk.Devices;

namespace Oadm.Client.Devices;

/// <summary>
/// The Clock column (docs/spec/main-window.md "Clock"): the device clock against the server's, short: "Off", "NTP not
/// synced" or "OK"; a warning chip while off; how far in the tooltip and the export ("50 s ahead", "2 min behind"); empty
/// until measured. The offset texts are the shared <see cref="ClockTexts"/> of the Clock compliance check.
/// </summary>
public static class DeviceClockText
{
    /// <summary>The cell of a clock beyond the allowed difference (how far: <see cref="Tooltip"/>).</summary>
    public const string Off = "Off";

    /// <summary>
    /// The cell: empty until measured; "Off" while the offset is beyond the allowed difference, else "NTP not synced"
    /// while the device's NTP is not synchronized, else "OK". A warning chip only while the server counts the clock as off
    /// (a device that is not Ok shows its last measured state as plain text).
    /// </summary>
    public static ChipInfo ToChip(int? offsetMs, NtpState ntp, bool clockOff, bool beyondTolerance)
    {
        if (offsetMs is null)
        {
            return ChipInfo.Empty;
        }

        string text = beyondTolerance ? Off
            : ntp == NtpState.NotSynced ? ClockTexts.NtpNotSynced
            : ClockTexts.Ok;
        return new ChipInfo(text, clockOff ? PillKind.Warning : PillKind.Ok);
    }

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
