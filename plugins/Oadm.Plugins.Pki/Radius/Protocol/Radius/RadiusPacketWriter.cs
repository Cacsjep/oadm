using System.Buffers.Binary;

namespace Oadm.Plugins.Radius.Protocol.Radius;

/// <summary>
/// Builds RADIUS packets. Every packet gets a Message-Authenticator as its first attribute (RFC 3579 3.2; Blast-RADIUS
/// hardening: first, so no attacker-chosen prefix precedes it); answers then get their Response Authenticator (RFC 2865 3),
/// computed after the Message-Authenticator as RFC 3579 3.2 requires.
/// </summary>
public static class RadiusPacketWriter
{
    /// <summary>
    /// An answer (Access-Accept, Access-Reject, Access-Challenge) to the request with <paramref name="requestAuthenticator"/>:
    /// Message-Authenticator first, then <paramref name="attributes"/> in order. Null when it would exceed 4,096 bytes.
    /// </summary>
    public static byte[]? Response(RadiusCode code, byte identifier, ReadOnlySpan<byte> requestAuthenticator, ReadOnlySpan<byte> secret, IReadOnlyList<RadiusAttribute> attributes)
    {
        if (requestAuthenticator.Length != RadiusPacket.AuthenticatorLength)
        {
            throw new ArgumentException("A Request Authenticator has 16 bytes.", nameof(requestAuthenticator));
        }

        var packet = Assemble(code, identifier, requestAuthenticator, attributes, messageAuthenticator: true);
        if (packet is null)
        {
            return null;
        }

        SignMessageAuthenticator(packet, secret);
        RadiusAuthenticators.ResponseAuthenticator(packet, secret).CopyTo(packet.AsSpan(4));
        return packet;
    }

    /// <summary>
    /// A request (what a switch sends; used by tests and fixtures): <paramref name="authenticator"/> as the Request
    /// Authenticator, a Message-Authenticator first when <paramref name="messageAuthenticator"/> is true.
    /// </summary>
    public static byte[]? Request(RadiusCode code, byte identifier, ReadOnlySpan<byte> authenticator, ReadOnlySpan<byte> secret, IReadOnlyList<RadiusAttribute> attributes, bool messageAuthenticator = true)
    {
        if (authenticator.Length != RadiusPacket.AuthenticatorLength)
        {
            throw new ArgumentException("A Request Authenticator has 16 bytes.", nameof(authenticator));
        }

        var packet = Assemble(code, identifier, authenticator, attributes, messageAuthenticator);
        if (packet is not null && messageAuthenticator)
        {
            SignMessageAuthenticator(packet, secret);
        }

        return packet;
    }

    private static byte[]? Assemble(RadiusCode code, byte identifier, ReadOnlySpan<byte> authenticator, IReadOnlyList<RadiusAttribute> attributes, bool messageAuthenticator)
    {
        ArgumentNullException.ThrowIfNull(attributes);
        var length = RadiusPacket.HeaderLength + (messageAuthenticator ? 2 + RadiusAuthenticators.MessageAuthenticatorLength : 0);
        foreach (var attribute in attributes)
        {
            if (attribute.Value.Length > RadiusAttribute.MaxValueLength)
            {
                throw new ArgumentException($"Attribute {attribute.Type} has {attribute.Value.Length} bytes, more than 253.", nameof(attributes));
            }

            if (attribute.Type == RadiusAttributeType.MessageAuthenticator)
            {
                throw new ArgumentException("The writer adds the Message-Authenticator itself.", nameof(attributes));
            }

            length += 2 + attribute.Value.Length;
        }

        if (length > RadiusPacket.MaxLength)
        {
            return null;
        }

        var packet = new byte[length];
        packet[0] = (byte)code;
        packet[1] = identifier;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)length);
        authenticator.CopyTo(packet.AsSpan(4));
        var offset = RadiusPacket.HeaderLength;
        if (messageAuthenticator)
        {
            packet[offset] = RadiusAttributeType.MessageAuthenticator;
            packet[offset + 1] = 2 + RadiusAuthenticators.MessageAuthenticatorLength;
            offset += 2 + RadiusAuthenticators.MessageAuthenticatorLength; // value stays zero until signed
        }

        foreach (var attribute in attributes)
        {
            packet[offset] = attribute.Type;
            packet[offset + 1] = (byte)(2 + attribute.Value.Length);
            attribute.Value.Span.CopyTo(packet.AsSpan(offset + 2));
            offset += 2 + attribute.Value.Length;
        }

        return packet;
    }

    /// <summary>The Message-Authenticator is the first attribute; its value is zero while the HMAC is computed (RFC 3579 3.2).</summary>
    private static void SignMessageAuthenticator(byte[] packet, ReadOnlySpan<byte> secret)
    {
        var value = packet.AsSpan(RadiusPacket.HeaderLength + 2, RadiusAuthenticators.MessageAuthenticatorLength);
        value.Clear();
        RadiusAuthenticators.MessageAuthenticator(packet, secret).CopyTo(value);
    }
}
