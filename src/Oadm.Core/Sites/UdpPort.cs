using System.Net;

namespace Oadm.Core.Sites;

/// <summary>
/// A local UDP port on the server's own network that receives from devices (RTP over UDP of the stream probes,
/// <see cref="DeviceRouter.BindUdp"/>). Sites relay TCP only, so a device at a site never gets one.
/// </summary>
public interface IUdpPort : IDisposable
{
    /// <summary>The local port.</summary>
    int Port { get; }

    /// <summary>The next datagram into <paramref name="buffer"/>; a datagram longer than the buffer is cut.</summary>
    ValueTask<UdpReceived> ReceiveAsync(Memory<byte> buffer, CancellationToken ct);

    /// <summary>Sends one datagram from this port.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> data, IPEndPoint remote, CancellationToken ct);
}

/// <summary>One received datagram: its length in the buffer and its sender.</summary>
public readonly record struct UdpReceived(int Length, IPEndPoint Remote);
