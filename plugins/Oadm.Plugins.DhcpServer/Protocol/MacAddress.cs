using System.Globalization;
using System.Net;
using System.Net.Sockets;

using Oadm.Plugins.Network.Model;

namespace Oadm.Plugins.DhcpServer.Protocol;

/// <summary>48-bit Ethernet MAC addresses as <see cref="ulong"/>: "B8:A4:4F:63:13:39".</summary>
public static class MacAddress
{
    public static string Format(ulong mac) => string.Create(CultureInfo.InvariantCulture,
        $"{(mac >> 40) & 0xFF:X2}:{(mac >> 32) & 0xFF:X2}:{(mac >> 24) & 0xFF:X2}:{(mac >> 16) & 0xFF:X2}:{(mac >> 8) & 0xFF:X2}:{mac & 0xFF:X2}");

    /// <summary>12 hex digits, optionally separated by ':' or '-' in pairs ("B8:A4:4F:63:13:39", "b8a44f631339").</summary>
    public static bool TryParse(string? text, out ulong mac)
    {
        mac = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var t = text.Trim();
        string hex;
        if (t.Length == 17)
        {
            var sep = t[2];
            if (sep is not (':' or '-'))
            {
                return false;
            }

            for (var i = 2; i < 17; i += 3)
            {
                if (t[i] != sep)
                {
                    return false;
                }
            }

            hex = t.Replace(sep.ToString(), string.Empty, StringComparison.Ordinal);
        }
        else
        {
            hex = t;
        }

        return hex.Length == 12 && hex.All(char.IsAsciiHexDigit)
            && ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out mac);
    }

    /// <summary>The 12 hex digits without separators, upper case (an AXIS serial number is its MAC address).</summary>
    public static string Compact(ulong mac) => mac.ToString("X12", CultureInfo.InvariantCulture);

    /// <summary>Multicast or broadcast MAC addresses never get a lease.</summary>
    public static bool IsUnicast(ulong mac) => mac != 0 && ((mac >> 40) & 1) == 0;
}

/// <summary>Conversions between host-order <see cref="uint"/> IPv4 addresses and <see cref="IPAddress"/>.</summary>
public static class Ip4
{
    public static uint From(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            address = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : throw new ArgumentException("Not an IPv4 address.", nameof(address));
        }

        Span<byte> bytes = stackalloc byte[4];
        address.TryWriteBytes(bytes, out _);
        return (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
    }

    public static IPAddress ToAddress(uint value) =>
        new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);

    public static string Format(uint value) => Ipv4.Format(value);

    public static bool TryParse(string? text, out uint value) => Ipv4.TryParse(text, out value);
}
