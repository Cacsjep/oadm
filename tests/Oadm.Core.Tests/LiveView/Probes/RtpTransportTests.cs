using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

using Oadm.Core.LiveView.Probes;
using Oadm.Core.LiveView.Rtsp;
using Oadm.Core.Sites;

namespace Oadm.Core.Tests.LiveView.Probes;

/// <summary>The Transport header, the UDP port pairs and the RTCP packets of the stream probes.</summary>
public sealed class RtpTransportTests
{
    [Fact]
    public void TransportHeadersAreBuiltLikeRfc2326()
    {
        Assert.Equal("RTP/AVP;unicast;client_port=50000-50001", RtspTransport.Udp(50000));
        Assert.Equal("RTP/AVP/TCP;unicast;interleaved=0-1", RtspTransport.Tcp());
        Assert.Throws<ArgumentOutOfRangeException>(() => RtspTransport.Udp(65535));
    }

    [Fact]
    public void TheCamerasUdpAnswerIsParsed()
    {
        var t = RtspTransport.Parse("RTP/AVP;unicast;client_port=50000-50001;server_port=50010-50011;source=10.0.0.48;ssrc=1A2B3C4D;mode=\"PLAY\"")!;

        Assert.False(t.IsTcp);
        Assert.Equal(50000, t.ClientRtpPort);
        Assert.Equal((50010, 50011), (t.ServerRtpPort, t.ServerRtcpPort));
        Assert.Equal(IPAddress.Parse("10.0.0.48"), t.Source);
        Assert.Equal(0x1A2B3C4Du, t.Ssrc);
    }

    [Theory]
    [InlineData("RTP/AVP/TCP;unicast;interleaved=2-3", true, 2)]
    [InlineData("RTP/AVP/TCP;unicast", true, null)]
    [InlineData("RTP/AVP;unicast;server_port=6970", false, null)]
    public void OtherAnswersAreParsed(string header, bool tcp, int? channel)
    {
        var t = RtspTransport.Parse(header)!;
        Assert.Equal(tcp, t.IsTcp);
        Assert.Equal(channel, t.Interleaved is { } c ? c : null);
        if (!tcp)
        {
            Assert.Equal((6970, 6971), (t.ServerRtpPort, t.ServerRtcpPort)); // one port named: RTCP is the next
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("RAW/RAW/UDP;unicast")]
    public void NoRtpTransportIsNull(string? header) => Assert.Null(RtspTransport.Parse(header));

    [Fact]
    public void TheDefaultRangeHas400Pairs()
    {
        var ports = new RtpUdpPorts();
        Assert.Equal((50000, 50799), (RtpUdpPorts.FirstPort, RtpUdpPorts.LastPort));
        Assert.Equal(400, ports.Pairs);
        Assert.Throws<ArgumentOutOfRangeException>(() => new RtpUdpPorts(50001, 50799));
    }

    [Fact]
    public async Task TwoHundredProbesAtOnceGetDistinctEvenPairs()
    {
        var ports = new RtpUdpPorts();
        using var start = new Barrier(8);
        var leases = new System.Collections.Concurrent.ConcurrentBag<RtpPortLease>();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            start.SignalAndWait();
            for (var i = 0; i < 25; i++)
            {
                leases.Add(ports.TryRent(port => new FakeUdpPort(port))!);
            }
        })));

        var rtp = leases.Select(l => l.RtpPort).ToList();
        Assert.Equal(200, rtp.Distinct().Count());
        Assert.All(leases, l => Assert.Equal((l.RtpPort, l.RtpPort + 1), (l.Rtp.Port, l.Rtcp.Port)));
        Assert.All(rtp, p => Assert.True(p % 2 == 0 && p is >= 50000 and <= 50798, $"port {p}"));
        Assert.Equal(200, ports.InUse);

        foreach (var lease in leases)
        {
            lease.Dispose();
            lease.Dispose(); // twice is harmless
        }

        Assert.Equal(0, ports.InUse);
        Assert.All(leases, l => Assert.True(((FakeUdpPort)l.Rtp).Disposed && ((FakeUdpPort)l.Rtcp).Disposed));
    }

    [Fact]
    public void AFullRangeGivesNoPairUntilOneIsFreed()
    {
        var ports = new RtpUdpPorts(51000, 51003);
        var first = ports.TryRent(port => new FakeUdpPort(port))!;
        var second = ports.TryRent(port => new FakeUdpPort(port))!;

        Assert.Null(ports.TryRent(port => new FakeUdpPort(port)));

        first.Dispose();
        var third = ports.TryRent(port => new FakeUdpPort(port));
        Assert.Equal(first.RtpPort, third!.RtpPort);
        second.Dispose();
        third.Dispose();
    }

    [Fact]
    public void APairAnotherProgramHoldsIsSkipped()
    {
        var ports = new RtpUdpPorts(51000, 51005);
        var opened = new List<FakeUdpPort>();
        var lease = ports.TryRent(port =>
        {
            if (port == 51001)
            {
                throw new SocketException((int)SocketError.AddressAlreadyInUse); // its RTCP port is taken
            }

            var udp = new FakeUdpPort(port);
            opened.Add(udp);
            return udp;
        })!;

        Assert.Equal(51002, lease.RtpPort);
        Assert.True(opened.Single(p => p.Port == 51000).Disposed); // the half-bound pair was closed
        Assert.Equal(1, ports.InUse);
        Assert.Null(ports.TryRent(_ => throw new SocketException((int)SocketError.AddressAlreadyInUse)));
        Assert.Equal(1, ports.InUse);
    }

    [Fact]
    public void AReceiverReportCarriesOneReportBlockAndTheCname()
    {
        var state = new RtcpReceiverState(Ssrc: 0xCAFEBABE, ExtendedHighestSequence: 0x0001_0005, Expected: 100, Lost: 3, Jitter: 54);
        var packet = RtcpPackets.BuildReceiverReport(0x11111111, state, RtcpPackets.FractionLost(100, 25), 0x33445566, 65536);
        var span = packet.AsSpan();

        Assert.Equal(0, packet.Length % 4);
        Assert.Equal((0x81, 201, 7), (span[0], span[1], BinaryPrimitives.ReadUInt16BigEndian(span[2..])));
        Assert.Equal(0x11111111u, BinaryPrimitives.ReadUInt32BigEndian(span[4..]));
        Assert.Equal(0xCAFEBABEu, BinaryPrimitives.ReadUInt32BigEndian(span[8..]));
        Assert.Equal((64u << 24) | 3, BinaryPrimitives.ReadUInt32BigEndian(span[12..])); // 25 % lost = 64/256, 3 in total
        Assert.Equal(0x0001_0005u, BinaryPrimitives.ReadUInt32BigEndian(span[16..]));
        Assert.Equal(54u, BinaryPrimitives.ReadUInt32BigEndian(span[20..]));
        Assert.Equal(0x33445566u, BinaryPrimitives.ReadUInt32BigEndian(span[24..]));
        Assert.Equal(65536u, BinaryPrimitives.ReadUInt32BigEndian(span[28..]));

        var sdes = span[32..];
        Assert.Equal((0x81, 202), (sdes[0], sdes[1]));
        Assert.Equal(sdes.Length, (BinaryPrimitives.ReadUInt16BigEndian(sdes[2..]) + 1) * 4);
        Assert.Equal((1, 4), (sdes[8], sdes[9]));
        Assert.Equal("oadm", Encoding.ASCII.GetString(sdes.Slice(10, 4)));
    }

    [Fact]
    public void TheSenderReportsNtpMiddleIsRead()
    {
        // An RR first, then the SR of 0x01020304 with NTP 0x11223344.55667788: the middle 32 bits are 0x33445566.
        var rr = RtcpPackets.BuildReceiverReport(1, default, 0, 0, 0);
        var sr = new byte[28];
        sr[0] = 0x80;
        sr[1] = RtcpPackets.SenderReport;
        BinaryPrimitives.WriteUInt16BigEndian(sr.AsSpan(2), 6);
        BinaryPrimitives.WriteUInt32BigEndian(sr.AsSpan(4), 0x01020304);
        BinaryPrimitives.WriteUInt32BigEndian(sr.AsSpan(8), 0x11223344);
        BinaryPrimitives.WriteUInt32BigEndian(sr.AsSpan(12), 0x55667788);
        byte[] compound = [.. rr, .. sr];

        Assert.True(RtcpPackets.TryReadSenderReport(compound, 0x01020304, out var middle));
        Assert.Equal(0x33445566u, middle);
        Assert.False(RtcpPackets.TryReadSenderReport(compound, 0x09, out _));
        Assert.False(RtcpPackets.TryReadSenderReport(compound.AsSpan(0, 10), null, out _));
        Assert.Equal(0, RtcpPackets.FractionLost(0, 5));
    }

    internal sealed class FakeUdpPort(int port) : IUdpPort
    {
        public int Port { get; } = port;

        public bool Disposed { get; private set; }

        public ValueTask<UdpReceived> ReceiveAsync(Memory<byte> buffer, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask SendAsync(ReadOnlyMemory<byte> data, IPEndPoint remote, CancellationToken ct) => ValueTask.CompletedTask;

        public void Dispose() => Disposed = true;
    }
}
