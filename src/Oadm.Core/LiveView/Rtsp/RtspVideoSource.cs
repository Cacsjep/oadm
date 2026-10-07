using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.LiveView.Rtp;
using Oadm.Core.Vapix;

namespace Oadm.Core.LiveView.Rtsp;

/// <summary>An open camera stream producing access units until disposed.</summary>
public interface ILiveVideoSource : IAsyncDisposable
{
    /// <summary>Access units until the stream ends (then the sequence completes) or fails (then it throws).</summary>
    IAsyncEnumerable<LiveVideoFrame> ReadFramesAsync(CancellationToken ct);
}

/// <summary>Connection settings of an RTSP live stream.</summary>
public sealed record RtspSourceOptions
{
    /// <summary>Device address as stored ("10.0.0.48", "cam.local:443", "fe80::1"); the HTTP port is ignored.</summary>
    public required string Address { get; init; }

    public int Port { get; init; } = RtspClient.DefaultPort;

    public NetworkCredential? Credentials { get; init; }

    public required VideoCodecKind Codec { get; init; }

    public required ImageSize Size { get; init; }

    public int Fps { get; init; } = LiveViewNegotiation.DefaultFps;

    /// <summary>1-based VAPIX camera: view area, sensor or encoder channel.</summary>
    public int Camera { get; init; } = 1;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Longest gap between packets before the stream counts as dead.</summary>
    public TimeSpan DataTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Axis RTSP stream (<c>rtsp://host/axis-media/media.amp?videocodec=h264|h265&amp;camera=C&amp;resolution=WxH&amp;fps=N&amp;videokeyframeinterval=N&amp;audio=0</c>)
/// over RTP/AVP/TCP interleaved on port 554 with Digest authentication. A keyframe is requested
/// every second (videokeyframeinterval = fps) so new viewers see a picture quickly.
/// </summary>
public sealed partial class RtspVideoSource : ILiveVideoSource
{
    private readonly RtspClient _client;
    private readonly Uri _playUrl;
    private readonly SdpVideoTrack _track;
    private readonly byte _rtpChannel;
    private readonly RtspSourceOptions _options;
    private readonly NalDepacketizer _depacketizer;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private int _disposed;

    private RtspVideoSource(RtspClient client, Uri playUrl, SdpVideoTrack track, byte rtpChannel, RtspSourceOptions options, TimeProvider time, ILogger logger)
    {
        _client = client;
        _playUrl = playUrl;
        _track = track;
        _rtpChannel = rtpChannel;
        _options = options;
        _time = time;
        _logger = logger;
        _depacketizer = options.Codec == VideoCodecKind.H265 ? new H265Depacketizer() : new H264Depacketizer();
        foreach (var nal in track.ParameterSets())
        {
            _depacketizer.AddParameterSet(nal);
        }
    }

    /// <summary>The DESCRIBE URL for the options.</summary>
    public static Uri BuildUrl(RtspSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var host = VapixClient.BuildBaseAddress(Uri.UriSchemeHttp, options.Address).Host;
        var codec = options.Codec == VideoCodecKind.H265 ? "h265" : "h264";
        var port = options.Port == RtspClient.DefaultPort ? string.Empty : ":" + options.Port.ToString(CultureInfo.InvariantCulture);
        var fps = LiveViewNegotiation.ClampFps(options.Fps);
        return new Uri(string.Create(
            CultureInfo.InvariantCulture,
            $"rtsp://{host}{port}/axis-media/media.amp?videocodec={codec}&camera={Math.Max(1, options.Camera)}&resolution={options.Size}&fps={fps}&videokeyframeinterval={fps}&audio=0"));
    }

    /// <summary>DESCRIBE, SETUP, PLAY. Throws <see cref="LiveViewException"/> when the camera refuses.</summary>
    public static async Task<RtspVideoSource> OpenAsync(RtspSourceOptions options, CancellationToken ct, ILogger? logger = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        logger ??= NullLogger.Instance;
        var url = BuildUrl(options);
        var connectHost = VapixClient.BuildBaseAddress(Uri.UriSchemeHttp, options.Address).DnsSafeHost;
        var client = await RtspClient.ConnectAsync(connectHost, options.Port, options.Credentials, options.ConnectTimeout, ct).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.ConnectTimeout * 2);
            var token = timeout.Token;

            var describe = await client.SendAsync("DESCRIBE", url, [new("Accept", "application/sdp")], token).ConfigureAwait(false);
            if (describe.StatusCode != 200)
            {
                throw new LiveViewException(describe.StatusCode is 404 or 415 or 451 or 461 ? LiveViewError.NotSupported : LiveViewError.Protocol,
                    $"RTSP DESCRIBE failed: {describe.StatusCode} {describe.Reason}");
            }

            var track = SdpVideoTrack.Parse(describe.Body)
                ?? throw new LiveViewException(LiveViewError.NotSupported, "The camera stream has no video track.");
            if (track.Codec != options.Codec)
            {
                throw new LiveViewException(LiveViewError.NotSupported, $"The camera answered with {track.EncodingName} instead of {options.Codec}.");
            }

            var baseUrl = describe.Header("Content-Base") is { } contentBase && Uri.TryCreate(contentBase, UriKind.Absolute, out var cb) ? cb : url;
            var control = track.ResolveControl(baseUrl);
            var setup = await client.SendAsync(
                "SETUP",
                control,
                [new("Transport", "RTP/AVP/TCP;unicast;interleaved=0-1"), new("Blocksize", "64000")],
                token).ConfigureAwait(false);
            if (setup.StatusCode != 200 || client.Session is null)
            {
                throw new LiveViewException(LiveViewError.Protocol, $"RTSP SETUP failed: {setup.StatusCode} {setup.Reason}");
            }

            var channel = ParseInterleavedChannel(setup.Header("Transport")) ?? 0;
            var play = await client.SendAsync("PLAY", baseUrl, [new("Range", "npt=0.000-")], token).ConfigureAwait(false);
            if (play.StatusCode != 200)
            {
                throw new LiveViewException(LiveViewError.Protocol, $"RTSP PLAY failed: {play.StatusCode} {play.Reason}");
            }

            return new RtspVideoSource(client, baseUrl, track, channel, options, time ?? TimeProvider.System, logger);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw new LiveViewException(LiveViewError.Unreachable, "The camera did not answer the RTSP request in time.", ex);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The SDP video track that is being played.</summary>
    public SdpVideoTrack Track => _track;

    /// <summary>Access units dropped by the depacketizer (loss, damage, waiting for a keyframe).</summary>
    public int DroppedAccessUnits => _depacketizer.DroppedAccessUnits;

    public async IAsyncEnumerable<LiveVideoFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var keepAliveEvery = TimeSpan.FromSeconds(Math.Max(5, _client.SessionTimeout.TotalSeconds / 2));
        var lastKeepAlive = Stopwatch.GetTimestamp();
        while (true)
        {
            InterleavedPacket? packet;
            using (var dataTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                dataTimeout.CancelAfter(_options.DataTimeout);
                try
                {
                    packet = await _client.ReadInterleavedAsync(dataTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    throw new LiveViewException(LiveViewError.Unreachable, "The camera stopped sending video.", ex);
                }
            }

            if (packet is null)
            {
                yield break;
            }

            if (Stopwatch.GetElapsedTime(lastKeepAlive) > keepAliveEvery)
            {
                lastKeepAlive = Stopwatch.GetTimestamp();
                await _client.SendWithoutResponseAsync("OPTIONS", _playUrl, ct).ConfigureAwait(false);
            }

            var p = packet.Value;
            if (p.Channel != _rtpChannel || !RtpPacket.TryParse(p.Data, out var rtp) || rtp.PayloadType != _track.PayloadType)
            {
                continue;
            }

            foreach (var unit in _depacketizer.Push(rtp))
            {
                yield return new LiveVideoFrame(
                    _options.Codec,
                    unit.Data,
                    unit.IsKeyframe,
                    _options.Size.Width,
                    _options.Size.Height,
                    _time.GetUtcNow(),
                    unit.CodecConfig,
                    unit.RtpTimestamp);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await _client.SendWithoutResponseAsync("TEARDOWN", _playUrl, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or System.Net.Sockets.SocketException)
        {
            LogTeardownFailed(_logger, ex.Message);
        }

        await _client.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Reads "interleaved=0-1" from a Transport header.</summary>
    internal static byte? ParseInterleavedChannel(string? transport)
    {
        if (transport is null)
        {
            return null;
        }

        foreach (var part in transport.Split(';', StringSplitOptions.TrimEntries))
        {
            if (part.StartsWith("interleaved=", StringComparison.OrdinalIgnoreCase))
            {
                var first = part["interleaved=".Length..].Split('-')[0];
                if (byte.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var channel))
                {
                    return channel;
                }
            }
        }

        return null;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "RTSP TEARDOWN failed: {Reason}")]
    private static partial void LogTeardownFailed(ILogger logger, string reason);
}
