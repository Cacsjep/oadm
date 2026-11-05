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
/// PLCs serve only one; a reply belongs to its request by transaction id, so a late reply after a timeout is ignored. The reader
/// runs all the time, which is how a closed connection shows at once.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs", Justification = "The user's own PLC, not a device: it is not reached through the host's device routes.")]
public sealed class ModbusSession(string host, int port, byte unitId) : IRecipientSession, IModbusWriter
{
    /// <summary>One write. A PLC answers in milliseconds; a machine must not stand behind a slow one for long.</summary>
    internal static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private TcpClient? _client;
    private Stream? _stream;
    private Task _reader = Task.CompletedTask;
    private TaskCompletionSource<byte[]>? _pending;
    private ushort _pendingId;
    private ushort _nextId;
    private int _ended;

    public event EventHandler<RecipientDroppedEventArgs>? Dropped;

    private string Target => string.Create(CultureInfo.InvariantCulture, $"{host}:{port}");

    public async Task ConnectAsync(CancellationToken ct)
    {
        _client = new TcpClient { NoDelay = true };
        try
        {
            await _client.ConnectAsync(host, port, ct).ConfigureAwait(false);
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
        Interlocked.Exchange(ref _ended, 1);
        await _stop.CancelAsync().ConfigureAwait(false);
        _client?.Dispose(); // ends the reader
        await _reader.ConfigureAwait(false);
        _oneAtATime.Dispose();
        _stop.Dispose();
    }

    private async Task SendAsync(byte[] pdu, CancellationToken ct)
    {
        var stream = _stream ?? throw new InvalidOperationException(RecipientSessionTexts.NotConnected(null));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ReplyTimeout);
        await _oneAtATime.WaitAsync(timeout.Token).ConfigureAwait(false);
        try
        {
            var id = ++_nextId;
            var waiting = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingId = id;
            Volatile.Write(ref _pending, waiting);
            await stream.WriteAsync(ModbusFrames.Request(id, unitId, pdu), timeout.Token).ConfigureAwait(false);
            ModbusFrames.CheckReply(pdu, await waiting.Task.WaitAsync(timeout.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"{Target} did not answer. The write may have happened.");
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"The connection to {Target} ended: {ex.Message}", ex);
        }
        finally
        {
            Volatile.Write(ref _pending, null);
            _oneAtATime.Release();
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
                    waiting.TrySetResult(pdu);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or EndOfStreamException or ObjectDisposedException or OperationCanceledException)
        {
            // Closed by the other side or by us; the reason stays general.
        }
        finally
        {
            Volatile.Read(ref _pending)?.TrySetException(new IOException(reason));
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                Dropped?.Invoke(this, new RecipientDroppedEventArgs(reason));
            }
        }
    }
}
