namespace Oadm.Plugins.Radius.Protocol.Text;

/// <summary>The names of NAS-Port-Type values (RFC 2865 5.41, RFC 3580 3.23) as the page shows them.</summary>
public static class NasPortTypes
{
    /// <summary>The name of <paramref name="value"/>; null when the switch sent none; "Type n" for values without a name.</summary>
    public static string? Name(uint? value) => value switch
    {
        null => null,
        0 => "Async",
        1 => "Sync",
        2 => "ISDN Sync",
        3 => "ISDN Async V.120",
        4 => "ISDN Async V.110",
        5 => "Virtual",
        6 => "PIAFS",
        7 => "HDLC Clear Channel",
        8 => "X.25",
        9 => "X.75",
        10 => "G.3 Fax",
        11 => "SDSL",
        12 => "ADSL-CAP",
        13 => "ADSL-DMT",
        14 => "IDSL",
        15 => "Ethernet",
        16 => "xDSL",
        17 => "Cable",
        18 or 19 => "Wireless",
        20 => "Token Ring",
        21 => "FDDI",
        _ => $"Type {value}",
    };

    /// <summary>IEEE 802.11 (RFC 3580 3.10: such ports get EAP packets of at most 1,496 bytes).</summary>
    public const uint Wireless80211 = 19;
}
