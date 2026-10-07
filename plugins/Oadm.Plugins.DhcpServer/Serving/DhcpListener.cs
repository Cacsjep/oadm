using System.Buffers;
using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Plugins.DhcpServer.Protocol;
using Oadm.Sdk.Network;

namespace Oadm.Plugins.DhcpServer.Serving;

/// <summary>Message rate limits of the DHCP server.</summary>
public sealed record DhcpRateLimits
{
    /// <summary>
    /// Per MAC address: 10 messages back to back, then one every 3 s (a booting device needs 2-4, a renewal 1); notice 0 =
    /// one "rate limited" log line per device per minute. Global: 500 messages per second. At most 10,000 devices tracked
    /// (least recently seen forgotten first), idle ones after 10 min.
    /// </summary>
    public KeyedRateLimitOptions PerMac { get; init; } = new()
    {
        Burst = 10,
        RefillSeconds = 3,
        MaxClients = 10_000,
        IdleExpiry = TimeSpan.FromMinutes(10),
        GlobalPerSecond = 500,
        NoticeIntervals = [TimeSpan.FromMinutes(1)],
    };

    /// <summary>Messages handled at the same time (in-use probes take up to 2.5 s); more are dropped.</summary>
    public int MaxInFlight { get; init; } = 64;
}

/// <summary>
/// The receive loop on port 67 of one interface: parse (pooled buffers), rate limit per MAC and globally, hand the
/// message to the <see cref="DhcpEngine"/> (in-flight bounded), send the reply. Never blocks on a single client.
/// </summary>
public sealed partial class DhcpListener : IAsyncDisposable
{
    private readonly IDhcpSocket _socket;
    private readonly DhcpEngine _engine;
    private readonly KeyedRateLimiter<ulong> _limiter;
    private readonly SemaphoreSlim _inFlight;
    private readonly int _maxInFlight;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private long _received;
    private long _dropped;

    public DhcpListener(IDhcpSocket socket, DhcpEngine engine, KeyedRateLimiter<ulong> limiter, int maxInFlight, ILogger? logger = null)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _limiter = limiter ?? throw new ArgumentNullException(nameof(limiter));
        _maxInFlight = maxInFlight;
        _inFlight = new SemaphoreSlim(maxInFlight, maxInFlight);
        _logger = logger ?? NullLogger.Instance;
    }

    public long ReceivedCount => Interlocked.Read(ref _received);

    public long DroppedCount => Interlocked.Read(ref _dropped);

    public void Start() => _loop ??= Task.Run(() => LoopAsync(_cts.Token));

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        await _socket.DisposeAsync().ConfigureAwait(false);
        if (_loop is { } loop)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // stopping
            }
        }

        // Wait for handlers still running (they see the cancelled token).
        for (var i = 0; i < 300 && _inFlight.CurrentCount < _maxInFlight; i++)
        {
            await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);
        }

        if (_inFlight.CurrentCount == _maxInFlight)
        {
            _inFlight.Dispose();
            _cts.Dispose();
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var buffer = new byte[DhcpMessage.MaxLength];
        while (!ct.IsCancellationRequested)
        {
            DhcpReceived received;
            try
            {
                received = await _socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted)
            {
                return;
            }
            catch (SocketException ex)
            {
                LogReceiveFailed(ex.SocketErrorCode);
                continue;
            }

            Interlocked.Increment(ref _received);
            if (!DhcpMessage.TryRead(buffer.AsSpan(0, received.Length), out var message, out var error) || message is null)
            {
                Interlocked.Increment(ref _dropped);
                LogMalformed(received.Remote, error);
                continue;
            }

            if (message.Op != DhcpMessage.BootRequest)
            {
                continue; // replies of other servers (and our own broadcasts on some OSes)
            }

            var rate = _limiter.Check(message.Mac);
            if (rate.Decision != RateLimitDecision.Allow)
            {
                Interlocked.Increment(ref _dropped);
                if (rate.IsDue(0))
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                    {
                        LogRateLimited(MacAddress.Format(message.Mac));
                    }
                }

                continue;
            }

            if (!_inFlight.Wait(0, CancellationToken.None))
            {
                Interlocked.Increment(ref _dropped);
                continue; // busy (probes running): the client asks again
            }

            _ = HandleAsync(message, ct);
        }
    }

    private async Task HandleAsync(DhcpMessage message, CancellationToken ct)
    {
        var bytes = ArrayPool<byte>.Shared.Rent(DhcpMessage.MaxLength);
        try
        {
            var reply = await _engine.HandleAsync(message, ct).ConfigureAwait(false);
            if (reply is not null)
            {
                var length = reply.Message.Write(bytes);
                await _socket.SendAsync(bytes.AsMemory(0, length), reply.Destination, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
        catch (ObjectDisposedException)
        {
            // stopping
        }
        catch (SocketException ex)
        {
            LogSendFailed(ex.SocketErrorCode);
        }
#pragma warning disable CA1031 // One bad message must never stop the server.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogHandleFailed(ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
            _inFlight.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "DHCP: ignored malformed message from {Remote}: {Error}")]
    private partial void LogMalformed(IPEndPoint remote, string? error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DHCP: {Mac} sends too many messages, rate limited")]
    private partial void LogRateLimited(string mac);

    [LoggerMessage(Level = LogLevel.Debug, Message = "DHCP: receive failed: {Error}")]
    private partial void LogReceiveFailed(SocketError error);

    [LoggerMessage(Level = LogLevel.Debug, Message = "DHCP: reply could not be sent: {Error}")]
    private partial void LogSendFailed(SocketError error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DHCP: handling a message failed")]
    private partial void LogHandleFailed(Exception ex);
}
