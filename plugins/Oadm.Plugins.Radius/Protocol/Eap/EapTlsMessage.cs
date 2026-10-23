using System.Buffers.Binary;

namespace Oadm.Plugins.Radius.Protocol.Eap;

/// <summary>The EAP-TLS flags octet (RFC 5216 3.1, 3.2): L, M, S and five reserved bits (ignored on reception).</summary>
[Flags]
public enum EapTlsBits : byte
{
    None = 0,
    /// <summary>Start: only in the server's EAP-TLS Start.</summary>
    Start = 0x20,
    /// <summary>More fragments follow.</summary>
    More = 0x40,
    /// <summary>The 4-octet TLS Message Length follows.</summary>
    Length = 0x80,
}

/// <summary>
/// The Type-Data of an EAP-TLS packet (RFC 5216 3.1, 3.2): Flags, the TLS Message Length when L is set, and TLS data. The
/// reserved bits are ignored (RFC 5216 3.1 "MUST ignore them on reception").
/// </summary>
public sealed record EapTlsMessage(EapTlsBits Flags, uint? TotalLength, ReadOnlyMemory<byte> Data)
{
    /// <summary>Flags (1) and TLS Message Length (4): the EAP-TLS header of a first fragment.</summary>
    public const int MaxHeaderLength = 5;

    public bool IsStart => (Flags & EapTlsBits.Start) != 0;

    public bool HasMore => (Flags & EapTlsBits.More) != 0;

    public bool HasLength => (Flags & EapTlsBits.Length) != 0;

    /// <summary>An empty message without L and M: a fragment acknowledgement or the final empty response (RFC 5216 2.1.5, 2.1.3).</summary>
    public bool IsAcknowledgement => Data.IsEmpty && !HasMore && !HasLength;

    /// <summary>The EAP-TLS Start of the server: S set, no data (RFC 5216 2.1.1).</summary>
    public static EapTlsMessage StartMessage { get; } = new(EapTlsBits.Start, null, ReadOnlyMemory<byte>.Empty);

    /// <summary>An empty message: a fragment acknowledgement (RFC 5216 2.1.5).</summary>
    public static EapTlsMessage Acknowledgement { get; } = new(EapTlsBits.None, null, ReadOnlyMemory<byte>.Empty);

    /// <summary>The Type-Data bytes.</summary>
    public byte[] ToBytes()
    {
        var hasLength = TotalLength is not null;
        var bytes = new byte[1 + (hasLength ? 4 : 0) + Data.Length];
        bytes[0] = (byte)((Flags & ~EapTlsBits.Length) | (hasLength ? EapTlsBits.Length : EapTlsBits.None));
        if (hasLength)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(1), TotalLength!.Value);
        }

        Data.Span.CopyTo(bytes.AsSpan(hasLength ? 5 : 1));
        return bytes;
    }

    /// <summary>Reads the Type-Data of an EAP-TLS packet; null when it has no flags octet or L is set without 4 length octets.</summary>
    public static EapTlsMessage? Parse(ReadOnlySpan<byte> typeData)
    {
        if (typeData.IsEmpty)
        {
            return null;
        }

        var flags = (EapTlsBits)(typeData[0] & 0xE0);
        if ((flags & EapTlsBits.Length) == 0)
        {
            return new EapTlsMessage(flags, null, typeData[1..].ToArray());
        }

        return typeData.Length < 5 ? null : new EapTlsMessage(flags, BinaryPrimitives.ReadUInt32BigEndian(typeData[1..]), typeData[5..].ToArray());
    }
}
