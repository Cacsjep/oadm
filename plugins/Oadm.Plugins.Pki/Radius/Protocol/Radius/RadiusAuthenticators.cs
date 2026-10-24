using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

namespace Oadm.Plugins.Radius.Protocol.Radius;

/// <summary>
/// The RADIUS integrity checks: the Response Authenticator (RFC 2865 3, MD5) and the Message-Authenticator (RFC 3579 3.2,
/// HMAC-MD5 with the shared secret). MD5 is what the RFCs define; nothing here protects confidentiality.
/// </summary>
[SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "RADIUS defines MD5 and HMAC-MD5 (RFC 2865 3, RFC 3579 3.2).")]
public static class RadiusAuthenticators
{
    /// <summary>The value length of a Message-Authenticator (attribute length 18).</summary>
    public const int MessageAuthenticatorLength = 16;

    /// <summary>
    /// ResponseAuth = MD5(Code + ID + Length + RequestAuth + Attributes + Secret) (RFC 2865 3). <paramref name="packet"/> holds
    /// the answer with the Request Authenticator in its authenticator field.
    /// </summary>
    public static byte[] ResponseAuthenticator(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> secret)
    {
        var buffer = new byte[packet.Length + secret.Length];
        packet.CopyTo(buffer);
        secret.CopyTo(buffer.AsSpan(packet.Length));
        return MD5.HashData(buffer);
    }

    /// <summary>
    /// HMAC-MD5 over the whole packet with the Message-Authenticator value taken as 16 zero octets (RFC 3579 3.2).
    /// <paramref name="packet"/> must already hold zeros there; in an answer the authenticator field holds the Request
    /// Authenticator.
    /// </summary>
    public static byte[] MessageAuthenticator(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> secret) => HMACMD5.HashData(secret, packet);

    /// <summary>
    /// Checks the Message-Authenticator of a request against the shared secret (RFC 3579 3.2). False when there is none, more
    /// than one, a wrong length or a wrong value. The comparison takes the same time for every value.
    /// </summary>
    public static bool VerifyRequest(RadiusPacket request, ReadOnlySpan<byte> secret)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Verify(request, request.Authenticator.Span, secret);
    }

    /// <summary>
    /// Checks an answer as a switch does (RFC 2865 3, RFC 3579 3.2): its Response Authenticator and, when present, its
    /// Message-Authenticator, both over the Request Authenticator of <paramref name="requestAuthenticator"/>.
    /// </summary>
    public static bool VerifyResponse(RadiusPacket response, ReadOnlySpan<byte> requestAuthenticator, ReadOnlySpan<byte> secret)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (requestAuthenticator.Length != RadiusPacket.AuthenticatorLength)
        {
            return false;
        }

        var copy = response.Bytes.ToArray();
        requestAuthenticator.CopyTo(copy.AsSpan(4));
        var expected = ResponseAuthenticator(copy, secret);
        if (!CryptographicOperations.FixedTimeEquals(expected, response.Authenticator.Span))
        {
            return false;
        }

        return response.Count(RadiusAttributeType.MessageAuthenticator) == 0 || Verify(response, requestAuthenticator, secret);
    }

    private static bool Verify(RadiusPacket packet, ReadOnlySpan<byte> authenticator, ReadOnlySpan<byte> secret)
    {
        var index = -1;
        for (var i = 0; i < packet.Attributes.Count; i++)
        {
            if (packet.Attributes[i].Type != RadiusAttributeType.MessageAuthenticator)
            {
                continue;
            }

            if (index >= 0 || packet.Attributes[i].Value.Length != MessageAuthenticatorLength)
            {
                return false;
            }

            index = i;
        }

        if (index < 0)
        {
            return false;
        }

        var copy = packet.Bytes.ToArray();
        authenticator.CopyTo(copy.AsSpan(4));
        var valueOffset = packet.OffsetOf(index) + 2;
        copy.AsSpan(valueOffset, MessageAuthenticatorLength).Clear();
        var expected = MessageAuthenticator(copy, secret);
        return CryptographicOperations.FixedTimeEquals(expected, packet.Attributes[index].Value.Span);
    }
}
