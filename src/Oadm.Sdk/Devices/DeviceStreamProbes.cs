namespace Oadm.Sdk.Devices;

/// <summary>
/// Measuring video streams of managed devices on the server (the Load test): the host opens RTSP with the stored
/// credentials (which never reach the plugin), receives the video and counts packets, frames and bytes; nothing is decoded
/// and nothing is passed on. Offered to core plugins through <c>ICorePluginContext.StreamProbes</c>. SDK 0.6.0.
/// </summary>
public interface IDeviceStreamProbes
{
    /// <summary>
    /// The video sources of a device that are not disabled, each with its largest resolution, maximum frame rate and
    /// codecs. Throws <see cref="DeviceStreamException"/> when the device refuses or does not answer,
    /// <see cref="KeyNotFoundException"/> for an unknown device.
    /// </summary>
    Task<IReadOnlyList<StreamProbeSource>> ListSourcesAsync(Guid deviceId, CancellationToken ct);

    /// <summary>
    /// Connects, authenticates and starts the stream of <paramref name="request"/>. RTP over UDP when the request prefers
    /// it and the server reaches the device directly, else interleaved over TCP (devices at sites, a camera that refuses
    /// UDP); <see cref="IDeviceStreamProbe.Transport"/> says which. Throws <see cref="DeviceStreamException"/> when the
    /// device refuses or does not answer, <see cref="KeyNotFoundException"/> for an unknown device.
    /// </summary>
    Task<IDeviceStreamProbe> OpenAsync(Guid deviceId, StreamProbeRequest request, CancellationToken ct);
}

/// <summary>One video source of a device (<c>Image.I&lt;n&gt;</c>, camera <c>n+1</c>).</summary>
/// <param name="Camera">The RTSP camera number, 1 for the first source.</param>
/// <param name="Name">The source's name (<c>Image.I&lt;n&gt;.Name</c>), e.g. "View Area 1".</param>
/// <param name="Width">Width of the largest resolution.</param>
/// <param name="Height">Height of the largest resolution.</param>
/// <param name="MaxFps">The highest frame rate the source offers.</param>
/// <param name="Codecs">The codecs the source offers, best first ("h265", "h264").</param>
public sealed record StreamProbeSource(int Camera, string Name, int Width, int Height, int MaxFps, IReadOnlyList<string> Codecs);

/// <summary>What to stream.</summary>
/// <param name="Camera">The RTSP camera number (<see cref="StreamProbeSource.Camera"/>).</param>
/// <param name="Width">Requested width; 0 = the largest of the source.</param>
/// <param name="Height">Requested height; 0 = the largest of the source.</param>
/// <param name="Fps">Requested frame rate; 0 = the maximum of the source.</param>
/// <param name="Codec">"h265" or "h264"; null = the best the source offers.</param>
/// <param name="PreferUdp">RTP over UDP where possible (real packet loss); false = always TCP.</param>
public sealed record StreamProbeRequest(int Camera, int Width = 0, int Height = 0, int Fps = 0, string? Codec = null, bool PreferUdp = true);

/// <summary>How the RTP packets of a probe travel.</summary>
public enum StreamProbeTransport
{
    /// <summary>RTP over UDP: a sequence gap is a packet lost on the network.</summary>
    Udp = 0,

    /// <summary>RTP interleaved on the RTSP TCP connection: the network resends, loss shows only as late frames.</summary>
    Tcp = 1,
}

/// <summary>An open measuring stream; dispose to end it (RTSP TEARDOWN).</summary>
public interface IDeviceStreamProbe : IAsyncDisposable
{
    /// <summary>UDP or TCP, as negotiated.</summary>
    StreamProbeTransport Transport { get; }

    /// <summary>
    /// True when the probe asked for UDP, the camera agreed, but no video arrived over UDP in the first seconds (a firewall
    /// or NAT on the way drops it), so it streams over TCP instead. False otherwise, also when the camera refused UDP or
    /// the device is at a site. SDK 0.6.0.
    /// </summary>
    bool UdpBlocked => false;

    /// <summary>The codec the camera streams ("h265" or "h264").</summary>
    string Codec { get; }

    /// <summary>The streamed width.</summary>
    int Width { get; }

    /// <summary>The streamed height.</summary>
    int Height { get; }

    /// <summary>The asked frame rate (frames per second the camera should send).</summary>
    int Fps { get; }

    /// <summary>When the first video packet arrived.</summary>
    DateTimeOffset StartedUtc { get; }

    /// <summary>The counters since the start; cheap, thread-safe, call it once per second and take the difference.</summary>
    StreamProbeCounters Read();

    /// <summary>
    /// Completes when the stream ends: normally after <see cref="IAsyncDisposable.DisposeAsync"/>, faulted with
    /// <see cref="DeviceStreamException"/> when the device closes or the connection breaks.
    /// </summary>
    Task Completion { get; }
}

/// <summary>Counters of a probe since its start (all cumulative except <see cref="JitterMs"/>).</summary>
/// <param name="Packets">RTP packets received.</param>
/// <param name="LostPackets">RTP packets missing in the sequence (UDP; reorders inside 50 packets are not lost).</param>
/// <param name="Frames">Complete frames received (access units with their marker).</param>
/// <param name="ExpectedFrames">Frames the asked frame rate gives for the time since the start (by RTP time).</param>
/// <param name="LateFrames">Frames that arrived more than 2 frame times after their RTP time says.</param>
/// <param name="Bytes">RTP payload bytes received (without RTP, UDP, IP and RTSP overhead).</param>
/// <param name="JitterMs">The current interarrival jitter (RFC 3550 6.4.1) in milliseconds.</param>
/// <param name="LastPacketUtc">When the last packet arrived; null before the first.</param>
public readonly record struct StreamProbeCounters(
    long Packets,
    long LostPackets,
    long Frames,
    long ExpectedFrames,
    long LateFrames,
    long Bytes,
    double JitterMs,
    DateTimeOffset? LastPacketUtc);
