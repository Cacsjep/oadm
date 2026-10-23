using System.Buffers.Binary;
using System.Globalization;

namespace Oadm.Plugins.Radius.Protocol.Eap;

/// <summary>EAP codes (RFC 3748 4).</summary>
public enum EapCode : byte
{
    Request = 1,
    Response = 2,
    Success = 3,
    Failure = 4,
}

/// <summary>EAP method types (RFC 3748 5, IANA "Method Types").</summary>
public static class EapType
{
    public const byte Identity = 1;
    public const byte Notification = 2;
    public const byte Nak = 3;
    public const byte Md5Challenge = 4;
    public const byte Otp = 5;
    public const byte Gtc = 6;
    public const byte Tls = 13;
    public const byte Leap = 17;
    public const byte Sim = 18;
    public const byte Ttls = 21;
    public const byte Aka = 23;
    public const byte Peap = 25;
    public const byte MsChapV2 = 26;
    public const byte Fast = 43;
    public const byte Pwd = 52;
    public const byte Teap = 55;
    public const byte Expanded = 254;

    /// <summary>The usual name of a method, for "The device does not support EAP-TLS (it asked for PEAP)".</summary>
    public static string Name(byte type) => type switch
    {
        Md5Challenge => "MD5",
        Otp => "OTP",
        Gtc => "GTC",
        Tls => "EAP-TLS",
        Leap => "LEAP",
        Sim => "EAP-SIM",
        Ttls => "EAP-TTLS",
        Aka => "EAP-AKA",
        Peap => "PEAP",
        MsChapV2 => "EAP-MSCHAPv2",
        Fast => "EAP-FAST",
        Pwd => "EAP-pwd",
        Teap => "TEAP",
        Expanded => "an expanded type",
        _ => "type " + type.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>Why bytes are not an EAP packet.</summary>
public enum EapMalformation
{
    None,
    /// <summary>Shorter than the 4-byte header, or a Request/Response without a Type.</summary>
    TooShort,
    /// <summary>The Length field differs from the bytes the EAP-Message attributes carried (docs/spec: must match).</summary>
    LengthMismatch,
    /// <summary>A code other than 1..4, or Success/Failure with data (RFC 3748 4.2).</summary>
    BadCode,
}

/// <summary>
/// One EAP packet (RFC 3748 4): Code, Identifier, Length and, for Request and Response, Type and Type-Data. Parsing is
/// strict: the Length field must equal the bytes (docs/spec/radius-server.md "EAP"), a Request or Response has a Type
/// (RFC 3748 4.1), Success and Failure are 4 bytes (RFC 3748 4.2). Never throws on input.
/// </summary>
public sealed record EapPacket(EapCode Code, byte Identifier, byte Type, ReadOnlyMemory<byte> Data)
{
    /// <summary>Code, Identifier and Length.</summary>
    public const int HeaderLength = 4;

    /// <summary>The bytes of this packet.</summary>
    public byte[] ToBytes()
    {
        var hasType = Code is EapCode.Request or EapCode.Response;
        var length = HeaderLength + (hasType ? 1 + Data.Length : 0);
        if (length > ushort.MaxValue)
        {
            throw new InvalidOperationException("An EAP packet has at most 65,535 bytes.");
        }

        var bytes = new byte[length];
        bytes[0] = (byte)Code;
        bytes[1] = Identifier;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2), (ushort)length);
        if (hasType)
        {
            bytes[4] = Type;
            Data.Span.CopyTo(bytes.AsSpan(5));
        }

        return bytes;
    }

    public static EapPacket Request(byte identifier, byte type, ReadOnlyMemory<byte> data) => new(EapCode.Request, identifier, type, data);

    public static EapPacket Response(byte identifier, byte type, ReadOnlyMemory<byte> data) => new(EapCode.Response, identifier, type, data);

    /// <summary>EAP-Success; its Identifier matches the Response it answers (RFC 3748 4.2).</summary>
    public static EapPacket Success(byte identifier) => new(EapCode.Success, identifier, 0, ReadOnlyMemory<byte>.Empty);

    /// <summary>EAP-Failure; its Identifier matches the Response it answers (RFC 3748 4.2).</summary>
    public static EapPacket Failure(byte identifier) => new(EapCode.Failure, identifier, 0, ReadOnlyMemory<byte>.Empty);

    /// <summary>Reads the joined EAP-Message bytes.</summary>
    public static EapParseResult Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderLength)
        {
            return new EapParseResult(null, EapMalformation.TooShort);
        }

        var code = bytes[0];
        int length = BinaryPrimitives.ReadUInt16BigEndian(bytes[2..]);
        if (length != bytes.Length)
        {
            return new EapParseResult(null, EapMalformation.LengthMismatch);
        }

        switch ((EapCode)code)
        {
            case EapCode.Request or EapCode.Response:
                if (length < HeaderLength + 1)
                {
                    return new EapParseResult(null, EapMalformation.TooShort);
                }

                return new EapParseResult(new EapPacket((EapCode)code, bytes[1], bytes[4], bytes[5..].ToArray()), EapMalformation.None);
            case EapCode.Success or EapCode.Failure:
                return length == HeaderLength
                    ? new EapParseResult(new EapPacket((EapCode)code, bytes[1], 0, ReadOnlyMemory<byte>.Empty), EapMalformation.None)
                    : new EapParseResult(null, EapMalformation.BadCode);
            default:
                return new EapParseResult(null, EapMalformation.BadCode);
        }
    }
}

/// <summary>A parsed EAP packet or why it is malformed.</summary>
public readonly record struct EapParseResult(EapPacket? Packet, EapMalformation Error);
