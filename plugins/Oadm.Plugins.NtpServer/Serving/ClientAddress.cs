using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Oadm.Plugins.NtpServer.Serving;

/// <summary>
/// A client address as a 128-bit key (IPv6, IPv4 as IPv4-mapped ::ffff:a.b.c.d) read straight from a reused
/// <see cref="SocketAddress"/>, so the receive loop needs no <see cref="IPEndPoint"/> per packet. The address bytes sit
/// at the same offsets of sockaddr_in (4..7) and sockaddr_in6 (8..23) on Windows, Linux and macOS.
/// </summary>
public static class ClientAddress
{
    private static readonly UInt128 V4Prefix = (UInt128)0xFFFF << 32;

    public static bool TryGetKey(SocketAddress address, out UInt128 key)
    {
        ArgumentNullException.ThrowIfNull(address);
        var buffer = address.Buffer.Span[..address.Size];
        switch (address.Family)
        {
            case AddressFamily.InterNetwork when buffer.Length >= 8:
                key = V4Prefix | BinaryPrimitives.ReadUInt32BigEndian(buffer[4..]);
                return true;
            case AddressFamily.InterNetworkV6 when buffer.Length >= 24:
                key = BinaryPrimitives.ReadUInt128BigEndian(buffer[8..]);
                return true;
            default:
                key = default;
                return false;
        }
    }

    public static UInt128 FromIPAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        Span<byte> bytes = stackalloc byte[16];
        var v6 = address.AddressFamily == AddressFamily.InterNetwork ? address.MapToIPv6() : address;
        v6.TryWriteBytes(bytes, out _);
        return BinaryPrimitives.ReadUInt128BigEndian(bytes);
    }

    /// <summary>The address of a key; IPv4-mapped keys become plain IPv4 addresses.</summary>
    public static IPAddress ToIPAddress(UInt128 key)
    {
        Span<byte> bytes = stackalloc byte[16];
        if ((key >> 32) == 0xFFFF)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)(key & 0xFFFFFFFF));
            return new IPAddress(bytes[..4]);
        }

        BinaryPrimitives.WriteUInt128BigEndian(bytes, key);
        return new IPAddress(bytes);
    }
}
