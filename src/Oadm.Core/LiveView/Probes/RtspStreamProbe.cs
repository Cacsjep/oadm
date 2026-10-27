using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.LiveView.Rtp;
using Oadm.Core.LiveView.Rtsp;
using Oadm.Core.Sites;
using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;

namespace Oadm.Core.LiveView.Probes;

/// <summary>Connection settings of a stream probe.</summary>
public sealed record RtspProbeOptions
{
    /// <summary>Device address as stored ("10.0.0.48", "cam.local:443", "fe80::1"); the HTTP port is ignored.</summary>
    public required string Address { get; init; }

    public int Port { get; init; } = RtspClient.DefaultPort;

    public NetworkCredential? Credentials { get; init; }

    /// <summary>Answer a Basic-only RTSP login (the server setting Devices.AllowBasicOverHttp).</summary>
    public bool AllowBasic { get; init; }

    /// <summary>The setting now (<see cref="RtspClient.BasicAllowed"/>); null = <see cref="AllowBasic"/> only.</summary>
    public Func<bool>? BasicAllowed { get; init; }

    public required VideoCodecKind Codec { get; init; }

    public required ImageSize Size { get; init; }

    public required int Fps { get; init; }

    /// <summary>1-based VAPIX camera: view area, sensor or encoder channel.</summary>
    public int Camera { get; init; } = 1;

    /// <summary>RTP over UDP when the route is the server's own network and a port pair is free; else TCP.</summary>
    public bool PreferUdp { get; init; } = true;

    /// <summary>The UDP port pairs; null = TCP only.</summary>
    public RtpUdpPorts? Ports { get; init; }

    /// <summary>Binds one local UDP port (tests: their own); default <see cref="DeviceRouter.BindUdp"/>.</summary>
    public Func<DeviceRoute, AddressFamily, int, IUdpPort?>? Bind { get; init; }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The way to the device (docs/spec/sites.md); the connection opens through <see cref="Router"/>.</summary>
    public DeviceRoute Route { get; init; }

    /// <summary>Opens the connection on <see cref="Route"/>; default the server's own network only.</summary>
    public DeviceRouter? Router { get; init; }

    /// <summary>No packet for this long ends the probe (the plugin's own rules see a shorter gap first).</summary>
    public TimeSpan DataTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a UDP stream may stay without its first packet before the probe takes TCP instead (default
    /// <see cref="RtspStreamProbe.UdpStartTime"/>).
    /// </summary>
    public TimeSpan UdpStartTimeout { get; init; } = RtspStreamProbe.UdpStartTime;

    /// <summary>How often a receiver report goes to the camera over UDP.</summary>
    public TimeSpan ReportInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Clock of the counters, keep-alives and reports (tests: a fake one).</summary>
    public TimeProvider? Time { get; init; }
}

/// <summary>
/// One measuring stream (<see cref="IDeviceStreamProbe"/>, docs/spec/live-view.md "Stream probes"): RTSP DESCRIBE, SETUP,
/// PLAY of the Axis stream with RTP over UDP to a port pair of <see cref="RtpUdpPorts"/> (only datagrams from the camera's
/// address count; a receiver report every 5 s keeps the camera sending) or interleaved over TCP (devices at a site, a
/// camera that answers 461, no free pair). Every packet goes to an <see cref="RtpStreamCounter"/>; nothing is decoded or
/// passed on. The RTSP session stays alive with OPTIONS within its timeout.
/// </summary>
public sealed partial class RtspStreamProbe : IDeviceStreamProbe
{
    /// <summary>The camera refused the asked stream (codec, resolution or camera number).</summary>
    public const string NoVideoStream = "The camera does not offer this video stream";

    /// <summary>The stream went silent.</summary>
    public const string StoppedSending = "The camera stopped sending video";

    /// <summary>The camera closed the RTSP session.</summary>
    public const string Closed = "The camera ended the stream";

    /// <summary>No UDP packet this long after PLAY: the way drops UDP, the probe reopens over TCP.</summary>
    public static readonly TimeSpan UdpStartTime = TimeSpan.FromSeconds(3);

    private const int ReceiveBufferBytes = 1024 * 1024;

    private readonly RtspClient _client;
    private readonly Uri _playUrl;
    private readonly SdpVideoTrack _track;
    private readonly byte _rtpChannel;
    private readonly RtpPortLease? _ports;
    private readonly IPAddress? _camera;
    private readonly IPEndPoint? _cameraRtp;
    private readonly IPEndPoint? _cameraRtcp;
    private readonly RtspProbeOptions _options;
    private readonly RtpStreamCounter _counter;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly uint _ownSsrc = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DateTimeOffset _openedUtc;
    private Task _loops = Task.CompletedTask;
    private long _senderReport;
    private long _senderReportArrival;
    private long _reportedExpected;
    private long _reportedLost;
    private int _disposed;

    private RtspStreamProbe(RtspClient client, Uri playUrl, SdpVideoTrack track, byte rtpChannel, RtpPortLease? ports, RtspTransport? transport,
        RtspProbeOptions options, int fps, ILogger logger)
    {
        _client = client;
        _playUrl = playUrl;
        _track = track;
        _rtpChannel = rtpChannel;
        _ports = ports;
        _options = options;
        _time = options.Time ?? TimeProvider.System;
        _logger = logger;
        _camera = client.RemoteAddress;
        if (ports is not null && _camera is not null)
        {
            _cameraRtp = transport?.ServerRtpPort is { } rtp ? new IPEndPoint(_camera, rtp) : null;
            _cameraRtcp = transport?.ServerRtcpPort is { } rtcp ? new IPEndPoint(_camera, rtcp) : null;
        }

        Fps = fps;
        _counter = new RtpStreamCounter(fps, track.ClockRate, _time);
        _openedUtc = _time.GetUtcNow();
    }

    public StreamProbeTransport Transport => _ports is null ? StreamProbeTransport.Tcp : StreamProbeTransport.Udp;

    /// <summary>The camera agreed to UDP but nothing arrived in <see cref="RtspProbeOptions.UdpStartTimeout"/>: this probe is the TCP retry.</summary>
    public bool UdpBlocked { get; private set; }

    public string Codec => _options.Codec == VideoCodecKind.H265 ? "h265" : "h264";

    public int Width => _options.Size.Width;

    public int Height => _options.Size.Height;

    public int Fps { get; }

    /// <summary>When the first video packet arrived; until then when the stream was set up.</summary>
    public DateTimeOffset StartedUtc => _counter.StartedUtc ?? _openedUtc;

    public Task Completion => _completion.Task;

    /// <summary>The local RTP port over UDP; null over TCP.</summary>
    public int? LocalRtpPort => _ports?.RtpPort;

    public StreamProbeCounters Read() => _counter.Read();

    /// <summary>The DESCRIBE URL: the asked codec, camera, resolution and frame rate, a keyframe every second, no audio.</summary>
    public static Uri BuildUrl(RtspProbeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var host = VapixClient.BuildBaseAddress(Uri.UriSchemeHttp, options.Address).Host;
        var codec = options.Codec == VideoCodecKind.H265 ? "h265" : "h264";
        var port = options.Port == RtspClient.DefaultPort ? string.Empty : ":" + options.Port.ToString(CultureInfo.InvariantCulture);
        var fps = Math.Max(1, options.Fps);
        return new Uri(string.Create(
            CultureInfo.InvariantCulture,
            $"rtsp://{host}{port}/axis-media/media.amp?videocodec={codec}&camera={Math.Max(1, options.Camera)}&resolution={options.Size}&fps={fps}&videokeyframeinterval={fps}&audio=0"));
    }

    /// <summary>
    /// DESCRIBE, SETUP (UDP first when allowed, TCP after a 461), PLAY. Over UDP it waits up to
    /// <see cref="RtspProbeOptions.UdpStartTimeout"/> for the first packet; when none comes the way drops UDP: TEARDOWN and the
    /// same request again over TCP (<see cref="UdpBlocked"/>). Throws <see cref="DeviceStreamException"/>.
    /// </summary>
    public static async Task<RtspStreamProbe> OpenAsync(RtspProbeOptions options, CancellationToken ct, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        logger ??= NullLogger.Instance;
        var probe = await OpenOnceAsync(options, logger, ct).ConfigureAwait(false);
        if (probe.Transport != StreamProbeTransport.Udp)
        {
            return probe;
        }

        try
        {
            // The first packet, or the end of the stream (its fault tells why), within the start time.
            await Task.WhenAny(probe._counter.FirstPacket, probe.Completion)
                .WaitAsync(options.UdpStartTimeout, options.Time ?? TimeProvider.System, ct).ConfigureAwait(false);
            return probe;
        }
        catch (TimeoutException)
        {
            LogUdpBlocked(logger, options.Address, options.UdpStartTimeout.TotalSeconds);
        }
        catch
        {
            await probe.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        await probe.DisposeAsync().ConfigureAwait(false);
        var tcp = await OpenOnceAsync(options with { PreferUdp = false }, logger, ct).ConfigureAwait(false);
        tcp.UdpBlocked = true;
        return tcp;
    }

    /// <summary>One try: DESCRIBE, SETUP (UDP first when allowed, TCP after a 461), PLAY.</summary>
    private static async Task<RtspStreamProbe> OpenOnceAsync(RtspProbeOptions options, ILogger logger, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var url = BuildUrl(options);
        var connectHost = VapixClient.BuildBaseAddress(Uri.UriSchemeHttp, options.Address).DnsSafeHost;
        RtspClient client;
        try
        {
            client = await RtspClient.ConnectAsync(connectHost, options.Port, options.Credentials, options.ConnectTimeout, ct, options.Time, options.Route, options.Router,
                options.BasicAllowed?.Invoke() ?? options.AllowBasic, options.BasicAllowed).ConfigureAwait(false);
        }
        catch (LiveViewException ex)
        {
            throw ToStreamException(ex);
        }

        RtpPortLease? ports = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.ConnectTimeout * 2);
            var token = timeout.Token;

            var describe = await client.SendAsync("DESCRIBE", url, [new("Accept", "application/sdp")], token).ConfigureAwait(false);
            if (describe.StatusCode != 200)
            {
                throw describe.StatusCode is 404 or 415 or 451 or 461
                    ? new DeviceStreamException(DeviceStreamError.NotSupported, NoVideoStream)
                    : new DeviceStreamException(DeviceStreamError.Protocol, $"The camera refused the video stream: RTSP {describe.StatusCode} {describe.Reason}");
            }

            var track = SdpVideoTrack.Parse(describe.Body)
                ?? throw new DeviceStreamException(DeviceStreamError.NotSupported, NoVideoStream);
            if (track.Codec != options.Codec)
            {
                throw new DeviceStreamException(DeviceStreamError.NotSupported, NoVideoStream);
            }

            var baseUrl = describe.Header("Content-Base") is { } contentBase && Uri.TryCreate(contentBase, UriKind.Absolute, out var cb) ? cb : url;
            var control = track.ResolveControl(baseUrl);

            ports = RentPorts(options, client.RemoteAddress);
            RtspResponse setup;
            if (ports is not null)
            {
                setup = await client.SendAsync("SETUP", control, [new("Transport", RtspTransport.Udp(ports.RtpPort))], token).ConfigureAwait(false);
                if (setup.StatusCode == RtspTransport.UnsupportedTransport)
                {
                    LogUdpRefused(logger, connectHost);
                    ports.Dispose();
                    ports = null;
                    setup = await SetupTcpAsync(client, control, token).ConfigureAwait(false);
                }
            }
            else
            {
                setup = await SetupTcpAsync(client, control, token).ConfigureAwait(false);
            }

            if (setup.StatusCode != 200 || client.Session is null)
            {
                throw new DeviceStreamException(DeviceStreamError.Protocol, $"The camera refused the video stream: RTSP SETUP {setup.StatusCode} {setup.Reason}");
            }

            var transport = RtspTransport.Parse(setup.Header("Transport"));
            if (ports is not null && transport is { IsTcp: true })
            {
                ports.Dispose(); // the camera chose TCP itself
                ports = null;
            }

            var channel = transport is { IsTcp: true, Interleaved: { } c } ? c : (byte)0;
            var play = await client.SendAsync("PLAY", baseUrl, [new("Range", "npt=0.000-")], token).ConfigureAwait(false);
            if (play.StatusCode != 200)
            {
                throw new DeviceStreamException(DeviceStreamError.Protocol, $"The camera refused the video stream: RTSP PLAY {play.StatusCode} {play.Reason}");
            }

            // The camera's announced rate when it is lower than asked (a 25 fps sensor asked for 30): frames are expected at that.
            var fps = track.Framerate is { } announced && announced >= 1 && announced < options.Fps ? (int)Math.Round(announced) : options.Fps;
            var probe = new RtspStreamProbe(client, baseUrl, track, channel, ports, transport, options, fps, logger);
            probe.Start();
            return probe;
        }
        catch (Exception ex)
        {
            ports?.Dispose();
            await client.DisposeAsync().ConfigureAwait(false);
            throw ex switch
            {
                DeviceStreamException => ex,
                LiveViewException live => ToStreamException(live),
                OperationCanceledException when !ct.IsCancellationRequested =>
                    new DeviceStreamException(DeviceStreamError.Unreachable, "Unreachable - the camera did not answer the RTSP request in time", ex),
                IOException or SocketException => new DeviceStreamException(DeviceStreamError.Unreachable, "Unreachable - " + ex.Message, ex),
                _ => ex,
            };
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await _client.SendWithoutResponseAsync("TEARDOWN", _playUrl, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            LogTeardownFailed(_logger, ex.Message);
        }

        await _client.DisposeAsync().ConfigureAwait(false);
        _ports?.Dispose();
        try
        {
            await _loops.ConfigureAwait(false);
        }
#pragma warning disable CA1031 // The loops end on the closed connection and ports; nothing of theirs matters after the end.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        _stop.Dispose();
        _completion.TrySetResult();
    }

    /// <summary>The user text of an RTSP failure.</summary>
    internal static DeviceStreamException ToStreamException(LiveViewException ex) => ex.Error switch
    {
        LiveViewError.Unauthorized => new DeviceStreamException(
            DeviceStreamError.Unauthorized,
            ex.Message == DeviceMessages.BasicOnlyOverHttp ? DeviceMessages.BasicOnlyOverHttp : DeviceMessages.Unauthorized,
            ex),
        LiveViewError.Unreachable => new DeviceStreamException(
            DeviceStreamError.Unreachable,
            DeviceMessages.Unreachable(ex.InnerException is SocketException socket ? socket.Message : ex.Message),
            ex),
        LiveViewError.NotSupported => new DeviceStreamException(DeviceStreamError.NotSupported, NoVideoStream, ex),
        _ => new DeviceStreamException(DeviceStreamError.Protocol, ex.Message, ex),
    };

    private static Task<RtspResponse> SetupTcpAsync(RtspClient client, Uri control, CancellationToken ct) =>
        client.SendAsync("SETUP", control, [new("Transport", RtspTransport.Tcp()), new("Blocksize", "64000")], ct);

    /// <summary>A port pair for RTP over UDP, or null: TCP (not asked, a site, no camera address, no free pair).</summary>
    private static RtpPortLease? RentPorts(RtspProbeOptions options, IPAddress? camera)
    {
        if (!options.PreferUdp || options.Ports is null || !options.Route.IsLocal || camera is null)
        {
            return null;
        }

        var bind = options.Bind ?? ((route, family, port) => DeviceRouter.BindUdp(route, family, port, ReceiveBufferBytes));
        return options.Ports.TryRent(port => bind(options.Route, camera.AddressFamily, port)
            ?? throw new SocketException((int)SocketError.AddressFamilyNotSupported));
    }

    private void Start()
    {
        var token = _stop.Token;
        _loops = _ports is null
            ? Task.WhenAll(Task.Run(() => ReadRtspAsync(countInterleaved: true, token)), Task.Run(() => WatchAsync(token)))
            : Task.WhenAll(
                Task.Run(() => ReadRtspAsync(countInterleaved: false, token)),
                Task.Run(() => ReceiveRtpAsync(_ports.Rtp, token)),
                Task.Run(() => ReceiveRtcpAsync(_ports.Rtcp, token)),
                Task.Run(() => WatchAsync(token)));
    }

    private void Fail(DeviceStreamException error)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (_completion.TrySetException(error))
        {
            LogEnded(_logger, error.Message);
            try
            {
                _stop.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // disposed meanwhile
            }
        }
    }

    /// <summary>The RTSP connection: interleaved RTP over TCP, keep-alive answers over UDP; its end ends the probe.</summary>
    private async Task ReadRtspAsync(bool countInterleaved, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var packet = await _client.ReadInterleavedAsync(ct).ConfigureAwait(false);
                if (packet is null)
                {
                    Fail(new DeviceStreamException(DeviceStreamError.Unreachable, Closed));
                    return;
                }

                var p = packet.Value;
                if (countInterleaved && p.Channel == _rtpChannel && RtpPacket.TryParse(p.Data, out var rtp) && rtp.PayloadType == _track.PayloadType)
                {
                    _counter.Add(rtp);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // stopped
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or LiveViewException)
        {
            Fail(new DeviceStreamException(DeviceStreamError.Unreachable, DeviceMessages.Unreachable("the connection was lost"), ex));
        }
    }

    private async Task ReceiveRtpAsync(IUdpPort port, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (!ct.IsCancellationRequested)
        {
            UdpReceived received;
            try
            {
                received = await port.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
            {
                continue; // an ICMP answer to a report, a datagram larger than any RTP packet
            }
            catch (SocketException ex)
            {
                Fail(new DeviceStreamException(DeviceStreamError.Unreachable, DeviceMessages.Unreachable(ex.Message), ex));
                return;
            }

            if (FromCamera(received.Remote)
                && RtpPacket.TryParse(buffer.AsMemory(0, received.Length), out var rtp)
                && rtp.PayloadType == _track.PayloadType)
            {
                _counter.Add(rtp);
            }
        }
    }

    /// <summary>The camera's sender reports: their NTP time goes back in the next receiver report (LSR, DLSR).</summary>
    private async Task ReceiveRtcpAsync(IUdpPort port, CancellationToken ct)
    {
        var buffer = new byte[2048];
        while (!ct.IsCancellationRequested)
        {
            UdpReceived received;
            try
            {
                received = await port.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                continue; // RTCP is best effort; the RTP port decides
            }

            if (FromCamera(received.Remote) && RtcpPackets.TryReadSenderReport(buffer.AsSpan(0, received.Length), null, out var ntp))
            {
                Interlocked.Exchange(ref _senderReport, ntp);
                Interlocked.Exchange(ref _senderReportArrival, _time.GetTimestamp());
            }
        }
    }

    /// <summary>Keep-alives within the session timeout, receiver reports over UDP, and the end of a silent stream.</summary>
    private async Task WatchAsync(CancellationToken ct)
    {
        var keepAlive = TimeSpan.FromSeconds(Math.Max(5, _client.SessionTimeout.TotalSeconds / 2));
        var tick = TimeSpan.FromSeconds(1);
        var sinceKeepAlive = TimeSpan.Zero;
        var sinceReport = TimeSpan.Zero;
        var sinceOpen = TimeSpan.Zero;
        try
        {
            if (_ports is not null)
            {
                await OpenPathAsync(ct).ConfigureAwait(false);
            }

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(tick, _time, ct).ConfigureAwait(false);
                sinceKeepAlive += tick;
                sinceReport += tick;
                sinceOpen += tick;
                if (sinceKeepAlive >= keepAlive)
                {
                    sinceKeepAlive = TimeSpan.Zero;
                    await _client.SendWithoutResponseAsync("OPTIONS", _playUrl, ct).ConfigureAwait(false);
                }

                if (_ports is not null && sinceReport >= _options.ReportInterval)
                {
                    sinceReport = TimeSpan.Zero;
                    await SendReportAsync(ct).ConfigureAwait(false);
                }

                var silent = _counter.SinceLastPacket ?? sinceOpen;
                if (silent >= _options.DataTimeout)
                {
                    Fail(new DeviceStreamException(DeviceStreamError.Unreachable, StoppedSending));
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // stopped
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            Fail(new DeviceStreamException(DeviceStreamError.Unreachable, DeviceMessages.Unreachable("the connection was lost"), ex));
        }
    }

    /// <summary>
    /// One packet from each local port to the camera's ports right after PLAY (like other RTSP clients): a stateful
    /// firewall or NAT on the way then lets the camera's datagrams in.
    /// </summary>
    private async Task OpenPathAsync(CancellationToken ct)
    {
        try
        {
            if (_cameraRtp is not null)
            {
                var punch = new byte[12];
                punch[0] = 0x80;
                punch[1] = (byte)(_track.PayloadType & 0x7F);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(punch.AsSpan(8), _ownSsrc);
                await _ports!.Rtp.SendAsync(punch, _cameraRtp, ct).ConfigureAwait(false);
            }

            await SendReportAsync(ct).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            LogReportFailed(_logger, ex.Message);
        }
    }

    /// <summary>A receiver report (RFC 3550 6.4.2) to the camera's RTCP port; before the first packet an empty one.</summary>
    private async Task SendReportAsync(CancellationToken ct)
    {
        if (_cameraRtcp is null || _ports is null)
        {
            return;
        }

        var state = _counter.ReadForReport() ?? default;
        var fraction = RtcpPackets.FractionLost(state.Expected - _reportedExpected, state.Lost - _reportedLost);
        _reportedExpected = state.Expected;
        _reportedLost = state.Lost;
        var lsr = (uint)Interlocked.Read(ref _senderReport);
        var arrival = Interlocked.Read(ref _senderReportArrival);
        var dlsr = lsr == 0 ? 0u : (uint)Math.Min(uint.MaxValue, _time.GetElapsedTime(arrival).TotalSeconds * 65536);
        var report = RtcpPackets.BuildReceiverReport(_ownSsrc, state, fraction, lsr, dlsr);
        try
        {
            await _ports.Rtcp.SendAsync(report, _cameraRtcp, ct).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            LogReportFailed(_logger, ex.Message); // the next one may get through
        }
    }

    private bool FromCamera(IPEndPoint remote)
    {
        var address = remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address;
        return _camera is not null && address.Equals(_camera);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "RTSP TEARDOWN of a stream probe failed: {Reason}")]
    private static partial void LogTeardownFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Host} refused RTP over UDP: the stream probe uses TCP")]
    private static partial void LogUdpRefused(ILogger logger, string host);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Host} sent no video over UDP in {Seconds} s: the stream probe uses TCP")]
    private static partial void LogUdpBlocked(ILogger logger, string host, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stream probe ended: {Reason}")]
    private static partial void LogEnded(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "RTCP receiver report not sent: {Reason}")]
    private static partial void LogReportFailed(ILogger logger, string reason);
}
