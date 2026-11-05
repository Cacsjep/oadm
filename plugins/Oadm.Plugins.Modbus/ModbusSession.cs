using System.Buffers.Binary;
using System.Globalization;
using System.Net.Sockets;

using Oadm.Sdk.Actions;

namespace Oadm.Plugins.Modbus;

/// <summary>What an action writes through a session; a session is never asked to read.</summary>
public interface IModbusWriter
{
    /// <summary>Writes one coil (function 05).</summary>
    Task WriteCoilAsync(ushort address, bool state, CancellationToken ct);

    /// <summary>Writes holding registers: one with function 06, several with function 16.</summary>
    Task WriteRegistersAsync(ushort address, IReadOnlyList<ushort> values, CancellationToken ct);
}

/// <summary>Makes the sessions of a Modbus recipient.</summary>
public sealed class ModbusSessionFactory : IRecipientSessionFactory
{
    public IRecipientSession Create(RecipientSessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new ModbusSession(request.Get("host") ?? string.Empty, request.GetInt("port", ModbusRecipient.DefaultPort), (byte)request.GetInt("unitId", 1));
    }
}

/// <summary>
/// One Modbus TCP connection, our own client: the three write functions and nothing else. Requests go one at a time, because many
/// PLCs serve only one; a reply belongs to its request by transaction id. The reader runs all the time, which is how a closed
/// connection shows at once. A write without an answer ends the connection: its bytes may still wait in the kernel, and an abort
/// throws them away, so that it cannot fire later.
/// </summary>
/// <param name="host">The PLC or gateway.</param>
/// <param name="port">Its port.</param>
/// <param name="unitId">The unit behind it.</param>
/// <param name="time">The clock of the reply timeout.</param>
/// <param name="replyTimeout">The wait for one reply; default <see cref="DefaultReplyTimeout"/>.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs", Justification = "The user's own PLC, not a device: it is not reached through the host's device routes.")]
public sealed class ModbusSession(string host, int port, byte unitId, TimeProvider? time = null, TimeSpan? replyTimeout = null) : IRecipientSession, IModbusWriter
{
    /// <summary>One write, from the moment it is its turn. A PLC answers in milliseconds; a machine must not stand behind a slow one for long.</summary>
    public static readonly TimeSpan DefaultReplyTimeout = TimeSpan.FromSeconds(5);

    /// <summary>An idle connection that was cut shows after about a minute, also when nothing is written.</summary>
    private const int KeepAliveIdleSeconds = 30;
    private const int KeepAliveIntervalSeconds = 10;
    private const int KeepAliveProbes = 3;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly TimeSpan _replyTimeout = replyTimeout ?? DefaultReplyTimeout;

    // Never disposed: a write that is in the queue at the close still gets its turn and its plain text.
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private TcpClient? _client;
    private Stream? _stream;
    private Task _reader = Task.CompletedTask;
    private TaskCompletionSource<ModbusReply>? _pending;
    private ushort _pendingId;
    private ushort _nextId;
    private int _ended;
    private int _disposed;

    public event EventHandler<RecipientDroppedEventArgs>? Dropped;

    /// <summary>Whether the operating system watches the connection while nothing is written.</summary>
    internal bool KeepsAlive => _client?.Client.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive) is int value && value != 0;

    private string Target => string.Create(CultureInfo.InvariantCulture, $"{host}:{port}");

    public async Task ConnectAsync(CancellationToken ct)
    {
        _client = new TcpClient { NoDelay = true };
        try
        {
            await _client.ConnectAsync(host, port, ct).ConfigureAwait(false);
            var socket = _client.Client;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, KeepAliveIdleSeconds);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, KeepAliveIntervalSeconds);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, KeepAliveProbes);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            throw new InvalidOperationException($"{Target} cannot be reached: {ex.Message}", ex);
        }

        _stream = _client.GetStream();
        _reader = Task.Run(ReadRepliesAsync, CancellationToken.None);
    }

    public Task WriteCoilAsync(ushort address, bool state, CancellationToken ct) =>
        SendAsync(ModbusFrames.WriteCoil(address, state), ct);

    public Task WriteRegistersAsync(ushort address, IReadOnlyList<ushort> values, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(values);
        return SendAsync(values.Count == 1 ? ModbusFrames.WriteRegister(address, values[0]) : ModbusFrames.WriteRegisters(address, values), ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _ended, 1);
        await _stop.CancelAsync().ConfigureAwait(false);
        _client?.Dispose(); // ends the reader
        await _reader.ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task SendAsync(byte[] pdu, CancellationToken ct)
    {
        ThrowIfEnded();
        await _oneAtATime.WaitAsync(ct).ConfigureAwait(false);
        var sent = false;
        try
        {
            using var timeout = new CancellationTokenSource(_replyTimeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            var id = ++_nextId;
            var waiting = new TaskCompletionSource<ModbusReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingId = id;
            Interlocked.Exchange(ref _pending, waiting);
            ThrowIfEnded(); // it ended during the wait for the turn, and the reader will not tell this one
            linked.Token.ThrowIfCancellationRequested();
            sent = true;
            await _stream!.WriteAsync(ModbusFrames.Request(id, unitId, pdu), linked.Token).ConfigureAwait(false);
            var reply = await waiting.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            ModbusFrames.CheckReply(pdu, reply.Pdu, unitId, reply.UnitId);
        }
        catch (OperationCanceledException)
        {
            if (sent)
            {
                Abort(ct.IsCancellationRequested ? "A write was cancelled" : $"{Target} did not answer");
            }

            if (ct.IsCancellationRequested)
            {
                throw;
            }

            throw new InvalidOperationException($"{Target} did not answer. The write may have happened.");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw new InvalidOperationException($"The connection to {Target} ended. The write may have happened.", ex);
        }
        finally
        {
            Volatile.Write(ref _pending, null);
            _oneAtATime.Release();
        }
    }

    private void ThrowIfEnded()
    {
        if (_stream is null || Volatile.Read(ref _ended) != 0)
        {
            throw new InvalidOperationException(RecipientSessionTexts.NotConnected(null));
        }
    }

    /// <summary>Ends the connection with a reset: a graceful close would still deliver what the kernel holds.</summary>
    private void Abort(string reason)
    {
        var first = Interlocked.Exchange(ref _ended, 1) == 0;
        try
        {
            if (_client?.Client is { } socket)
            {
                socket.LingerState = new LingerOption(true, 0);
                socket.Close(); // not the client's Dispose, which shuts the connection down in order first
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // Gone already.
        }

        if (first)
        {
            Dropped?.Invoke(this, new RecipientDroppedEventArgs(reason));
        }
    }

    private async Task ReadRepliesAsync()
    {
        var reason = "The connection ended";
        try
        {
            var header = new byte[ModbusFrames.HeaderLength];
            while (true)
            {
                await _stream!.ReadExactlyAsync(header, _stop.Token).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4)) - 1; // the length counts the unit id
                if (BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2)) != 0 || length is < 1 or > ModbusFrames.MaxPduLength)
                {
                    reason = "The other side does not speak Modbus";
                    return;
                }

                var pdu = new byte[length];
                await _stream.ReadExactlyAsync(pdu, _stop.Token).ConfigureAwait(false);
                var waiting = Volatile.Read(ref _pending);
                if (waiting is not null && BinaryPrimitives.ReadUInt16BigEndian(header) == _pendingId)
                {
                    waiting.TrySetResult(new ModbusReply(header[ModbusFrames.HeaderLength - 1], pdu));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException or ObjectDisposedException or OperationCanceledException)
        {
            // Closed by the other side or by us; the reason stays general.
        }
        finally
        {
            var first = Interlocked.Exchange(ref _ended, 1) == 0;
            Volatile.Read(ref _pending)?.TrySetException(new IOException(reason));
            if (first)
            {
                Dropped?.Invoke(this, new RecipientDroppedEventArgs(reason));
            }
        }
    }

    private sealed record ModbusReply(byte UnitId, byte[] Pdu);
}
