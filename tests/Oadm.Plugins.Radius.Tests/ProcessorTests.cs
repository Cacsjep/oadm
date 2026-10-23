using System.Net;
using System.Security.Cryptography;
using System.Text;
using Oadm.Plugins.Radius.Protocol.Eap;
using Oadm.Plugins.Radius.Protocol.Radius;
using Oadm.Plugins.Radius.Protocol.Sessions;
using Oadm.Plugins.Radius.Tests.Support;

namespace Oadm.Plugins.Radius.Tests;

/// <summary>
/// The processor's RADIUS rules (docs/spec/radius-server.md "Protocol", "Parsing and RFC compliance"): what is dropped and
/// why, the Message-Authenticator before anything else, Status-Server, duplicates, Proxy-State, State, non-EAP requests.
/// </summary>
public sealed class ProcessorTests
{
    private static readonly byte[] Secret = TestSwitch.DefaultSecret;
    private static readonly RadiusClient Client = new("switch-1", Secret);
    private static readonly IPEndPoint Source = new(IPAddress.Parse("10.0.0.2"), 51812);

    private static RadiusProcessor NewProcessor() => new(new RadiusProtocolOptions { ServerIdentity = TestPki.ServerIdentity }, new TestValidator());

    private static byte[] Identity(byte id = 1) => EapPacket.Response(id, EapType.Identity, "axis-accc8e123456"u8.ToArray()).ToBytes();

    private static byte[] Request(byte identifier, IReadOnlyList<RadiusAttribute> attributes, byte[]? authenticator = null, byte[]? secret = null, bool messageAuthenticator = true, RadiusCode code = RadiusCode.AccessRequest) =>
        RadiusPacketWriter.Request(code, identifier, authenticator ?? RandomNumberGenerator.GetBytes(16), secret ?? Secret, attributes, messageAuthenticator)!;

    [Fact]
    public void AMalformedDatagramIsDroppedWithItsReason()
    {
        var result = NewProcessor().Process(new byte[10], Client, Source);

        Assert.Equal(RadiusOutcome.Dropped, result.Outcome);
        Assert.Equal(RadiusDropReason.Malformed, result.Drop);
        Assert.Null(result.Response);
        Assert.Contains("shorter than 20", result.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RadiusCode.AccountingRequest)]
    [InlineData(RadiusCode.AccessAccept)]
    [InlineData(RadiusCode.AccessChallenge)]
    [InlineData(RadiusCode.StatusClient)]
    [InlineData((RadiusCode)200)]
    public void OnlyAccessRequestAndStatusServerAreAnswered(RadiusCode code)
    {
        var result = NewProcessor().Process(Request(1, [.. RadiusAttribute.EapMessages(Identity())], code: code), Client, Source);
        Assert.Equal(RadiusDropReason.NotARequest, result.Drop);
    }

    [Fact]
    public void ARequestWithoutMessageAuthenticatorIsDroppedAsWrongSecret()
    {
        var result = NewProcessor().Process(Request(1, [.. RadiusAttribute.EapMessages(Identity())], messageAuthenticator: false), Client, Source);

        Assert.Equal(RadiusDropReason.WrongSecret, result.Drop);
        Assert.Equal("No Message-Authenticator", result.Detail);
    }

    [Fact]
    public void ARequestSignedWithAnotherSecretIsDroppedAsWrongSecret()
    {
        var processor = NewProcessor();
        var result = processor.Process(Request(1, [.. RadiusAttribute.EapMessages(Identity())], secret: Encoding.ASCII.GetBytes("another-secret-xx")), Client, Source);

        Assert.Equal(RadiusDropReason.WrongSecret, result.Drop);
        Assert.Equal("Wrong Message-Authenticator", result.Detail);
        Assert.Equal(0, processor.SessionCount);
        Assert.Equal(0, processor.Duplicates.Count); // RFC 5080 2.2.2: invalid Message-Authenticators never affect the cache
    }

    [Fact]
    public void ThePasswordRequestWithoutMessageAuthenticatorIsDroppedNotChecked()
    {
        // Blast-RADIUS: the Message-Authenticator is required on every Access-Request, PAP included.
        var result = NewProcessor().Process(Request(1, [RadiusAttribute.Text(RadiusAttributeType.UserName, "u"), new RadiusAttribute(RadiusAttributeType.UserPassword, new byte[16])], messageAuthenticator: false), Client, Source);
        Assert.Equal(RadiusDropReason.WrongSecret, result.Drop);
    }

    [Fact]
    public void ARequestWithoutEapIsRejectedOnlyEapTls()
    {
        var authenticator = RandomNumberGenerator.GetBytes(16);
        var result = NewProcessor().Process(Request(4, [RadiusAttribute.Text(RadiusAttributeType.UserName, "u"), new RadiusAttribute(RadiusAttributeType.UserPassword, new byte[16])], authenticator), Client, Source);

        Assert.Equal(RadiusOutcome.Answered, result.Outcome);
        Assert.Equal("Only EAP-TLS is supported", result.Reason);
        var answer = RadiusPacket.Parse(result.Response!).Packet!;
        Assert.Equal(RadiusCode.AccessReject, answer.Code);
        Assert.Equal(4, answer.Identifier);
        Assert.Equal(RadiusAttributeType.MessageAuthenticator, answer.Attributes[0].Type);
        Assert.True(RadiusAuthenticators.VerifyResponse(answer, authenticator, Secret));
    }

    [Fact]
    public void StatusServerIsAnsweredWithAnAcceptAndNoCache()
    {
        // RFC 5997 3: Access-Accept with a Message-Authenticator, validated like an Access-Request; never forwarded or cached.
        var processor = NewProcessor();
        var authenticator = RandomNumberGenerator.GetBytes(16);
        var request = Request(218, [RadiusAttribute.Of(RadiusAttributeType.NasIpAddress, [192, 0, 2, 16])], authenticator, code: RadiusCode.StatusServer);

        var first = processor.Process(request, Client, Source);
        var second = processor.Process(request, Client, Source);

        Assert.Equal(RadiusOutcome.Answered, first.Outcome);
        Assert.Equal(RadiusOutcome.Answered, second.Outcome);
        var answer = RadiusPacket.Parse(first.Response!).Packet!;
        Assert.Equal(RadiusCode.AccessAccept, answer.Code);
        Assert.Equal(218, answer.Identifier);
        Assert.Equal([RadiusAttributeType.MessageAuthenticator], answer.Attributes.Select(a => a.Type));
        Assert.True(RadiusAuthenticators.VerifyResponse(answer, authenticator, Secret));
        Assert.Equal(0, processor.Duplicates.Count);
        Assert.Equal(0, processor.SessionCount);
    }

    [Fact]
    public void TheRfc5997StatusServerExampleIsAnswered()
    {
        var rfcClient = new RadiusClient("rfc", Encoding.ASCII.GetBytes("xyzzy5461"));
        var request = Hex.Bytes("0c da 00 26 8a 54 f4 68 6f b3 94 c5 28 66 e3 02 18 5d 06 23 50 12 5a 66 5e 2e 1e 84 11 f3 e2 43 82 20 97 c8 4f a3");

        var result = NewProcessor().Process(request, rfcClient, Source);

        var answer = RadiusPacket.Parse(result.Response!).Packet!;
        Assert.Equal(RadiusCode.AccessAccept, answer.Code);
        Assert.True(RadiusAuthenticators.VerifyResponse(answer, request.AsSpan(4, 16), rfcClient.Secret.Span));
    }

    [Fact]
    public void ARetransmissionGetsTheSameAnswerAndNoSecondStep()
    {
        var processor = NewProcessor();
        var changes = 0;
        processor.SessionChanged += _ => changes++;
        var request = Request(9, [.. RadiusAttribute.EapMessages(Identity())]);

        var first = processor.Process(request, Client, Source);
        var changesAfterFirst = changes;
        var again = processor.Process(request, Client, Source);

        Assert.Equal(RadiusOutcome.Answered, first.Outcome);
        Assert.Equal(RadiusOutcome.AnsweredFromCache, again.Outcome);
        Assert.Equal(first.Response, again.Response);
        Assert.Equal(changesAfterFirst, changes);
        Assert.Equal(1, processor.SessionCount);
    }

    [Fact]
    public void TheSameIdentifierWithANewAuthenticatorIsANewRequest()
    {
        var processor = NewProcessor();
        processor.Process(Request(9, [.. RadiusAttribute.EapMessages(Identity())]), Client, Source);
        var other = processor.Process(Request(9, [.. RadiusAttribute.EapMessages(Identity())]), Client, Source);

        Assert.Equal(RadiusOutcome.Answered, other.Outcome);
        Assert.Equal(2, processor.SessionCount);
    }

    [Fact]
    public void ProxyStateIsEchoedUnchangedAndInOrder()
    {
        // RFC 2865 5.33, through a whole conversation (the test switch checks every answer).
        var processor = NewProcessor();
        var camera = new TestSupplicant();
        var conversation = new TestSwitch(processor) { ProxyStates = [[0xAA, 1], [0x00], [0xFF, 0xFE, 0xFD]] }.Run(camera);

        Assert.Equal(RadiusCode.AccessAccept, conversation.Final);
        var echoed = conversation.LastAnswer!.All(RadiusAttributeType.ProxyState).Select(p => p.Value.ToArray()).ToList();
        Assert.Equal([new byte[] { 0xAA, 1 }, [0x00], [0xFF, 0xFE, 0xFD]], echoed);
        Assert.Equal(RadiusAttributeType.ProxyState, conversation.LastAnswer.Attributes[^1].Type);
    }

    [Fact]
    public void AnUnknownStateIsRejectedSessionNotFound()
    {
        var result = NewProcessor().Process(Request(2, [.. RadiusAttribute.EapMessages(Identity(5)), RadiusAttribute.Of(RadiusAttributeType.State, [1, 2, 3])]), Client, Source);

        Assert.Equal("Session not found or expired", result.Reason);
        var answer = RadiusPacket.Parse(result.Response!).Packet!;
        Assert.Equal(RadiusCode.AccessReject, answer.Code);
        var failure = EapPacket.Parse(TestSwitch.Join(answer)).Packet!;
        Assert.Equal(EapCode.Failure, failure.Code);
        Assert.Equal(5, failure.Identifier);
    }

    [Fact]
    public void AStateOfAnotherSwitchIsNotFound()
    {
        var processor = NewProcessor();
        var first = processor.Process(Request(2, [.. RadiusAttribute.EapMessages(Identity())]), Client, Source);
        var state = RadiusPacket.Parse(first.Response!).Packet!.Find(RadiusAttributeType.State)!.Value.Value.ToArray();

        var other = new RadiusClient("switch-2", Secret);
        var result = processor.Process(Request(3, [.. RadiusAttribute.EapMessages(Identity(2)), RadiusAttribute.Of(RadiusAttributeType.State, state)]), other, Source);

        Assert.Equal("Session not found or expired", result.Reason);
    }

    [Fact]
    public void EveryChallengeCarriesTheSessionsState()
    {
        var processor = NewProcessor();
        var radiusSwitch = new TestSwitch(processor);
        var conversation = radiusSwitch.Run(new TestSupplicant());

        var states = conversation.Results.Where(r => r.Response is not null)
            .Select(r => RadiusPacket.Parse(r.Response!).Packet!)
            .Where(p => p.Code == RadiusCode.AccessChallenge)
            .Select(p => Convert.ToHexString(p.Find(RadiusAttributeType.State)!.Value.Value.Span))
            .Distinct()
            .ToList();
        var state = Assert.Single(states);
        Assert.Equal(32, state.Length); // 16 random bytes
    }

    [Fact]
    public void ARequestFromTheSwitchAsIfItWereThePeerIsRejectedWithANak()
    {
        // RFC 3579 2.6.2: an Access-Request with an EAP-Request gets Access-Reject with EAP-Response/Nak, no alternative.
        var result = NewProcessor().Process(Request(2, [.. RadiusAttribute.EapMessages(EapPacket.Request(4, EapType.Md5Challenge, new byte[] { 16 }).ToBytes())]), Client, Source);

        var answer = RadiusPacket.Parse(result.Response!).Packet!;
        Assert.Equal(RadiusCode.AccessReject, answer.Code);
        var nak = EapPacket.Parse(TestSwitch.Join(answer)).Packet!;
        Assert.Equal(EapCode.Response, nak.Code);
        Assert.Equal(EapType.Nak, nak.Type);
        Assert.Equal(new byte[] { 0 }, nak.Data.ToArray());
    }

    [Theory]
    [InlineData(new byte[] { 2, 1, 0, 9, 1 })] // EAP length larger than the attributes carried
    [InlineData(new byte[] { 2, 1, 0, 4, 1, 0 })] // EAP length smaller
    [InlineData(new byte[] { 3, 1, 0, 4 })] // EAP-Success from the switch
    [InlineData(new byte[] { 7, 1 })]
    public void AnUnreadableEapPacketIsRejectedWithEapFailure(byte[] eap)
    {
        var result = NewProcessor().Process(Request(2, [.. RadiusAttribute.EapMessages(eap)]), Client, Source);

        Assert.Equal("Message could not be read", result.Reason);
        var answer = RadiusPacket.Parse(result.Response!).Packet!;
        Assert.Equal(RadiusCode.AccessReject, answer.Code);
        Assert.Equal(EapCode.Failure, EapPacket.Parse(TestSwitch.Join(answer)).Packet!.Code);
    }

    [Fact]
    public void AnUnreadableEapPacketEndsItsSession()
    {
        var processor = NewProcessor();
        var first = processor.Process(Request(2, [.. RadiusAttribute.EapMessages(Identity())]), Client, Source);
        var state = RadiusPacket.Parse(first.Response!).Packet!.Find(RadiusAttributeType.State)!.Value.Value.ToArray();

        var result = processor.Process(Request(3, [.. RadiusAttribute.EapMessages(new byte[] { 2, 1, 0, 99 }), RadiusAttribute.Of(RadiusAttributeType.State, state)]), Client, Source);

        Assert.Equal(EapSessionState.Failed, result.Session!.State);
        Assert.Equal(0, processor.SessionCount);
    }

    [Fact]
    public void AMalformedRequestIsDroppedAfterTheMessageAuthenticatorCheck()
    {
        // Authentic (right secret) but two State attributes: dropped as malformed, nothing cached.
        var processor = NewProcessor();
        var result = processor.Process(Request(2, [.. RadiusAttribute.EapMessages(Identity()), RadiusAttribute.Of(RadiusAttributeType.State, [1]), RadiusAttribute.Of(RadiusAttributeType.State, [2])]), Client, Source);

        Assert.Equal(RadiusDropReason.Malformed, result.Drop);
        Assert.Equal(0, processor.Duplicates.Count);
    }

    [Fact]
    public void AnAnswerTooLargeForRadiusIsDropped()
    {
        // An EAP-Start with Proxy-States of 4,047 bytes fits a request (4,087 bytes) but not the Access-Challenge (Identity request and State on top: 4,110).
        var proxies = Enumerable.Range(0, 15).Select(_ => new RadiusAttribute(RadiusAttributeType.ProxyState, new byte[253])).Append(new RadiusAttribute(RadiusAttributeType.ProxyState, new byte[220])).ToList();
        var result = NewProcessor().Process(Request(2, [.. RadiusAttribute.EapMessages(Array.Empty<byte>()), .. proxies]), Client, Source);

        Assert.Equal(RadiusDropReason.AnswerTooLarge, result.Drop);
    }

    [Fact]
    public void WithoutAServerCertificateTheHandshakeIsRefused()
    {
        var processor = new RadiusProcessor(new RadiusProtocolOptions(), new TestValidator());
        var conversation = new TestSwitch(processor).Run(new TestSupplicant());

        Assert.Equal(RadiusCode.AccessReject, conversation.Final);
        Assert.Equal("The 802.1X server has no certificate", conversation.Session!.Reason);
    }

    [Fact]
    public void ARenewedServerCertificateAppliesToASessionWhoseHandshakeStartsLater()
    {
        // The session started without a certificate; the renewal arrives before its handshake starts.
        var processor = new RadiusProcessor(new RadiusProtocolOptions(), new TestValidator());
        var radiusSwitch = new TestSwitch(processor);
        var camera = new TestSupplicant();
        radiusSwitch.Run(camera, stopWhen: r => r.Session?.State == EapSessionState.StartingTls);

        processor.Options = processor.Options with { ServerIdentity = TestPki.ServerIdentity };

        Assert.Equal(RadiusCode.AccessAccept, radiusSwitch.Run(camera, resume: true).Final);
    }

    [Fact]
    public void TheOptionsCanBeReplacedWhileTheServerRuns()
    {
        var processor = new RadiusProcessor(new RadiusProtocolOptions(), new TestValidator());
        processor.Options = processor.Options with { ServerIdentity = TestPki.ServerIdentity };

        Assert.Equal(RadiusCode.AccessAccept, new TestSwitch(processor).Run(new TestSupplicant()).Final);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RadiusProtocolOptions { LargestFragment = 499 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new RadiusProtocolOptions { LargestFragment = 1401 });
        Assert.Throws<ArgumentException>(() => new RadiusClient("x", ReadOnlyMemory<byte>.Empty));
        Assert.Equal("switch-1", Client.ToString());
    }
}
