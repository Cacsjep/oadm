using System.Text;
using Oadm.Plugins.Radius.Protocol.Radius;

namespace Oadm.Plugins.Radius.Tests;

/// <summary>
/// The Access-Request reader (docs/spec/radius-server.md "Required in each Access-Request", "Port", "MAC address",
/// "Text attributes"): every optional attribute may be missing, the strict rules of RFC 3579 and RFC 2865 5.44 drop the packet.
/// </summary>
public sealed class AccessRequestTests
{
    private static readonly byte[] Secret = Encoding.ASCII.GetBytes("0123456789abcdef");

    private static AccessRequestReadResult Read(params RadiusAttribute[] attributes) =>
        AccessRequest.Read(RadiusPacket.Parse(RadiusPacketWriter.Request(RadiusCode.AccessRequest, 1, new byte[16], Secret, attributes)!).Packet!);

    private static RadiusAttribute Eap(params byte[] bytes) => new(RadiusAttributeType.EapMessage, bytes);

    [Fact]
    public void ARequestWithOnlyEapAndTheMessageAuthenticatorHasEveryOtherFieldEmpty()
    {
        var request = Read(Eap(2, 1, 0, 5, 1)).Request!;

        Assert.Equal(new byte[] { 2, 1, 0, 5, 1 }, request.Eap);
        Assert.Null(request.UserName);
        Assert.Null(request.NasIpAddress);
        Assert.Null(request.NasIpv6Address);
        Assert.Null(request.NasIdentifier);
        Assert.Null(request.NasPort);
        Assert.Null(request.NasPortId);
        Assert.Null(request.NasPortType);
        Assert.Null(request.CallingStationId);
        Assert.False(request.Mac.IsValid);
        Assert.Equal(string.Empty, request.Mac.Display);
        Assert.Null(request.CalledStationId);
        Assert.Null(request.FramedMtu);
        Assert.Null(request.State);
        Assert.Empty(request.ProxyStates);
        Assert.Equal(string.Empty, request.Port);
    }

    [Fact]
    public void EveryAttributeIsRead()
    {
        var request = Read(
            RadiusAttribute.Text(RadiusAttributeType.UserName, "axis-accc8e123456"),
            RadiusAttribute.Of(RadiusAttributeType.NasIpAddress, [10, 0, 0, 2]),
            RadiusAttribute.Of(RadiusAttributeType.NasIpv6Address, [0xFE, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1]),
            RadiusAttribute.Text(RadiusAttributeType.NasIdentifier, "core-sw-1"),
            RadiusAttribute.Number(RadiusAttributeType.NasPort, 50105),
            RadiusAttribute.Text(RadiusAttributeType.NasPortId, "GigabitEthernet1/0/5"),
            RadiusAttribute.Number(RadiusAttributeType.NasPortType, 15),
            RadiusAttribute.Text(RadiusAttributeType.CallingStationId, "AC-CC-8E-12-34-56"),
            RadiusAttribute.Text(RadiusAttributeType.CalledStationId, "00-10-A4-23-19-C0"),
            RadiusAttribute.Number(RadiusAttributeType.FramedMtu, 1500),
            Eap(2, 1, 0, 5, 1),
            RadiusAttribute.Of(RadiusAttributeType.State, [9, 9]),
            RadiusAttribute.Of(RadiusAttributeType.ProxyState, [1]),
            RadiusAttribute.Of(RadiusAttributeType.ProxyState, [2])).Request!;

        Assert.Equal("axis-accc8e123456", request.UserName);
        Assert.Equal("10.0.0.2", request.NasIpAddress!.ToString());
        Assert.Equal("fe80::1", request.NasIpv6Address!.ToString());
        Assert.Equal("core-sw-1", request.NasIdentifier);
        Assert.Equal(50105u, request.NasPort);
        Assert.Equal("GigabitEthernet1/0/5", request.Port);
        Assert.Equal(15u, request.NasPortType);
        Assert.Equal("AC:CC:8E:12:34:56", request.Mac.Normalized);
        Assert.Equal("00-10-A4-23-19-C0", request.CalledStationId);
        Assert.Equal(1500, request.FramedMtu);
        Assert.Equal(new byte[] { 9, 9 }, request.State);
        Assert.Equal([new byte[] { 1 }, new byte[] { 2 }], request.ProxyStates.Select(p => p.Value.ToArray()));
    }

    [Fact]
    public void ThePortIsNasPortIdElseNasPortElseEmpty()
    {
        Assert.Equal("Gi1/0/5", Read(Eap(2, 1, 0, 5, 1), RadiusAttribute.Text(RadiusAttributeType.NasPortId, "Gi1/0/5"), RadiusAttribute.Number(RadiusAttributeType.NasPort, 7)).Request!.Port);
        Assert.Equal("7", Read(Eap(2, 1, 0, 5, 1), RadiusAttribute.Number(RadiusAttributeType.NasPort, 7)).Request!.Port);
        Assert.Equal(string.Empty, Read(Eap(2, 1, 0, 5, 1)).Request!.Port);
    }

    [Fact]
    public void EapMessagesAreJoinedInOrder()
    {
        var request = Read(Eap(2, 1, 0, 7), Eap(13, 0), Eap(9)).Request!;
        Assert.Equal(new byte[] { 2, 1, 0, 7, 13, 0, 9 }, request.Eap);
    }

    [Fact]
    public void AnEmptyEapMessageIsEapStart()
    {
        Assert.Equal([], Read(Eap()).Request!.Eap!);
    }

    [Fact]
    public void ARequestWithoutEapHasNoEap()
    {
        Assert.Null(Read(RadiusAttribute.Text(RadiusAttributeType.UserName, "x")).Request!.Eap);
    }

    [Fact]
    public void EapMessagesThatAreNotConsecutiveAreMalformed()
    {
        // RFC 3579 3.1: "they MUST be consecutive attributes".
        var result = Read(Eap(2, 1), RadiusAttribute.Text(RadiusAttributeType.UserName, "x"), Eap(0, 5, 1));
        Assert.Equal(RadiusMalformation.BadEapMessage, result.Error);
    }

    [Theory]
    [InlineData(RadiusAttributeType.UserPassword)]
    [InlineData(RadiusAttributeType.ChapPassword)]
    [InlineData(RadiusAttributeType.ArapPassword)]
    public void EapTogetherWithAPasswordIsMalformed(byte password)
    {
        // RFC 3579 3.3 note 1: not more than one of User-Password, CHAP-Password, ARAP-Password, EAP-Message.
        var result = Read(Eap(2, 1, 0, 5, 1), RadiusAttribute.Of(password, new byte[16]));
        Assert.Equal(RadiusMalformation.BadEapMessage, result.Error);
    }

    [Fact]
    public void TwoMessageAuthenticatorsAreMalformed()
    {
        var bytes = RadiusPacketWriter.Request(RadiusCode.AccessRequest, 1, new byte[16], Secret, [Eap(2, 1, 0, 5, 1), new RadiusAttribute(250, new byte[16])])!;
        bytes[^18] = RadiusAttributeType.MessageAuthenticator;
        var result = AccessRequest.Read(RadiusPacket.Parse(bytes).Packet!);
        Assert.Equal(RadiusMalformation.BadMessageAuthenticator, result.Error);
    }

    [Fact]
    public void AMissingMessageAuthenticatorIsItsOwnCase()
    {
        var bytes = RadiusPacketWriter.Request(RadiusCode.AccessRequest, 1, new byte[16], Secret, [Eap(2, 1, 0, 5, 1)], messageAuthenticator: false)!;
        var result = AccessRequest.Read(RadiusPacket.Parse(bytes).Packet!);
        Assert.True(result.MissingMessageAuthenticator);
    }

    [Theory]
    [InlineData(RadiusAttributeType.State, 0)]
    [InlineData(RadiusAttributeType.FramedMtu, 2)]
    [InlineData(RadiusAttributeType.FramedMtu, 5)]
    [InlineData(RadiusAttributeType.NasPort, 3)]
    [InlineData(RadiusAttributeType.NasPortType, 8)]
    [InlineData(RadiusAttributeType.NasIpAddress, 16)]
    [InlineData(RadiusAttributeType.NasIpv6Address, 4)]
    public void AnAttributeOfTheWrongSizeIsMalformed(byte type, int size)
    {
        Assert.Equal(RadiusMalformation.BadAttributeValue, Read(Eap(2, 1, 0, 5, 1), new RadiusAttribute(type, new byte[size])).Error);
    }

    [Theory]
    [InlineData(RadiusAttributeType.State)]
    [InlineData(RadiusAttributeType.FramedMtu)]
    [InlineData(RadiusAttributeType.NasPort)]
    public void ARepeatedSingleAttributeIsMalformed(byte type)
    {
        var value = type == RadiusAttributeType.State ? new byte[] { 1 } : [0, 0, 5, 0xDC];
        Assert.Equal(RadiusMalformation.BadAttributeValue, Read(Eap(2, 1, 0, 5, 1), new RadiusAttribute(type, value), new RadiusAttribute(type, value)).Error);
    }

    [Theory]
    [InlineData(63u)]
    [InlineData(65536u)]
    [InlineData(uint.MaxValue)]
    public void AFramedMtuOutside64To65535IsMalformed(uint mtu)
    {
        // RFC 2865 5.12: "64 to 65535".
        Assert.Equal(RadiusMalformation.BadAttributeValue, Read(Eap(2, 1, 0, 5, 1), RadiusAttribute.Number(RadiusAttributeType.FramedMtu, mtu)).Error);
    }

    [Fact]
    public void TextAttributesAreSanitized()
    {
        var request = Read(Eap(2, 1, 0, 5, 1),
            RadiusAttribute.Of(RadiusAttributeType.UserName, [(byte)'a', 0x1B, (byte)'[', (byte)'2', (byte)'J', 0xFF, (byte)'b', 0x0A]),
            RadiusAttribute.Text(RadiusAttributeType.NasIdentifier, "sw‮itch")).Request!;

        Assert.Equal("a[2J�b", request.UserName);
        Assert.Equal("switch", request.NasIdentifier);
    }

    [Fact]
    public void RepeatedTextAttributesTakeTheFirst()
    {
        Assert.Equal("first", Read(Eap(2, 1, 0, 5, 1), RadiusAttribute.Text(RadiusAttributeType.UserName, "first"), RadiusAttribute.Text(RadiusAttributeType.UserName, "second")).Request!.UserName);
    }

    [Fact]
    public void UnknownAndVendorAttributesAreSkipped()
    {
        var result = Read(new RadiusAttribute(RadiusAttributeType.VendorSpecific, new byte[] { 0, 0, 0, 9, 1, 3, 7 }), new RadiusAttribute(240, new byte[] { 1 }), Eap(2, 1, 0, 5, 1));
        Assert.NotNull(result.Request);
    }
}
