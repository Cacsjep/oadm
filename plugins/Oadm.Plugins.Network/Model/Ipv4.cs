using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Oadm.Plugins.Network.Model;

/// <summary>IPv4 address math for validation and range assignment. Addresses are handled as host-order uint.</summary>
public static class Ipv4
{
    /// <summary>Strict dotted quad ("10.0.0.5"); rejects the short forms IPAddress.TryParse accepts ("10.5").</summary>
    public static bool TryParse(string? text, out uint value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Trim().Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        foreach (var part in parts)
        {
            if (part.Length is 0 or > 3 || !part.All(char.IsAsciiDigit)
                || (part.Length > 1 && part[0] == '0')
                || !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var octet) || octet > 255)
            {
                return false;
            }

            value = (value << 8) | (uint)octet;
        }

        return true;
    }

    public static string Format(uint value) =>
        string.Create(CultureInfo.InvariantCulture, $"{value >> 24}.{(value >> 16) & 0xFF}.{(value >> 8) & 0xFF}.{value & 0xFF}");

    public static uint Mask(int prefixLength) =>
        prefixLength <= 0 ? 0u : uint.MaxValue << (32 - prefixLength);

    public static string MaskText(int prefixLength) => Format(Mask(prefixLength));

    /// <summary>Accepts a prefix length ("24", "/24") or a contiguous dotted subnet mask ("255.255.255.0").</summary>
    public static bool TryParsePrefix(string? text, out int prefixLength)
    {
        prefixLength = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var t = text.Trim().TrimStart('/');
        if (t.Contains('.', StringComparison.Ordinal))
        {
            if (!TryParse(t, out var mask))
            {
                return false;
            }

            var ones = System.Numerics.BitOperations.PopCount(mask);
            if (Mask(ones) != mask)
            {
                return false; // not contiguous
            }

            prefixLength = ones;
            return true;
        }

        return int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out prefixLength) && prefixLength is >= 0 and <= 32;
    }

    public static uint Network(uint address, int prefixLength) => address & Mask(prefixLength);

    public static uint Broadcast(uint address, int prefixLength) => Network(address, prefixLength) | ~Mask(prefixLength);

    public static bool SameSubnet(uint a, uint b, int prefixLength) => Network(a, prefixLength) == Network(b, prefixLength);

    /// <summary>Why <paramref name="address"/> cannot be a device's unicast address in the given subnet, or null when it can.</summary>
    public static string? HostAddressProblem(uint address, int prefixLength)
    {
        var first = address >> 24;
        if (address == 0 || first == 0)
        {
            return "is not a usable address";
        }

        if (first == 127)
        {
            return "is a loopback address";
        }

        if (first >= 224)
        {
            return first < 240 ? "is a multicast address" : "is a reserved address";
        }

        if ((address & 0xFFFF0000) == 0xA9FE0000)
        {
            return "is a link-local address";
        }

        if (prefixLength <= 30)
        {
            if (address == Network(address, prefixLength))
            {
                return "is the network address of its subnet";
            }

            if (address == Broadcast(address, prefixLength))
            {
                return "is the broadcast address of its subnet";
            }
        }

        return null;
    }

    /// <summary>
    /// Assigns <paramref name="count"/> consecutive addresses starting at <paramref name="start"/>.
    /// Fails (returns false with a reason) when the range leaves the subnet or hits its broadcast address.
    /// </summary>
    public static bool TryAssignRange(string start, int prefixLength, int count, out IReadOnlyList<string> addresses, out string? error)
    {
        addresses = [];
        error = null;
        if (!TryParse(start, out var first))
        {
            error = $"Start address \"{start}\" is not a valid IPv4 address.";
            return false;
        }

        if (count <= 0)
        {
            return true;
        }

        var list = new List<string>(count);
        var broadcast = Broadcast(first, prefixLength);
        for (var i = 0; i < count; i++)
        {
            var address = (ulong)first + (ulong)i;
            if (address > uint.MaxValue || (prefixLength <= 30 && (uint)address >= broadcast) || !SameSubnet((uint)address, first, prefixLength))
            {
                error = $"The range starting at {start} has room for {i} of {count} devices in the /{prefixLength} subnet.";
                return false;
            }

            list.Add(Format((uint)address));
        }

        addresses = list;
        return true;
    }

    /// <summary>True for a valid IPv4 or IPv6 literal (DNS server, gateway).</summary>
    public static bool IsIpLiteral(string? text) =>
        TryParse(text, out _) || (IPAddress.TryParse(text?.Trim() ?? string.Empty, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6);
}
