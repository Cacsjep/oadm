using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Oadm.Plugins.DhcpServer.Protocol;

/// <summary>DHCP message types (option 53, RFC 2132 9.6).</summary>
public enum DhcpMessageType : byte
{
    None = 0,
    Discover = 1,
    Offer = 2,
    Request = 3,
    Decline = 4,
    Ack = 5,
    Nak = 6,
    Release = 7,
    Inform = 8,
}

/// <summary>Option codes the server reads or writes (RFC 2132); every other option is ignored.</summary>
public static class DhcpOption
{
    public const byte Pad = 0;
    public const byte SubnetMask = 1;
    public const byte Router = 3;
    public const byte DnsServers = 6;
    public const byte HostName = 12;
    public const byte DomainName = 15;
    public const byte RequestedAddress = 50;
    public const byte LeaseTime = 51;
    public const byte OptionOverload = 52;
    public const byte MessageType = 53;
    public const byte ServerId = 54;
    public const byte ParameterRequestList = 55;
    public const byte Message = 56;
    public const byte MaxMessageSize = 57;
    public const byte RenewalTime = 58;
    public const byte RebindingTime = 59;
    public const byte ClientId = 61;
    public const byte End = 255;
}

/// <summary>
/// One BOOTP/DHCP message (RFC 2131 fixed header + RFC 2132 options) with the options the server uses as typed fields.
/// Addresses are host-order <see cref="uint"/> (10.0.0.1 = 0x0A000001). <see cref="TryRead"/> rejects malformed input
/// (short, wrong magic cookie, truncated or wrongly sized known options); unknown options are skipped; repeated options
/// are concatenated (RFC 3396); option overload (52) reads options from the file and sname fields.
/// </summary>
public sealed class DhcpMessage
{
    public const int HeaderLength = 236;
    public const int MinLength = HeaderLength + 4;      // header + magic cookie
    public const int MinReplyLength = 300;              // BOOTP minimum, some clients drop shorter replies
    public const int MaxLength = 1500;
    public const uint MagicCookie = 0x63825363;
    public const byte BootRequest = 1;
    public const byte BootReply = 2;
    public const ushort BroadcastFlag = 0x8000;

    public byte Op { get; set; } = BootRequest;

    public byte HardwareType { get; set; } = 1;          // Ethernet

    public byte HardwareLength { get; set; } = 6;

    public byte Hops { get; set; }

    public uint TransactionId { get; set; }

    public ushort Seconds { get; set; }

    public ushort Flags { get; set; }

    public uint ClientAddress { get; set; }              // ciaddr

    public uint YourAddress { get; set; }                // yiaddr

    public uint ServerAddress { get; set; }              // siaddr

    public uint RelayAddress { get; set; }               // giaddr

    /// <summary>chaddr (16 bytes).</summary>
    public byte[] ClientHardware { get; set; } = new byte[16];

    public DhcpMessageType MessageType { get; set; }

    public uint? SubnetMask { get; set; }

    public IReadOnlyList<uint> Routers { get; set; } = [];

    public IReadOnlyList<uint> DnsServers { get; set; } = [];

    public string? HostName { get; set; }

    public string? DomainName { get; set; }

    public uint? RequestedAddress { get; set; }

    /// <summary>Lease time in seconds.</summary>
    public uint? LeaseTime { get; set; }

    public uint? ServerId { get; set; }

    public byte[]? ParameterRequestList { get; set; }

    /// <summary>Option 56 (NAK reason).</summary>
    public string? Message { get; set; }

    public ushort? MaxMessageSize { get; set; }

    public uint? RenewalTime { get; set; }

    public uint? RebindingTime { get; set; }

    public byte[]? ClientId { get; set; }

    public bool IsBroadcast => (Flags & BroadcastFlag) != 0;

    /// <summary>The Ethernet MAC address (48 bit) from chaddr; 0 for other hardware types.</summary>
    public ulong Mac
    {
        get
        {
            if (HardwareType != 1 || HardwareLength != 6)
            {
                return 0;
            }

            ulong value = 0;
            for (var i = 0; i < 6; i++)
            {
                value = (value << 8) | ClientHardware[i];
            }

            return value;
        }

        set
        {
            HardwareType = 1;
            HardwareLength = 6;
            Array.Clear(ClientHardware);
            for (var i = 5; i >= 0; i--)
            {
                ClientHardware[i] = (byte)(value & 0xFF);
                value >>= 8;
            }
        }
    }

    /// <summary>True when the client asked for <paramref name="option"/> (or sent no parameter request list).</summary>
    public bool Requests(byte option) => ParameterRequestList is null || Array.IndexOf(ParameterRequestList, option) >= 0;

    /// <summary>Parses a received datagram. False (with a reason for the server log) for malformed input.</summary>
    public static bool TryRead(ReadOnlySpan<byte> data, out DhcpMessage? message, out string? error)
    {
        message = null;
        if (data.Length < MinLength)
        {
            error = "too short";
            return false;
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(data[HeaderLength..]) != MagicCookie)
        {
            error = "no DHCP magic cookie";
            return false;
        }

        var m = new DhcpMessage
        {
            Op = data[0],
            HardwareType = data[1],
            HardwareLength = data[2],
            Hops = data[3],
            TransactionId = BinaryPrimitives.ReadUInt32BigEndian(data[4..]),
            Seconds = BinaryPrimitives.ReadUInt16BigEndian(data[8..]),
            Flags = BinaryPrimitives.ReadUInt16BigEndian(data[10..]),
            ClientAddress = BinaryPrimitives.ReadUInt32BigEndian(data[12..]),
            YourAddress = BinaryPrimitives.ReadUInt32BigEndian(data[16..]),
            ServerAddress = BinaryPrimitives.ReadUInt32BigEndian(data[20..]),
            RelayAddress = BinaryPrimitives.ReadUInt32BigEndian(data[24..]),
        };
        if (m.Op is not (BootRequest or BootReply))
        {
            error = "unknown op";
            return false;
        }

        if (m.HardwareLength > 16)
        {
            error = "hardware address too long";
            return false;
        }

        data.Slice(28, 16).CopyTo(m.ClientHardware);

        // Collect options (concatenating repeated ones), then the overloaded fields.
        var raw = new Dictionary<byte, List<byte>>();
        if (!ReadOptions(data[MinLength..], raw, out error))
        {
            return false;
        }

        if (raw.TryGetValue(DhcpOption.OptionOverload, out var overload) && overload.Count == 1)
        {
            if ((overload[0] & 1) != 0 && !ReadOptions(data.Slice(108, 128), raw, out error))
            {
                return false;
            }

            if ((overload[0] & 2) != 0 && !ReadOptions(data.Slice(44, 64), raw, out error))
            {
                return false;
            }
        }

        if (!Apply(m, raw, out error))
        {
            return false;
        }

        message = m;
        error = null;
        return true;
    }

    /// <summary>Writes the message (padded to 300 bytes); returns the length. The buffer must hold <see cref="MaxLength"/>.</summary>
    public int Write(Span<byte> buffer)
    {
        if (buffer.Length < MaxLength)
        {
            throw new ArgumentException("The buffer is too small.", nameof(buffer));
        }

        buffer[..MaxLength].Clear();
        buffer[0] = Op;
        buffer[1] = HardwareType;
        buffer[2] = HardwareLength;
        buffer[3] = Hops;
        BinaryPrimitives.WriteUInt32BigEndian(buffer[4..], TransactionId);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[8..], Seconds);
        BinaryPrimitives.WriteUInt16BigEndian(buffer[10..], Flags);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[12..], ClientAddress);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[16..], YourAddress);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[20..], ServerAddress);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[24..], RelayAddress);
        ClientHardware.AsSpan(0, Math.Min(16, ClientHardware.Length)).CopyTo(buffer[28..]);
        BinaryPrimitives.WriteUInt32BigEndian(buffer[HeaderLength..], MagicCookie);

        var w = new OptionWriter(buffer, MinLength);
        if (MessageType != DhcpMessageType.None)
        {
            w.Byte(DhcpOption.MessageType, (byte)MessageType);
        }

        w.Address(DhcpOption.ServerId, ServerId);
        w.Address(DhcpOption.RequestedAddress, RequestedAddress);
        w.UInt(DhcpOption.LeaseTime, LeaseTime);
        w.UInt(DhcpOption.RenewalTime, RenewalTime);
        w.UInt(DhcpOption.RebindingTime, RebindingTime);
        w.Address(DhcpOption.SubnetMask, SubnetMask);
        w.Addresses(DhcpOption.Router, Routers);
        w.Addresses(DhcpOption.DnsServers, DnsServers);
        w.Text(DhcpOption.HostName, HostName);
        w.Text(DhcpOption.DomainName, DomainName);
        w.Text(DhcpOption.Message, Message);
        if (MaxMessageSize is { } max)
        {
            Span<byte> bytes = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(bytes, max);
            w.Bytes(DhcpOption.MaxMessageSize, bytes);
        }

        w.Bytes(DhcpOption.ParameterRequestList, ParameterRequestList);
        w.Bytes(DhcpOption.ClientId, ClientId);
        return Math.Max(MinReplyLength, w.End());
    }

    private static bool ReadOptions(ReadOnlySpan<byte> data, Dictionary<byte, List<byte>> raw, out string? error)
    {
        var i = 0;
        while (i < data.Length)
        {
            var code = data[i++];
            if (code == DhcpOption.Pad)
            {
                continue;
            }

            if (code == DhcpOption.End)
            {
                error = null;
                return true;
            }

            if (i >= data.Length || i + 1 + data[i] > data.Length)
            {
                error = string.Create(CultureInfo.InvariantCulture, $"option {code} is truncated");
                return false;
            }

            var length = data[i++];
            if (!raw.TryGetValue(code, out var list))
            {
                raw[code] = list = [];
            }

            list.AddRange(data.Slice(i, length));
            i += length;
        }

        error = null; // a missing End option is tolerated (some clients)
        return true;
    }

    private static bool Apply(DhcpMessage m, Dictionary<byte, List<byte>> raw, out string? error)
    {
        error = null;
        foreach (var (code, list) in raw)
        {
            var v = list.ToArray();
            switch (code)
            {
                case DhcpOption.MessageType:
                    if (v.Length != 1 || v[0] is 0 or > 8)
                    {
                        error = "bad message type";
                        return false;
                    }

                    m.MessageType = (DhcpMessageType)v[0];
                    break;
                case DhcpOption.SubnetMask:
                    if (!Single(v, out var mask, ref error, code))
                    {
                        return false;
                    }

                    m.SubnetMask = mask;
                    break;
                case DhcpOption.Router:
                case DhcpOption.DnsServers:
                    if (v.Length == 0 || v.Length % 4 != 0)
                    {
                        error = string.Create(CultureInfo.InvariantCulture, $"option {code} has a bad length");
                        return false;
                    }

                    var list4 = new uint[v.Length / 4];
                    for (var k = 0; k < list4.Length; k++)
                    {
                        list4[k] = BinaryPrimitives.ReadUInt32BigEndian(v.AsSpan(k * 4));
                    }

                    if (code == DhcpOption.Router)
                    {
                        m.Routers = list4;
                    }
                    else
                    {
                        m.DnsServers = list4;
                    }

                    break;
                case DhcpOption.HostName:
                    m.HostName = Text(v);
                    break;
                case DhcpOption.DomainName:
                    m.DomainName = Text(v);
                    break;
                case DhcpOption.Message:
                    m.Message = Text(v);
                    break;
                case DhcpOption.RequestedAddress:
                    if (!Single(v, out var requested, ref error, code))
                    {
                        return false;
                    }

                    m.RequestedAddress = requested;
                    break;
                case DhcpOption.LeaseTime:
                    if (!Single(v, out var lease, ref error, code))
                    {
                        return false;
                    }

                    m.LeaseTime = lease;
                    break;
                case DhcpOption.RenewalTime:
                    if (!Single(v, out var t1, ref error, code))
                    {
                        return false;
                    }

                    m.RenewalTime = t1;
                    break;
                case DhcpOption.RebindingTime:
                    if (!Single(v, out var t2, ref error, code))
                    {
                        return false;
                    }

                    m.RebindingTime = t2;
                    break;
                case DhcpOption.ServerId:
                    if (!Single(v, out var server, ref error, code))
                    {
                        return false;
                    }

                    m.ServerId = server;
                    break;
                case DhcpOption.ParameterRequestList:
                    m.ParameterRequestList = v;
                    break;
                case DhcpOption.MaxMessageSize:
                    if (v.Length != 2)
                    {
                        error = "option 57 has a bad length";
                        return false;
                    }

                    m.MaxMessageSize = BinaryPrimitives.ReadUInt16BigEndian(v);
                    break;
                case DhcpOption.ClientId:
                    if (v.Length < 2)
                    {
                        error = "option 61 is too short";
                        return false;
                    }

                    m.ClientId = v;
                    break;
            }
        }

        return true;
    }

    private static bool Single(byte[] v, out uint value, ref string? error, byte code)
    {
        if (v.Length != 4)
        {
            value = 0;
            error = string.Create(CultureInfo.InvariantCulture, $"option {code} has a bad length");
            return false;
        }

        value = BinaryPrimitives.ReadUInt32BigEndian(v);
        return true;
    }

    /// <summary>Printable ASCII only, trailing NULs removed (clients pad host names).</summary>
    private static string? Text(byte[] v)
    {
        var text = Encoding.ASCII.GetString(v).TrimEnd('\0');
        var clean = new string([.. text.Where(c => c >= 0x20 && c < 0x7F)]).Trim();
        return clean.Length == 0 ? null : clean;
    }

    private ref struct OptionWriter(Span<byte> buffer, int start)
    {
        private readonly Span<byte> _buffer = buffer;
        private int _pos = start;

        public void Byte(byte code, byte value)
        {
            Header(code, 1);
            _buffer[_pos++] = value;
        }

        public void UInt(byte code, uint? value)
        {
            if (value is { } v)
            {
                Header(code, 4);
                BinaryPrimitives.WriteUInt32BigEndian(_buffer[_pos..], v);
                _pos += 4;
            }
        }

        public void Address(byte code, uint? value) => UInt(code, value);

        public void Addresses(byte code, IReadOnlyList<uint> values)
        {
            var count = Math.Min(values.Count, 63);
            if (count == 0)
            {
                return;
            }

            Header(code, count * 4);
            for (var i = 0; i < count; i++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(_buffer[_pos..], values[i]);
                _pos += 4;
            }
        }

        public void Text(byte code, string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            var bytes = Encoding.ASCII.GetBytes(value.Length > 255 ? value[..255] : value);
            Bytes(code, bytes);
        }

        public void Bytes(byte code, scoped ReadOnlySpan<byte> value)
        {
            if (value.IsEmpty)
            {
                return;
            }

            var length = Math.Min(value.Length, 255);
            Header(code, length);
            value[..length].CopyTo(_buffer[_pos..]);
            _pos += length;
        }

        public int End()
        {
            _buffer[_pos++] = DhcpOption.End;
            return _pos;
        }

        private void Header(byte code, int length)
        {
            _buffer[_pos++] = code;
            _buffer[_pos++] = (byte)length;
        }
    }
}
