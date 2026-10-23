using Oadm.Plugins.Radius.Protocol.Eap;
using Oadm.Plugins.Radius.Protocol.Sessions;

namespace Oadm.Plugins.Radius.Tests;

/// <summary>EAP packets (RFC 3748 4) and the EAP-TLS header (RFC 5216 3.1, 3.2).</summary>
public sealed class EapTests
{
    [Fact]
    public void AnIdentityResponseRoundTrips()
    {
        var bytes = EapPacket.Response(7, EapType.Identity, "axis"u8.ToArray()).ToBytes();

        Assert.Equal(new byte[] { 2, 7, 0, 9, 1, (byte)'a', (byte)'x', (byte)'i', (byte)'s' }, bytes);
        var parsed = EapPacket.Parse(bytes).Packet!;
        Assert.Equal(EapCode.Response, parsed.Code);
        Assert.Equal(7, parsed.Identifier);
        Assert.Equal(EapType.Identity, parsed.Type);
        Assert.Equal("axis"u8.ToArray(), parsed.Data.ToArray());
    }

    [Fact]
    public void SuccessAndFailureHaveFourBytesAndTheResponsesIdentifier()
    {
        // RFC 3748 4.2.
        Assert.Equal(new byte[] { 3, 9, 0, 4 }, EapPacket.Success(9).ToBytes());
        Assert.Equal(new byte[] { 4, 9, 0, 4 }, EapPacket.Failure(9).ToBytes());
        Assert.Equal(EapCode.Success, EapPacket.Parse([3, 9, 0, 4]).Packet!.Code);
        Assert.Equal(EapMalformation.BadCode, EapPacket.Parse([3, 9, 0, 5, 0]).Error);
    }

    [Theory]
    [InlineData(new byte[] { 2, 1, 0 }, EapMalformation.TooShort)]
    [InlineData(new byte[] { 2, 1, 0, 4 }, EapMalformation.TooShort)] // a Response without a Type
    [InlineData(new byte[] { 2, 1, 0, 6, 1 }, EapMalformation.LengthMismatch)] // shorter than its Length
    [InlineData(new byte[] { 2, 1, 0, 5, 1, 0 }, EapMalformation.LengthMismatch)] // longer than its Length (docs/spec: must match)
    [InlineData(new byte[] { 5, 1, 0, 5, 1 }, EapMalformation.BadCode)]
    [InlineData(new byte[] { 0, 1, 0, 5, 1 }, EapMalformation.BadCode)]
    public void MalformedEapPacketsAreAResultNeverAnException(byte[] bytes, EapMalformation error)
    {
        var result = EapPacket.Parse(bytes);
        Assert.Null(result.Packet);
        Assert.Equal(error, result.Error);
    }

    [Fact]
    public void TheEapTlsStartHasOnlyTheSFlag()
    {
        Assert.Equal(new byte[] { 0x20 }, EapTlsMessage.StartMessage.ToBytes());
        Assert.True(EapTlsMessage.Parse([0x20])!.IsStart);
    }

    [Fact]
    public void AFirstFragmentCarriesTheTotalLength()
    {
        var message = new EapTlsMessage(EapTlsBits.Length | EapTlsBits.More, 3000, new byte[] { 1, 2 });
        var bytes = message.ToBytes();

        Assert.Equal(new byte[] { 0xC0, 0, 0, 0x0B, 0xB8, 1, 2 }, bytes);
        var parsed = EapTlsMessage.Parse(bytes)!;
        Assert.True(parsed.HasLength);
        Assert.True(parsed.HasMore);
        Assert.Equal(3000u, parsed.TotalLength);
        Assert.Equal(new byte[] { 1, 2 }, parsed.Data.ToArray());
    }

    [Fact]
    public void ReservedFlagBitsAreIgnored()
    {
        // RFC 5216 3.1: "MUST ignore them on reception".
        var parsed = EapTlsMessage.Parse([0x1F, 9])!;
        Assert.Equal(EapTlsBits.None, parsed.Flags);
        Assert.Equal(new byte[] { 9 }, parsed.Data.ToArray());
    }

    [Fact]
    public void AnEmptyMessageIsAnAcknowledgement()
    {
        Assert.True(EapTlsMessage.Parse([0])!.IsAcknowledgement);
        Assert.False(EapTlsMessage.Parse([0x40])!.IsAcknowledgement);
        Assert.Equal(new byte[] { 0 }, EapTlsMessage.Acknowledgement.ToBytes());
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x80 })]
    [InlineData(new byte[] { 0x80, 0, 0, 1 })]
    public void AnEapTlsMessageWithoutFlagsOrWithACutLengthIsUnreadable(byte[] typeData)
    {
        Assert.Null(EapTlsMessage.Parse(typeData));
    }

    [Theory]
    [InlineData(new byte[] { 25 }, "The device does not support EAP-TLS (it asked for PEAP)")]
    [InlineData(new byte[] { 21, 26 }, "The device does not support EAP-TLS (it asked for EAP-TTLS, EAP-MSCHAPv2)")]
    [InlineData(new byte[] { 0 }, "The device does not support EAP-TLS (it asked for no other method)")]
    [InlineData(new byte[0], "The device does not support EAP-TLS (it asked for no other method)")]
    [InlineData(new byte[] { 4 }, "The device does not support EAP-TLS (it asked for MD5)")]
    [InlineData(new byte[] { 99 }, "The device does not support EAP-TLS (it asked for type 99)")]
    public void ANakNamesTheMethodsTheDeviceAskedFor(byte[] desired, string text)
    {
        Assert.Equal(text, EapSessionTexts.NotEapTls(desired));
    }
}
