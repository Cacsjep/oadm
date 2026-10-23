using System.Text;
using Oadm.Plugins.Radius.Protocol.Radius;
using Oadm.Plugins.Radius.Tests.Support;

namespace Oadm.Plugins.Radius.Tests;

/// <summary>
/// The RADIUS codec against the RFC test vectors (RFC 2865 7 with the secret "xyzzy5461"; RFC 5997 6 for the
/// Message-Authenticator, which RFC 2869 and RFC 3579 give no hex dumps for) and every framing rule of
/// docs/spec/radius-server.md "Parsing and RFC compliance".
/// </summary>
public sealed class RadiusCodecTests
{
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("xyzzy5461");

    // RFC 2865 7.1: Access-Request of "nemo" and the Access-Accept.
    private const string Rfc2865Request71 = "01 00 00 38 0f 40 3f 94 73 97 80 57 bd 83 d5 cb 98 f4 22 7a 01 06 6e 65 6d 6f 02 12 0d be 70 8d 93 d4 13 ce 31 96 e4 3f 78 2a 0a ee 04 06 c0 a8 01 10 05 06 00 00 00 03";
    private const string Rfc2865Accept71 = "02 00 00 26 86 fe 22 0e 76 24 ba 2a 10 05 f6 bf 9b 55 e0 b2 06 06 00 00 00 01 0f 06 00 00 00 00 0e 06 c0 a8 01 03";

    // RFC 2865 7.2: CHAP request of "flopsy" and the Access-Accept.
    private const string Rfc2865Request72 = "01 01 00 47 2a ee 86 f0 8d 0d 55 96 9c a5 97 8e 0d 33 67 a2 01 08 66 6c 6f 70 73 79 03 13 16 e9 75 57 c3 16 18 58 95 f2 93 ff 63 44 07 72 75 04 06 c0 a8 01 10 05 06 00 00 00 14 06 06 00 00 00 02 07 06 00 00 00 01";
    private const string Rfc2865Accept72 = "02 01 00 38 15 ef bc 7d ab 26 cf a3 dc 34 d9 c0 3c 86 01 a4 06 06 00 00 00 02 07 06 00 00 00 01 08 06 ff ff ff fe 0a 06 00 00 00 02 0d 06 00 00 00 01 0c 06 00 00 05 dc";

    // RFC 2865 7.3: challenge-response of "mopsy": request, Access-Challenge with State, second request, Access-Reject.
    private const string Rfc2865Request73 = "01 02 00 39 f3 a4 7a 1f 6a 6d 76 71 0b 94 7a b9 30 41 a0 39 01 07 6d 6f 70 73 79 02 12 33 65 75 73 77 82 89 b5 70 88 5e 15 08 48 25 c5 04 06 c0 a8 01 10 05 06 00 00 00 07";
    private const string Rfc2865Challenge73 = "0b 02 00 4e 36 f3 c8 76 4a e8 c7 11 57 40 3c 0c 71 ff 9c 45 12 30 43 68 61 6c 6c 65 6e 67 65 20 33 32 37 36 39 34 33 30 2e 20 20 45 6e 74 65 72 20 72 65 73 70 6f 6e 73 65 20 61 74 20 70 72 6f 6d 70 74 2e 18 0a 33 32 37 36 39 34 33 30";
    private const string Rfc2865Request73b = "01 03 00 43 b1 22 55 6d 42 8a 13 d0 d6 25 38 07 c4 57 ec f0 01 07 6d 6f 70 73 79 02 12 69 2c 1f 20 5f c0 81 b9 19 b9 51 95 f5 61 a5 81 04 06 c0 a8 01 10 05 06 00 00 00 07 18 10 33 32 37 36 39 34 33 30";
    private const string Rfc2865Reject73 = "03 03 00 14 a4 2f 4f ca 45 91 6c 4e 09 c8 34 0f 9e 74 6a a0";

    // RFC 5997 6.1 and 6.3: Status-Server with a Message-Authenticator and the Access-Accept.
    private const string Rfc5997Status61 = "0c da 00 26 8a 54 f4 68 6f b3 94 c5 28 66 e3 02 18 5d 06 23 50 12 5a 66 5e 2e 1e 84 11 f3 e2 43 82 20 97 c8 4f a3";
    private const string Rfc5997Accept61 = "02 da 00 14 ef 0d 55 2a 4b f2 d6 93 ec 2b 6f e8 b5 41 1d 66";
    private const string Rfc5997Status63 = "0c 47 00 2c bf 58 de 56 ae 40 8a d3 b7 0c 85 13 f9 b0 3f be 04 06 c0 00 02 10 50 12 85 2d 6f ec 61 e7 ed 74 b8 e3 2d ac 2f 2a 5f b2";
    private const string Rfc5997Accept63 = "02 47 00 34 46 f4 3e 62 fd 03 54 42 4c bb eb fd 6d 21 4e 06 12 20 52 41 44 49 55 53 20 53 65 72 76 65 72 20 75 70 20 32 20 64 61 79 73 2c 20 31 38 3a 34 30";

    [Fact]
    public void TheRfc2865AccessRequestParsesWithItsAttributesInOrder()
    {
        var packet = RadiusPacket.Parse(Hex.Bytes(Rfc2865Request71)).Packet!;

        Assert.Equal(RadiusCode.AccessRequest, packet.Code);
        Assert.Equal(0, packet.Identifier);
        Assert.Equal(56, packet.Bytes.Length);
        Assert.Equal([RadiusAttributeType.UserName, RadiusAttributeType.UserPassword, RadiusAttributeType.NasIpAddress, RadiusAttributeType.NasPort], packet.Attributes.Select(a => a.Type));
        Assert.Equal("nemo", Encoding.ASCII.GetString(packet.Attributes[0].Value.Span));
        Assert.Equal(16, packet.Attributes[1].Value.Length);
        Assert.Equal([192, 168, 1, 16], packet.Attributes[2].Value.ToArray());
        Assert.Equal(new byte[] { 0, 0, 0, 3 }, packet.Attributes[3].Value.ToArray());
    }

    [Theory]
    [InlineData(Rfc2865Request71, Rfc2865Accept71)]
    [InlineData(Rfc2865Request72, Rfc2865Accept72)]
    [InlineData(Rfc2865Request73, Rfc2865Challenge73)]
    [InlineData(Rfc2865Request73b, Rfc2865Reject73)]
    [InlineData(Rfc5997Status61, Rfc5997Accept61)]
    [InlineData(Rfc5997Status63, Rfc5997Accept63)]
    public void TheResponseAuthenticatorMatchesTheRfcExamples(string requestDump, string answerDump)
    {
        // RFC 2865 3: ResponseAuth = MD5(Code + ID + Length + RequestAuth + Attributes + Secret). The Request Authenticator is
        // taken from the dump itself: the second request of RFC 2865 7.3 is printed with a broken State length (below).
        var requestAuthenticator = Hex.Bytes(requestDump)[4..20];
        var answer = RadiusPacket.Parse(Hex.Bytes(answerDump)).Packet!;
        var copy = answer.Bytes.ToArray();
        requestAuthenticator.CopyTo(copy.AsSpan(4));

        Assert.Equal(answer.Authenticator.ToArray(), RadiusAuthenticators.ResponseAuthenticator(copy, RfcSecret));
        Assert.True(RadiusAuthenticators.VerifyResponse(answer, requestAuthenticator, RfcSecret));
        Assert.False(RadiusAuthenticators.VerifyResponse(answer, requestAuthenticator, Encoding.ASCII.GetBytes("xyzzy5462")));
    }

    [Fact]
    public void TheSecondRequestOfRfc2865Section73IsMalformedAsPrintedAndDropped()
    {
        // The dump says State "18 10" (length 16) but carries 8 octets; the Length field (67) fits a State of length 10.
        // The parser refuses an attribute that runs past the packet (RFC 2865 5).
        var result = RadiusPacket.Parse(Hex.Bytes(Rfc2865Request73b));
        Assert.Equal(RadiusMalformation.BadAttribute, result.Error);

        var fixedDump = Hex.Bytes(Rfc2865Request73b);
        fixedDump[^9] = 0x0A;
        Assert.Equal(new byte[] { 0x33, 0x32, 0x37, 0x36, 0x39, 0x34, 0x33, 0x30 }, RadiusPacket.Parse(fixedDump).Packet!.Find(RadiusAttributeType.State)!.Value.Value.ToArray());
    }

    [Theory]
    [InlineData(Rfc5997Status61)]
    [InlineData(Rfc5997Status63)]
    public void TheMessageAuthenticatorOfTheRfc5997RequestsIsVerified(string dump)
    {
        // RFC 3579 3.2: HMAC-MD5 over the packet with the Message-Authenticator as 16 zero octets.
        var packet = RadiusPacket.Parse(Hex.Bytes(dump)).Packet!;

        Assert.True(RadiusAuthenticators.VerifyRequest(packet, RfcSecret));
        Assert.False(RadiusAuthenticators.VerifyRequest(packet, Encoding.ASCII.GetBytes("xyzzy546")));

        var tampered = Hex.Bytes(dump);
        tampered[5] ^= 1; // one bit of the Request Authenticator
        Assert.False(RadiusAuthenticators.VerifyRequest(RadiusPacket.Parse(tampered).Packet!, RfcSecret));
    }

    [Fact]
    public void ARequestWithoutAMessageAuthenticatorIsNotVerified()
    {
        var packet = RadiusPacket.Parse(Hex.Bytes(Rfc2865Request71)).Packet!;
        Assert.False(RadiusAuthenticators.VerifyRequest(packet, RfcSecret));
    }

    [Fact]
    public void TheWriterPutsTheMessageAuthenticatorFirstAndSignsTheAnswer()
    {
        var request = RadiusPacket.Parse(Hex.Bytes(Rfc5997Status61)).Packet!;
        var bytes = RadiusPacketWriter.Response(RadiusCode.AccessAccept, request.Identifier, request.Authenticator.Span, RfcSecret,
            [RadiusAttribute.Text(RadiusAttributeType.ReplyMessage, "up"), RadiusAttribute.Of(RadiusAttributeType.ProxyState, [1, 2])])!;
        var answer = RadiusPacket.Parse(bytes).Packet!;

        Assert.Equal([RadiusAttributeType.MessageAuthenticator, RadiusAttributeType.ReplyMessage, RadiusAttributeType.ProxyState], answer.Attributes.Select(a => a.Type));
        Assert.Equal(18, answer.Bytes.Span[21]);
        Assert.True(RadiusAuthenticators.VerifyResponse(answer, request.Authenticator.Span, RfcSecret));

        // The Message-Authenticator of the answer is over the Request Authenticator (RFC 3579 3.2).
        var tampered = bytes.ToArray();
        tampered[^1] ^= 1;
        Assert.False(RadiusAuthenticators.VerifyResponse(RadiusPacket.Parse(tampered).Packet!, request.Authenticator.Span, RfcSecret));
    }

    [Fact]
    public void ARequestOfTheWriterIsVerifiedByTheServer()
    {
        var bytes = RadiusPacketWriter.Request(RadiusCode.AccessRequest, 7, new byte[16], RfcSecret, [RadiusAttribute.Text(RadiusAttributeType.UserName, "x")])!;
        Assert.True(RadiusAuthenticators.VerifyRequest(RadiusPacket.Parse(bytes).Packet!, RfcSecret));
    }

    [Fact]
    public void AnAnswerLargerThan4096BytesIsNotWritten()
    {
        var attributes = Enumerable.Range(0, 17).Select(_ => new RadiusAttribute(RadiusAttributeType.ProxyState, new byte[253])).ToList();
        Assert.Null(RadiusPacketWriter.Response(RadiusCode.AccessReject, 1, new byte[16], RfcSecret, attributes));
        Assert.NotNull(RadiusPacketWriter.Response(RadiusCode.AccessReject, 1, new byte[16], RfcSecret, attributes[..15]));
    }

    [Fact]
    public void AnAttributeValueOver253BytesIsAProgrammingError()
    {
        Assert.Throws<ArgumentException>(() => RadiusPacketWriter.Response(RadiusCode.AccessReject, 1, new byte[16], RfcSecret, [new RadiusAttribute(1, new byte[254])]));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(253, 1)]
    [InlineData(254, 2)]
    [InlineData(1500, 6)]
    public void EapPacketsAreSplitIntoAttributesOfAtMost253Bytes(int length, int attributes)
    {
        // RFC 3579 3.1: one EAP packet over consecutive EAP-Message attributes; EAP-Start is one attribute of length 2.
        var eap = Enumerable.Range(0, length).Select(i => (byte)i).ToArray();
        var split = RadiusAttribute.EapMessages(eap);

        Assert.Equal(attributes, split.Count);
        Assert.All(split, a => Assert.True(a.Value.Length <= 253));
        Assert.Equal(eap, split.SelectMany(a => a.Value.ToArray()));
    }

    // ---- Framing rules (RFC 2865 3, 5) ----

    [Theory]
    [InlineData(0)]
    [InlineData(19)]
    public void ADatagramShorterThan20BytesIsMalformed(int length)
    {
        var result = RadiusPacket.Parse(new byte[length]);
        Assert.True(result.IsMalformed);
        Assert.Equal(RadiusMalformation.TooShort, result.Error);
    }

    [Theory]
    [InlineData(19)]
    [InlineData(4097)]
    [InlineData(0)]
    [InlineData(65535)]
    public void ALengthFieldOutside20To4096IsMalformed(int length)
    {
        var bytes = new byte[Math.Max(length, 20)];
        bytes[0] = 1;
        bytes[2] = (byte)(length >> 8);
        bytes[3] = (byte)length;
        Assert.Equal(RadiusMalformation.LengthOutOfRange, RadiusPacket.Parse(bytes).Error);
    }

    [Fact]
    public void ADatagramShorterThanItsLengthFieldIsMalformed()
    {
        var bytes = Hex.Bytes(Rfc2865Request71);
        Assert.Equal(RadiusMalformation.Truncated, RadiusPacket.Parse(bytes.AsSpan(0, bytes.Length - 1)).Error);
    }

    [Fact]
    public void BytesAfterTheLengthFieldArePaddingAndIgnored()
    {
        // RFC 2865 3: "Octets outside the range of the Length field MUST be treated as padding and ignored".
        var bytes = Hex.Bytes(Rfc5997Status61).Concat(new byte[] { 0xFF, 0xFF, 0x01 }).ToArray();
        var packet = RadiusPacket.Parse(bytes).Packet!;

        Assert.Equal(38, packet.Bytes.Length);
        Assert.Single(packet.Attributes);
        Assert.True(RadiusAuthenticators.VerifyRequest(packet, RfcSecret));
    }

    [Fact]
    public void ThePacketOf4096BytesIsAccepted()
    {
        var attributes = Enumerable.Range(0, 15).Select(_ => new RadiusAttribute(RadiusAttributeType.ProxyState, new byte[253])).ToList();
        attributes.Add(new RadiusAttribute(RadiusAttributeType.ProxyState, new byte[4096 - 20 - 18 - (15 * 255) - 2]));
        var bytes = RadiusPacketWriter.Request(RadiusCode.AccessRequest, 1, new byte[16], RfcSecret, attributes)!;

        Assert.Equal(4096, bytes.Length);
        Assert.False(RadiusPacket.Parse(bytes).IsMalformed);
    }

    [Theory]
    [InlineData(new byte[] { 1, 0 })] // length 0
    [InlineData(new byte[] { 1, 1 })] // length 1
    [InlineData(new byte[] { 1, 9, 0 })] // runs past the packet
    [InlineData(new byte[] { 1 })] // header cut off
    public void AnAttributeWithABadLengthIsMalformed(byte[] attribute)
    {
        var bytes = Packet(attribute);
        Assert.Equal(RadiusMalformation.BadAttribute, RadiusPacket.Parse(bytes).Error);
    }

    [Fact]
    public void AnAttributeOfLength2IsAccepted()
    {
        var packet = RadiusPacket.Parse(Packet([RadiusAttributeType.EapMessage, 2])).Packet!;
        Assert.Single(packet.Attributes);
        Assert.True(packet.Attributes[0].Value.IsEmpty);
    }

    [Fact]
    public void UnknownAndVendorAttributesAreKeptRawAndRepeatedOnesInOrder()
    {
        var packet = RadiusPacket.Parse(Packet(
            [200, 4, 0xAA, 0xBB],
            [RadiusAttributeType.VendorSpecific, 10, 0, 0, 0, 9, 1, 4, 0xCC, 0xDD],
            [RadiusAttributeType.ProxyState, 3, 1],
            [RadiusAttributeType.ProxyState, 3, 2])).Packet!;

        Assert.Equal([200, RadiusAttributeType.VendorSpecific, RadiusAttributeType.ProxyState, RadiusAttributeType.ProxyState], packet.Attributes.Select(a => a.Type));
        Assert.Equal([0xAA, 0xBB], packet.Attributes[0].Value.ToArray());
        Assert.Equal([new byte[] { 1 }, new byte[] { 2 }], packet.All(RadiusAttributeType.ProxyState).Select(a => a.Value.ToArray()));
        Assert.Equal(2, packet.Count(RadiusAttributeType.ProxyState));
    }

    /// <summary>A request header (code 1, length set) followed by the given attributes.</summary>
    internal static byte[] Packet(params byte[][] attributes)
    {
        var body = attributes.SelectMany(a => a).ToArray();
        var bytes = new byte[20 + body.Length];
        bytes[0] = 1;
        bytes[1] = 9;
        bytes[2] = (byte)(bytes.Length >> 8);
        bytes[3] = (byte)bytes.Length;
        body.CopyTo(bytes, 20);
        return bytes;
    }
}
