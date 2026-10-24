using System.Buffers.Binary;

namespace Oadm.Plugins.Radius.Protocol.Radius;

/// <summary>Why a datagram is not a usable RADIUS request (RFC 2865 3: such a packet is silently discarded).</summary>
public enum RadiusMalformation
{
    None,
    /// <summary>The datagram is shorter than the 20-byte header.</summary>
    TooShort,
    /// <summary>The Length field is below 20 or above 4,096.</summary>
    LengthOutOfRange,
    /// <summary>The datagram is shorter than its Length field.</summary>
    Truncated,
    /// <summary>An attribute's length is below 2 or it runs past the packet.</summary>
    BadAttribute,
    /// <summary>An attribute this server reads has the wrong size or appears more often than allowed.</summary>
    BadAttributeValue,
    /// <summary>Several EAP-Message attributes that are not consecutive, or EAP together with a password (RFC 3579 3.1, 3.3).</summary>
    BadEapMessage,
    /// <summary>The Message-Authenticator has the wrong length or appears more than once (RFC 3579 3.2, 3.3).</summary>
    BadMessageAuthenticator,
}

/// <summary>
/// A RADIUS packet as received: header, the attributes in their order with their raw bytes (repeated ones kept, unknown and
/// vendor ones kept but never interpreted) and the packet's own bytes (exactly Length bytes, padding cut off).
/// </summary>
public sealed class RadiusPacket
{
    /// <summary>The smallest packet: code, identifier, length and authenticator (RFC 2865 3).</summary>
    public const int HeaderLength = 20;

    /// <summary>The largest packet (RFC 2865 3).</summary>
    public const int MaxLength = 4096;

    /// <summary>The size of the (Request or Response) Authenticator.</summary>
    public const int AuthenticatorLength = 16;

    private readonly int[] _offsets;

    private RadiusPacket(byte[] bytes, RadiusAttribute[] attributes, int[] offsets)
    {
        Bytes = bytes;
        Attributes = attributes;
        _offsets = offsets;
    }

    /// <summary>The packet's bytes (a copy, exactly the Length field long).</summary>
    public ReadOnlyMemory<byte> Bytes { get; }

    public RadiusCode Code => (RadiusCode)Bytes.Span[0];

    public byte Identifier => Bytes.Span[1];

    /// <summary>The Request Authenticator (in a request) or the Response Authenticator (in an answer).</summary>
    public ReadOnlyMemory<byte> Authenticator => Bytes.Slice(4, AuthenticatorLength);

    /// <summary>Every attribute in the order of the packet.</summary>
    public IReadOnlyList<RadiusAttribute> Attributes { get; }

    /// <summary>The offset of attribute <paramref name="index"/>'s type octet in <see cref="Bytes"/>.</summary>
    public int OffsetOf(int index) => _offsets[index];

    /// <summary>The first attribute of <paramref name="type"/>, or null.</summary>
    public RadiusAttribute? Find(byte type)
    {
        foreach (var attribute in Attributes)
        {
            if (attribute.Type == type)
            {
                return attribute;
            }
        }

        return null;
    }

    /// <summary>How often <paramref name="type"/> appears.</summary>
    public int Count(byte type)
    {
        var count = 0;
        foreach (var attribute in Attributes)
        {
            if (attribute.Type == type)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Every attribute of <paramref name="type"/>, in order.</summary>
    public IReadOnlyList<RadiusAttribute> All(byte type) => [.. Attributes.Where(a => a.Type == type)];

    /// <summary>
    /// Reads a datagram (RFC 2865 3 and 5): 20..4,096 bytes; the Length field must not exceed the datagram (octets after it are
    /// padding and ignored, RFC 2865 3 "Length"); every attribute 2..255 bytes and inside the packet. Never throws: a
    /// malformed datagram is a result with the reason, to be dropped silently.
    /// </summary>
    public static RadiusParseResult Parse(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length < HeaderLength)
        {
            return RadiusParseResult.Malformed(RadiusMalformation.TooShort, $"Packet of {datagram.Length} bytes, shorter than 20");
        }

        int length = BinaryPrimitives.ReadUInt16BigEndian(datagram[2..]);
        if (length is < HeaderLength or > MaxLength)
        {
            return RadiusParseResult.Malformed(RadiusMalformation.LengthOutOfRange, $"Length field {length} outside 20..4096");
        }

        if (datagram.Length < length)
        {
            return RadiusParseResult.Malformed(RadiusMalformation.Truncated, $"Length field {length} longer than the packet ({datagram.Length} bytes)");
        }

        var bytes = datagram[..length].ToArray();
        var attributes = new List<RadiusAttribute>();
        var offsets = new List<int>();
        var offset = HeaderLength;
        while (offset < length)
        {
            if (length - offset < 2)
            {
                return RadiusParseResult.Malformed(RadiusMalformation.BadAttribute, $"Attribute header cut off at byte {offset}");
            }

            var type = bytes[offset];
            var attributeLength = bytes[offset + 1];
            if (attributeLength < 2)
            {
                return RadiusParseResult.Malformed(RadiusMalformation.BadAttribute, $"Attribute {type} with length {attributeLength} at byte {offset}");
            }

            if (offset + attributeLength > length)
            {
                return RadiusParseResult.Malformed(RadiusMalformation.BadAttribute, $"Attribute {type} runs past the packet at byte {offset}");
            }

            attributes.Add(new RadiusAttribute(type, bytes.AsMemory(offset + 2, attributeLength - 2)));
            offsets.Add(offset);
            offset += attributeLength;
        }

        return new RadiusParseResult(new RadiusPacket(bytes, [.. attributes], [.. offsets]), RadiusMalformation.None, null);
    }
}

/// <summary>A parsed packet, or the reason it is malformed (never both).</summary>
/// <param name="Packet">The packet; null when malformed.</param>
/// <param name="Error">Why it is malformed; <see cref="RadiusMalformation.None"/> for a packet.</param>
/// <param name="Detail">A short English detail for the debug log (no secrets, no payload).</param>
public readonly record struct RadiusParseResult(RadiusPacket? Packet, RadiusMalformation Error, string? Detail)
{
    public bool IsMalformed => Packet is null;

    public static RadiusParseResult Malformed(RadiusMalformation error, string detail) => new(null, error, detail);
}
