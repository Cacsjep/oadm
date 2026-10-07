using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Plugins.NtpServer.Protocol;

namespace Oadm.Plugins.NtpServer.Serving;

/// <summary>
/// The UDP responder: one socket per bound address (IPv4 and IPv6 separately), one receive loop each. Every request is
/// answered from <see cref="TimeSourceState"/> as it is right now (never waiting for anything), after the rate limiter.
/// The loop reuses one receive buffer, one answer buffer and one <see cref="SocketAddress"/>: no allocation per packet.
/// </summary>
public sealed partial class NtpUdpServer : IAsyncDisposable
{
    /// <summary>Offsets beyond this are shown as unknown (clients that randomize the transmit timestamp, e.g. chrony).</summary>
    private static readonly double MaxShownOffsetMilliseconds = TimeSpan.FromDays(1).TotalMilliseconds;

    private readonly IReadOnlyList<IPEndPoint> _endpoints;
    private readonly Func<TimeSourceState> _source;
    private readonly NtpRateLimiter _limiter;
    private readonly RequestLog _log;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly List<Socket> _sockets = [];
    private readonly List<Task> _loops = [];
    private CancellationTokenSource? _cts;
    private long _answered;
    private long _dropped;

    public NtpUdpServer(
        IReadOnlyList<IPEndPoint> endpoints,
        Func<TimeSourceState> source,
        NtpRateLimiter limiter,
        RequestLog log,
        TimeProvider? time = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(limiter);
        ArgumentNullException.ThrowIfNull(log);
        if (endpoints.Count == 0)
        {
            throw new ArgumentException("No address to listen on.", nameof(endpoints));
        }

        _endpoints = endpoints;
        _source = source;
        _limiter = limiter;
        _log = log;
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The bound endpoints (with the real port when 0 was requested).</summary>
    public IReadOnlyList<IPEndPoint> LocalEndpoints { get; private set; } = [];

    public long AnsweredCount => Interlocked.Read(ref _answered);

    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>Binds every endpoint and starts serving. Throws <see cref="SocketException"/> (nothing bound) when one fails.</summary>
    public void Start()
    {
        if (_cts is not null)
        {
            throw new InvalidOperationException("The server is already running.");
        }

        var bound = new List<IPEndPoint>();
        try
        {
            foreach (var endpoint in _endpoints)
            {
                var socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                _sockets.Add(socket);
                if (endpoint.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    socket.DualMode = false;
                }

                if (OperatingSystem.IsWindows())
                {
                    // SIO_UDP_CONNRESET off: an ICMP "port unreachable" for an earlier answer must not fail the next receive.
                    socket.IOControl(unchecked((int)0x9800000C), [0, 0, 0, 0], null);
                }

                socket.Bind(endpoint);
                bound.Add((IPEndPoint)socket.LocalEndPoint!);
            }
        }
        catch
        {
            CloseSockets();
            throw;
        }

        LocalEndpoints = bound;
        _cts = new CancellationTokenSource();
        foreach (var socket in _sockets)
        {
            _loops.Add(Task.Run(() => ReceiveLoopAsync(socket, _cts.Token)));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is { } cts)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            CloseSockets();
            try
            {
                await Task.WhenAll(_loops).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // stopping
            }

            cts.Dispose();
            _cts = null;
        }
        else
        {
            CloseSockets();
        }
    }

    private void CloseSockets()
    {
        foreach (var socket in _sockets)
        {
            socket.Dispose();
        }

        _sockets.Clear();
    }

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[1024];
        var answer = new byte[NtpPacket.Length];
        var remote = new SocketAddress(socket.AddressFamily);
        while (!ct.IsCancellationRequested)
        {
            int length;
            try
            {
                length = await socket.ReceiveFromAsync(buffer, SocketFlags.None, remote, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize or SocketError.NetworkReset)
            {
                continue;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted)
            {
                return;
            }

            var receivedUtc = _time.GetUtcNow().UtcDateTime;
            try
            {
                await HandleAsync(socket, buffer.AsMemory(0, length), answer, remote, receivedUtc, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                LogSendFailed(ex.SocketErrorCode);
            }
        }
    }

    private async ValueTask HandleAsync(Socket socket, ReadOnlyMemory<byte> request, byte[] answer, SocketAddress remote, DateTime receivedUtc, CancellationToken ct)
    {
        if (!NtpResponder.IsValidRequest(request.Span, out var parsed) || !ClientAddress.TryGetKey(remote, out var client))
        {
            Interlocked.Increment(ref _dropped);
            return; // silently: control/private/broadcast/symmetric modes, other versions, short packets
        }

        var rate = _limiter.Check(client);
        switch (rate.Decision)
        {
            case RateDecision.GlobalDrop:
                Interlocked.Increment(ref _dropped);
                return;
            case RateDecision.Drop:
            case RateDecision.DropWithKiss:
                Interlocked.Increment(ref _dropped);
                if (rate.Log)
                {
                    _log.Add(receivedUtc, client, double.NaN, RequestResult.RateLimited);
                }

                if (rate.Decision == RateDecision.DropWithKiss)
                {
                    NtpResponder.BuildKissOfDeath(parsed, receivedUtc, "RATE", answer);
                    await socket.SendToAsync(answer, SocketFlags.None, remote, ct).ConfigureAwait(false);
                }

                return;
        }

        if (!NtpResponder.TryBuildResponse(request.Span, receivedUtc, _source(), answer, out _))
        {
            return;
        }

        NtpPacket.WriteTransmitTimestamp(answer, NtpTimestamp.FromDateTime(_time.GetUtcNow().UtcDateTime));
        await socket.SendToAsync(answer, SocketFlags.None, remote, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _answered);

        var offset = (NtpTimestamp.ToDateTime(parsed.TransmitTimestamp, receivedUtc) - receivedUtc).TotalMilliseconds;
        _log.Add(receivedUtc, client, Math.Abs(offset) > MaxShownOffsetMilliseconds ? double.NaN : offset, RequestResult.Answered);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "NTP answer could not be sent: {Error}")]
    private partial void LogSendFailed(SocketError error);
}
