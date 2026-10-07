using System.Buffers.Binary;
using System.Text;

namespace Oadm.Plugins.NtpServer.Protocol;

/// <summary>NTP association modes (RFC 5905 figure 10).</summary>
public enum NtpMode : byte
{
    Reserved = 0,
    SymmetricActive = 1,
    SymmetricPassive = 2,
    Client = 3,
    Server = 4,
    Broadcast = 5,
    Control = 6,
    Private = 7,
}

/// <summary>Leap indicator: 0 no warning, 1/2 leap second pending, 3 = clock unsynchronized (alarm).</summary>
public enum NtpLeap : byte
{
    NoWarning = 0,
    AddSecond = 1,
    DeleteSecond = 2,
    Unsynchronized = 3,
}

/// <summary>
/// The 48-byte NTP header (RFC 5905 figure 8), no extension fields or MAC. Big-endian on the wire. Value type: parsing
/// and writing never allocate.
/// </summary>
public readonly record struct NtpPacket
{
    /// <summary>Header length; requests may be longer (extension fields, MAC), answers are exactly this long.</summary>
    public const int Length = 48;

    public NtpLeap Leap { get; init; }

    public byte Version { get; init; }

    public NtpMode Mode { get; init; }

    /// <summary>0 = unspecified / kiss-o'-death, 1 = primary, 2..15 secondary, 16 = unsynchronized.</summary>
    public byte Stratum { get; init; }

    /// <summary>log2 seconds.</summary>
    public sbyte Poll { get; init; }

    /// <summary>log2 seconds.</summary>
    public sbyte Precision { get; init; }

    /// <summary>NTP short format (16.16 seconds).</summary>
    public uint RootDelay { get; init; }

    /// <summary>NTP short format (16.16 seconds).</summary>
    public uint RootDispersion { get; init; }

    /// <summary>Reference id: IPv4 address of the upstream, a hash for IPv6, ASCII ("LOCL") for stratum 1 or a kiss code.</summary>
    public uint ReferenceId { get; init; }

    public ulong ReferenceTimestamp { get; init; }

    public ulong OriginateTimestamp { get; init; }

    public ulong ReceiveTimestamp { get; init; }

    public ulong TransmitTimestamp { get; init; }

    /// <summary>Reads the header. False for packets shorter than 48 bytes.</summary>
    public static bool TryRead(ReadOnlySpan<byte> data, out NtpPacket packet)
    {
        if (data.Length < Length)
        {
            packet = default;
            return false;
        }

        packet = new NtpPacket
        {
            Leap = (NtpLeap)(data[0] >> 6),
            Version = (byte)((data[0] >> 3) & 0x7),
            Mode = (NtpMode)(data[0] & 0x7),
            Stratum = data[1],
            Poll = (sbyte)data[2],
            Precision = (sbyte)data[3],
            RootDelay = BinaryPrimitives.ReadUInt32BigEndian(data[4..]),
            RootDispersion = BinaryPrimitives.ReadUInt32BigEndian(data[8..]),
            ReferenceId = BinaryPrimitives.ReadUInt32BigEndian(data[12..]),
            ReferenceTimestamp = BinaryPrimitives.ReadUInt64BigEndian(data[16..]),
            OriginateTimestamp = BinaryPrimitives.ReadUInt64BigEndian(data[24..]),
            ReceiveTimestamp = BinaryPrimitives.ReadUInt64BigEndian(data[32..]),
            TransmitTimestamp = BinaryPrimitives.ReadUInt64BigEndian(data[40..]),
        };
        return true;
    }

    /// <summary>Writes the 48-byte header into <paramref name="destination"/> (at least 48 bytes).</summary>
    public void Write(Span<byte> destination)
    {
        if (destination.Length < Length)
        {
            throw new ArgumentException("The buffer is shorter than 48 bytes.", nameof(destination));
        }

        destination[0] = (byte)(((byte)Leap << 6) | ((Version & 0x7) << 3) | ((byte)Mode & 0x7));
        destination[1] = Stratum;
        destination[2] = (byte)Poll;
        destination[3] = (byte)Precision;
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], RootDelay);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..], RootDispersion);
        BinaryPrimitives.WriteUInt32BigEndian(destination[12..], ReferenceId);
        BinaryPrimitives.WriteUInt64BigEndian(destination[16..], ReferenceTimestamp);
        BinaryPrimitives.WriteUInt64BigEndian(destination[24..], OriginateTimestamp);
        BinaryPrimitives.WriteUInt64BigEndian(destination[32..], ReceiveTimestamp);
        BinaryPrimitives.WriteUInt64BigEndian(destination[40..], TransmitTimestamp);
    }

    /// <summary>Overwrites the transmit timestamp of an encoded packet (set right before sending).</summary>
    public static void WriteTransmitTimestamp(Span<byte> encoded, ulong timestamp) =>
        BinaryPrimitives.WriteUInt64BigEndian(encoded[40..], timestamp);

    /// <summary>Four ASCII characters as a reference id ("LOCL", "RATE").</summary>
    public static uint AsciiId(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        Span<byte> bytes = stackalloc byte[4];
        bytes.Clear();
        Encoding.ASCII.GetBytes(code.AsSpan(0, Math.Min(4, code.Length)), bytes);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    /// <summary>The reference id as ASCII when it is printable ("RATE"), else null.</summary>
    public static string? AsciiCode(uint referenceId)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, referenceId);
        var length = bytes.IndexOf((byte)0);
        var text = bytes[..(length < 0 ? 4 : length)];
        foreach (var b in text)
        {
            if (b is < 0x20 or > 0x7E)
            {
                return null;
            }
        }

        return text.Length == 0 ? null : Encoding.ASCII.GetString(text);
    }
}
