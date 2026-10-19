using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.Time.Testing;

using Oadm.SiteNode.Relays;

namespace Oadm.SiteNode.Tests;

/// <summary>The senders of a UDP relay: forgotten after two idle minutes, never more than the capacity.</summary>
public sealed class UdpSendersTests : IDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

    public void Dispose() => _socket.Dispose();

    [Fact]
    public void ASenderIsAnsweredUntilItWasIdleForTwoMinutes()
    {
        var senders = new UdpSenders(_time);
        var camera = IPEndPoint.Parse("192.168.0.20:50123");

        Assert.True(senders.Touch(camera, _socket));
        _time.Advance(UdpSenders.Idle - TimeSpan.FromSeconds(1));
        Assert.Same(_socket, senders.Find(camera));
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(senders.Find(camera));
        Assert.Null(senders.Find(IPEndPoint.Parse("192.168.0.21:123")));
    }

    [Fact]
    public void NewSourcePortsDoNotFillTheTableForever()
    {
        var senders = new UdpSenders(_time, capacity: 3);
        for (var port = 1000; port < 1003; port++)
            Assert.True(senders.Touch(new IPEndPoint(IPAddress.Parse("192.168.0.20"), port), _socket));

        // Full of recent senders: a new one is not relayed, a known one is.
        Assert.False(senders.Touch(IPEndPoint.Parse("192.168.0.20:1003"), _socket));
        Assert.True(senders.Touch(IPEndPoint.Parse("192.168.0.20:1000"), _socket));

        // Days of SNTP queries with a new port each: the old ones go once idle.
        for (var i = 0; i < 1000; i++)
        {
            _time.Advance(UdpSenders.Idle);
            Assert.True(senders.Touch(new IPEndPoint(IPAddress.Parse("192.168.0.20"), 2000 + i), _socket));
            Assert.True(senders.Count <= 3);
        }
    }
}
