using System.Buffers.Binary;

using Oadm.Plugins.DhcpServer.Protocol;

namespace Oadm.Plugins.DhcpServer.Tests;

public sealed class ProtocolTests
{
    private static DhcpMessage Full() => new()
    {
        Op = DhcpMessage.BootReply,
        Hops = 1,
        TransactionId = 0xDEADBEEF,
        Seconds = 7,
        Flags = DhcpMessage.BroadcastFlag,
        ClientAddress = Ip.Of("10.0.0.5"),
        YourAddress = Ip.Of("10.0.0.101"),
        ServerAddress = Ip.Of("10.0.0.17"),
        RelayAddress = Ip.Of("10.0.1.1"),
        Mac = Mac.Of("AC:CC:8E:5F:60:71"),
        MessageType = DhcpMessageType.Ack,
        SubnetMask = Ip.Of("255.255.255.0"),
        Routers = [Ip.Of("10.0.0.138")],
        DnsServers = [Ip.Of("10.0.0.138"), Ip.Of("8.8.8.8")],
        HostName = "axis-accc8e5f6071",
        DomainName = "example.local",
        RequestedAddress = Ip.Of("10.0.0.101"),
        LeaseTime = 86_400,
        ServerId = Ip.Of("10.0.0.17"),
        ParameterRequestList = [1, 3, 6, 15, 42],
        Message = "no",
        MaxMessageSize = 1500,
        RenewalTime = 43_200,
        RebindingTime = 75_600,
        ClientId = [1, 0xAC, 0xCC, 0x8E, 0x5F, 0x60, 0x71],
    };

    [Fact]
    public void Every_option_round_trips()
    {
        var bytes = new byte[DhcpMessage.MaxLength];
        var length = Full().Write(bytes);
        Assert.True(DhcpMessage.TryRead(bytes.AsSpan(0, length), out var m, out var error), error);
        Assert.NotNull(m);
        var expected = Full();
        Assert.Equal(expected.Op, m.Op);
        Assert.Equal(expected.Hops, m.Hops);
        Assert.Equal(expected.TransactionId, m.TransactionId);
        Assert.Equal(expected.Seconds, m.Seconds);
        Assert.True(m.IsBroadcast);
        Assert.Equal(expected.ClientAddress, m.ClientAddress);
        Assert.Equal(expected.YourAddress, m.YourAddress);
        Assert.Equal(expected.ServerAddress, m.ServerAddress);
        Assert.Equal(expected.RelayAddress, m.RelayAddress);
        Assert.Equal(expected.Mac, m.Mac);
        Assert.Equal("AC:CC:8E:5F:60:71", MacAddress.Format(m.Mac));
        Assert.Equal(DhcpMessageType.Ack, m.MessageType);
        Assert.Equal(expected.SubnetMask, m.SubnetMask);
        Assert.Equal(expected.Routers, m.Routers);
        Assert.Equal(expected.DnsServers, m.DnsServers);
        Assert.Equal(expected.HostName, m.HostName);
        Assert.Equal(expected.DomainName, m.DomainName);
        Assert.Equal(expected.RequestedAddress, m.RequestedAddress);
        Assert.Equal(expected.LeaseTime, m.LeaseTime);
        Assert.Equal(expected.ServerId, m.ServerId);
        Assert.Equal(expected.ParameterRequestList, m.ParameterRequestList);
        Assert.Equal(expected.Message, m.Message);
        Assert.Equal(expected.MaxMessageSize, m.MaxMessageSize);
        Assert.Equal(expected.RenewalTime, m.RenewalTime);
        Assert.Equal(expected.RebindingTime, m.RebindingTime);
        Assert.Equal(expected.ClientId, m.ClientId);
    }

    [Fact]
    public void Replies_are_padded_to_300_bytes_and_end_with_the_end_option()
    {
        var bytes = new byte[DhcpMessage.MaxLength];
        var length = new DhcpMessage { MessageType = DhcpMessageType.Nak }.Write(bytes);
        Assert.Equal(DhcpMessage.MinReplyLength, length);
        Assert.Equal(0x63825363u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(236)));
        Assert.Equal(new byte[] { 53, 1, 6, 255 }, bytes[240..244]);
    }

    [Fact]
    public void Unknown_options_are_ignored_and_repeated_options_concatenated()
    {
        var bytes = Raw(
            [53, 1, 1],
            [42, 4, 1, 2, 3, 4],          // NTP servers: not used, ignored
            [224, 3, 9, 9, 9],            // private option
            [12, 3, (byte)'a', (byte)'x', (byte)'i'],
            [12, 2, (byte)'s', 0],        // RFC 3396: concatenated, trailing NUL removed
            [0, 0],                       // pad
            [255]);
        Assert.True(DhcpMessage.TryRead(bytes, out var m, out _));
        Assert.Equal(DhcpMessageType.Discover, m!.MessageType);
        Assert.Equal("axis", m.HostName);
    }

    [Fact]
    public void Option_overload_reads_options_from_the_file_field()
    {
        var bytes = Raw([53, 1, 3], [52, 1, 1], [255]);
        bytes[108] = 50;
        bytes[109] = 4;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(110), Ip.Of("10.0.0.120"));
        bytes[114] = 255;
        Assert.True(DhcpMessage.TryRead(bytes, out var m, out _));
        Assert.Equal(Ip.Of("10.0.0.120"), m!.RequestedAddress);
    }

    [Fact]
    public void A_missing_end_option_is_tolerated()
    {
        Assert.True(DhcpMessage.TryRead(Raw([53, 1, 8]), out var m, out _));
        Assert.Equal(DhcpMessageType.Inform, m!.MessageType);
    }

    public static TheoryData<string, byte[]> Malformed => new()
    {
        { "too short", new byte[100] },
        { "no DHCP magic cookie", BadCookie() },
        { "option 50 is truncated", Raw([53, 1, 1], [50, 4, 10, 0])[..247] },
        { "option 50 has a bad length", Raw([53, 1, 1], [50, 3, 10, 0, 0], [255]) },
        { "option 54 has a bad length", Raw([53, 1, 1], [54, 5, 10, 0, 0, 1, 1], [255]) },
        { "option 3 has a bad length", Raw([53, 1, 1], [3, 6, 10, 0, 0, 1, 1, 1], [255]) },
        { "bad message type", Raw([53, 1, 9], [255]) },
        { "bad message type", Raw([53, 2, 1, 1], [255]) },
        { "unknown op", Op(7) },
        { "option 61 is too short", Raw([53, 1, 1], [61, 1, 1], [255]) },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void Malformed_input_is_rejected(string reason, byte[] bytes)
    {
        Assert.False(DhcpMessage.TryRead(bytes, out var m, out var error));
        Assert.Null(m);
        Assert.Equal(reason, error);
    }

    [Fact]
    public void Random_garbage_never_throws()
    {
        var random = new Random(42);
        for (var i = 0; i < 5_000; i++)
        {
            var bytes = Raw([53, 1, 1]);
            var length = random.Next(240, bytes.Length);
            random.NextBytes(bytes.AsSpan(240, length - 240));
            DhcpMessage.TryRead(bytes.AsSpan(0, length), out _, out _);
        }
    }

    [Theory]
    [InlineData("B8:A4:4F:63:13:39")]
    [InlineData("b8-a4-4f-63-13-39")]
    [InlineData("B8A44F631339")]
    public void Mac_addresses_parse_in_the_usual_forms(string text)
    {
        Assert.True(MacAddress.TryParse(text, out var mac));
        Assert.Equal("B8:A4:4F:63:13:39", MacAddress.Format(mac));
        Assert.Equal("B8A44F631339", MacAddress.Compact(mac));
        Assert.True(MacAddress.IsUnicast(mac));
    }

    [Theory]
    [InlineData("")]
    [InlineData("B8:A4:4F:63:13")]
    [InlineData("B8:A4-4F:63:13:39")]
    [InlineData("G8:A4:4F:63:13:39")]
    public void Bad_mac_addresses_do_not_parse(string text) => Assert.False(MacAddress.TryParse(text, out _));

    [Fact]
    public void Multicast_and_zero_macs_are_not_unicast()
    {
        Assert.False(MacAddress.IsUnicast(Mac.Of("01:00:5E:00:00:01")));
        Assert.False(MacAddress.IsUnicast(Mac.Of("FF:FF:FF:FF:FF:FF")));
        Assert.False(MacAddress.IsUnicast(0));
    }

    private static byte[] Raw(params byte[][] options)
    {
        var bytes = new byte[600];
        bytes[0] = DhcpMessage.BootRequest;
        bytes[1] = 1;
        bytes[2] = 6;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), 1234);
        bytes[28] = 0xAC;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(236), DhcpMessage.MagicCookie);
        var pos = 240;
        foreach (var option in options)
        {
            option.CopyTo(bytes, pos);
            pos += option.Length;
        }

        return bytes[..Math.Max(pos, 300)];
    }

    private static byte[] BadCookie()
    {
        var bytes = Raw([53, 1, 1], [255]);
        bytes[236] = 0;
        return bytes;
    }

    private static byte[] Op(byte op)
    {
        var bytes = Raw([53, 1, 1], [255]);
        bytes[0] = op;
        return bytes;
    }
}
