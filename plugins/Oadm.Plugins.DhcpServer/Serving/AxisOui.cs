namespace Oadm.Plugins.DhcpServer.Serving;

/// <summary>
/// The MAC address blocks (MA-L, formerly OUI) the IEEE registry lists for Axis Communications AB, checked 2026-10-08:
/// 00:40:8C (1998), AC:CC:8E (2011), B8:A4:4F (2019), E8:27:25 (2023). The only place OADM keeps them; a new block is
/// added here.
/// </summary>
public static class AxisOui
{
    /// <summary>The first three bytes of each block as a number (0x00408C).</summary>
    public static IReadOnlyList<uint> Blocks { get; } = [0x00408C, 0xACCC8E, 0xB8A44F, 0xE82725];

    /// <summary>True when the MAC address (48 bits in the low bits) belongs to an Axis block.</summary>
    public static bool IsAxis(ulong mac)
    {
        var oui = (uint)((mac >> 24) & 0xFFFFFF);
        foreach (var block in Blocks)
        {
            if (block == oui)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The device serial number OADM uses for a MAC address: 12 upper-case hex digits ("B8A44F631339").</summary>
    public static string Serial(ulong mac) => (mac & 0xFFFF_FFFF_FFFF).ToString("X12", System.Globalization.CultureInfo.InvariantCulture);
}
