using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.LiveView.Rtp;
using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;

namespace Oadm.Core.LiveView.Rtsp;

/// <summary>Connection settings of an RTSP event (metadata) stream.</summary>
public sealed record RtspMetadataOptions
{
    /// <summary>Device address as stored ("10.0.0.48", "cam.local:443", "fe80::1"); the HTTP port is ignored.</summary>
    public required string Address { get; init; }

    public int Port { get; init; } = RtspClient.DefaultPort;

    public NetworkCredential? Credentials { get; init; }

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Keep-alive interval; null = half the session timeout, at most 10 s.</summary>
    public TimeSpan? KeepAliveInterval { get; init; }

    /// <summary>Silence (no packet, no keep-alive answer) after which the connection counts as broken; null = 2 x keep-alive + 5 s.</summary>
    public TimeSpan? DeadTimeout { get; init; }

    public int MaxDocumentBytes { get; init; } = MetadataDepacketizer.DefaultMaxDocumentBytes;
}

/// <summary>
/// Axis event stream <c>rtsp://host/axis-media/media.amp?video=0&amp;audio=0&amp;event=on</c>: RTSP/1.0 over TCP port 554,
/// RTP interleaved, Digest authentication (Basic is never sent on plain RTSP), DESCRIBE / SETUP of the
/// <c>vnd.onvif.metadata</c> media / PLAY, OPTIONS keep-alives on a timer (event streams can be silent for minutes),
/// TEARDOWN on dispose. Yields one XML document per RTP marker (<see cref="MetadataDepacketizer"/>).
/// </summary>
public sealed partial class RtspMetadataSource : IDeviceEventSource
{
    /// <summary>Message of a device without an event stream.</summary>
    public const string NoEventStream = "The device has no event stream";

    private readonly RtspClient _client;
    private readonly Uri _playUrl;
    private readonly SdpMetadataTrack _track;
    private readonly byte _rtpChannel;
    private readonly MetadataDepacketizer _depacketizer;
    private readonly TimeSpan _keepAlive;
    private readonly TimeSpan _dead;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private int _disposed;

    private RtspMetadataSource(RtspClient client, Uri playUrl, SdpMetadataTrack track, byte rtpChannel, RtspMetadataOptions options, TimeProvider time, ILogger logger)
    {
        _client = client;
        _playUrl = playUrl;
        _track = track;
        _rtpChannel = rtpChannel;
        _time = time;
        _logger = logger;
        _depacketizer = new MetadataDepacketizer(options.MaxDocumentBytes);
        var half = TimeSpan.FromSeconds(Math.Max(1, client.SessionTimeout.TotalSeconds / 2));
        _keepAlive = options.KeepAliveInterval ?? (half < TimeSpan.FromSeconds(10) ? half : TimeSpan.FromSeconds(10));
        _dead = options.DeadTimeout ?? (_keepAlive * 2) + TimeSpan.FromSeconds(5);
    }

    /// <summary>The SDP metadata media that is being played.</summary>
    public SdpMetadataTrack Track => _track;

    public int LostDocuments => _depacketizer.LostDocuments;

    /// <summary>The DESCRIBE URL: events only, no video, no audio.</summary>
    public static Uri BuildUrl(RtspMetadataOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var host = VapixClient.BuildBaseAddress(Uri.UriSchemeHttp, options.Address).Host;
        var port = options.Port == RtspClient.DefaultPort ? string.Empty : ":" + options.Port.ToString(CultureInfo.InvariantCulture);
        return new Uri($"rtsp://{host}{port}/axis-media/media.amp?video=0&audio=0&event=on");
    }

    /// <summary>DESCRIBE, SETUP, PLAY. Throws <see cref="DeviceStreamException"/> with a message for the user.</summary>
    public static async Task<RtspMetadataSource> OpenAsync(RtspMetadataOptions options, CancellationToken ct, ILogger? logger = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        logger ??= NullLogger.Instance;
        var url = BuildUrl(options);
        var connectHost = VapixClient.BuildBaseAddress(Uri.UriSchemeHttp, options.Address).DnsSafeHost;
        RtspClient client;
        try
        {
            client = await RtspClient.ConnectAsync(connectHost, options.Port, options.Credentials, options.ConnectTimeout, ct).ConfigureAwait(false);
        }
        catch (LiveViewException ex)
        {
            throw ToStreamException(ex);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.ConnectTimeout * 2);
            var token = timeout.Token;

            var describe = await client.SendAsync("DESCRIBE", url, [new("Accept", "application/sdp")], token).ConfigureAwait(false);
            if (describe.StatusCode != 200)
            {
                throw describe.StatusCode is 404 or 415 or 451 or 461
                    ? new DeviceStreamException(DeviceStreamError.NotSupported, NoEventStream)
                    : new DeviceStreamException(DeviceStreamError.Protocol, $"The device refused the event stream: RTSP {describe.StatusCode} {describe.Reason}");
            }

            var track = SdpMetadataTrack.Parse(describe.Body)
                ?? throw new DeviceStreamException(DeviceStreamError.NotSupported, NoEventStream);
            var baseUrl = describe.Header("Content-Base") is { } contentBase && Uri.TryCreate(contentBase, UriKind.Absolute, out var cb) ? cb : url;
            var setup = await client.SendAsync(
                "SETUP",
                track.ResolveControl(baseUrl),
                [new("Transport", "RTP/AVP/TCP;unicast;interleaved=0-1")],
                token).ConfigureAwait(false);
            if (setup.StatusCode != 200 || client.Session is null)
            {
                throw new DeviceStreamException(DeviceStreamError.Protocol, $"The device refused the event stream: RTSP SETUP {setup.StatusCode} {setup.Reason}");
            }

            var channel = RtspVideoSource.ParseInterleavedChannel(setup.Header("Transport")) ?? 0;
            var play = await client.SendAsync("PLAY", baseUrl, [new("Range", "npt=0.000-")], token).ConfigureAwait(false);
            if (play.StatusCode != 200)
            {
                throw new DeviceStreamException(DeviceStreamError.Protocol, $"The device refused the event stream: RTSP PLAY {play.StatusCode} {play.Reason}");
            }

            return new RtspMetadataSource(client, baseUrl, track, channel, options, time ?? TimeProvider.System, logger);
        }
        catch (Exception ex)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw ex switch
            {
                DeviceStreamException => ex,
                LiveViewException live => ToStreamException(live),
                OperationCanceledException when !ct.IsCancellationRequested =>
                    new DeviceStreamException(DeviceStreamError.Unreachable, "Unreachable - the device did not answer the RTSP request in time", ex),
                IOException or SocketException => new DeviceStreamException(DeviceStreamError.Unreachable, "Unreachable - " + ex.Message, ex),
                _ => ex,
            };
        }
    }

    public async IAsyncEnumerable<DeviceMetadataDocument> ReadAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watchdog = WatchAsync(linked);
        try
        {
            while (true)
            {
                InterleavedPacket? packet;
                try
                {
                    packet = await _client.ReadInterleavedAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    throw new DeviceStreamException(DeviceStreamError.Unreachable, "Unreachable - the device stopped answering", ex);
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                {
                    throw new DeviceStreamException(DeviceStreamError.Unreachable, "Unreachable - the connection was lost", ex);
                }
                catch (LiveViewException ex)
                {
                    throw ToStreamException(ex);
                }

                if (packet is null)
                {
                    yield break; // the device ended the session
                }

                var p = packet.Value;
                if (p.Channel != _rtpChannel || !RtpPacket.TryParse(p.Data, out var rtp) || rtp.PayloadType != _track.PayloadType)
                {
                    continue;
                }

                if (_depacketizer.Push(rtp) is { } document)
                {
                    yield return new DeviceMetadataDocument(document.Xml, _time.GetUtcNow());
                }
            }
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
            await watchdog.ConfigureAwait(false);
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
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            LogTeardownFailed(_logger, ex.Message);
        }

        await _client.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>The user text of a live view failure (start errors of the Metadata Monitor).</summary>
    internal static DeviceStreamException ToStreamException(LiveViewException ex) => ex.Error switch
    {
        LiveViewError.Unauthorized => new DeviceStreamException(DeviceStreamError.Unauthorized, DeviceMessages.Unauthorized, ex),
        LiveViewError.Unreachable => new DeviceStreamException(
            DeviceStreamError.Unreachable,
            DeviceMessages.Unreachable(ex.InnerException is SocketException socket ? socket.Message : ex.Message),
            ex),
        LiveViewError.NotSupported => new DeviceStreamException(DeviceStreamError.NotSupported, NoEventStream, ex),
        _ => new DeviceStreamException(DeviceStreamError.Protocol, ex.Message, ex),
    };

    /// <summary>Sends keep-alives and ends the read when nothing (not even a keep-alive answer) arrives in time.</summary>
    private async Task WatchAsync(CancellationTokenSource read)
    {
        var tick = _keepAlive < TimeSpan.FromSeconds(1) ? _keepAlive : TimeSpan.FromSeconds(1);
        var sinceKeepAlive = TimeSpan.Zero;
        try
        {
            while (!read.IsCancellationRequested)
            {
                await Task.Delay(tick, _time, read.Token).ConfigureAwait(false);
                sinceKeepAlive += tick;
                if (sinceKeepAlive >= _keepAlive)
                {
                    sinceKeepAlive = TimeSpan.Zero;
                    await _client.SendWithoutResponseAsync("OPTIONS", _playUrl, read.Token).ConfigureAwait(false);
                }

                if (_client.SinceLastReceive > _dead)
                {
                    LogSilent(_logger, _dead.TotalSeconds);
                    await read.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // reading ended
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // the connection broke while sending a keep-alive: end the read, it reports the loss
            await read.CancelAsync().ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "RTSP TEARDOWN of the event stream failed: {Reason}")]
    private static partial void LogTeardownFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Event stream silent for {Seconds} s (no packet, no keep-alive answer): connection lost")]
    private static partial void LogSilent(ILogger logger, double seconds);
}
