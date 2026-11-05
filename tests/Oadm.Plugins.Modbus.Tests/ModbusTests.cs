using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Modbus;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Modbus.Tests;

/// <summary>A PLC on the loopback that records the frames it gets and answers as a test says.</summary>
internal sealed class FakePlc : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private TcpClient? _client;

    public FakePlc()
    {
        _listener.Start();
        _loop = Task.Run(RunAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public ConcurrentQueue<byte[]> Requests { get; } = new();

    /// <summary>The reply to a frame; null closes the connection, nothing (<see cref="Silent"/>) leaves it waiting. Default: the echo a write gets.</summary>
    public Func<byte[], byte[]?> Answer { get; set; } = Echo;

    /// <summary>The connection ended with a reset, so nothing it still held was delivered.</summary>
    public bool WasReset { get; private set; }

    public static byte[] Silent(byte[] request) => [];

    public static byte[] Echo(byte[] request) => request.Length == 12 ? request : Reply(request, request.AsSpan(7, 5).ToArray());

    public static byte[] Reply(byte[] request, byte[] pdu)
    {
        var frame = new byte[7 + pdu.Length];
        request.AsSpan(0, 4).CopyTo(frame);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(pdu.Length + 1));
        frame[6] = request[6];
        pdu.CopyTo(frame, 7);
        return frame;
    }

    public void CloseConnection() => _client?.Dispose();

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _client?.Dispose();
        _listener.Stop();
        await _loop;
        _stop.Dispose();
    }

    private async Task RunAsync()
    {
        try
        {
            _client = await _listener.AcceptTcpClientAsync(_stop.Token);
            var stream = _client.GetStream();
            var header = new byte[7];
            while (true)
            {
                await stream.ReadExactlyAsync(header, _stop.Token);
                var pdu = new byte[BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4)) - 1];
                await stream.ReadExactlyAsync(pdu, _stop.Token);
                byte[] request = [.. header, .. pdu];
                Requests.Enqueue(request);
                if (Answer(request) is not { } reply)
                {
                    _client.Dispose();
                    return;
                }

                await stream.WriteAsync(reply, _stop.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
            // The test ended or the client went.
            WasReset = ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset };
        }
    }
}

public sealed class ModbusFramesTests
{
    [Fact]
    public void ACoilOnAndOffAreTheValuesOfTheProtocol()
    {
        Assert.Equal([5, 0x00, 0x10, 0xFF, 0x00], ModbusFrames.WriteCoil(16, on: true));
        Assert.Equal([5, 0x00, 0x10, 0x00, 0x00], ModbusFrames.WriteCoil(16, on: false));
    }

    [Fact]
    public void RegistersGoInOneRequest()
    {
        Assert.Equal([6, 0x00, 0x02, 0x12, 0x34], ModbusFrames.WriteRegister(2, 0x1234));
        Assert.Equal([16, 0x00, 0x02, 0x00, 0x02, 4, 0x12, 0x34, 0x56, 0x78], ModbusFrames.WriteRegisters(2, [0x1234, 0x5678]));
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusFrames.WriteRegisters(0, new ushort[ModbusFrames.MaxRegisters + 1]));
    }

    [Fact]
    public void TheHeaderCountsTheUnitId()
    {
        Assert.Equal([0x00, 0x07, 0x00, 0x00, 0x00, 0x06, 9, 6, 0, 2, 0, 1], ModbusFrames.Request(7, 9, ModbusFrames.WriteRegister(2, 1)));
    }

    [Theory]
    [InlineData(2, "The PLC has no such address. Addresses count from 0.")]
    [InlineData(3, "The PLC refused the value.")]
    [InlineData(5, "The PLC accepted the write and is still working on it.")]
    [InlineData(11, "The gateway cannot reach the PLC. Check the unit ID.")]
    [InlineData(77, "The PLC refused the write (code 77).")]
    public void AnExceptionReplySaysWhy(byte code, string text)
    {
        var refused = Assert.Throws<InvalidOperationException>(() => ModbusFrames.CheckReply(ModbusFrames.WriteCoil(1, true), [0x85, code]));
        Assert.Equal(text, refused.Message);
    }

    [Fact]
    public void AReplyThatDoesNotEchoTheRequestIsNotAWrite()
    {
        Assert.Throws<InvalidOperationException>(() => ModbusFrames.CheckReply(ModbusFrames.WriteRegister(2, 1), ModbusFrames.WriteRegister(2, 9)));
        Assert.Throws<InvalidOperationException>(() => ModbusFrames.CheckReply(ModbusFrames.WriteRegister(2, 1), [6, 0]));
    }

    [Fact]
    public void AnEchoHasExactlyItsLength()
    {
        byte[] request = ModbusFrames.WriteRegister(2, 1);

        ModbusFrames.CheckReply(request, request);
        Assert.Throws<InvalidOperationException>(() => ModbusFrames.CheckReply(request, [.. request, 0]));
    }

    [Fact]
    public void AnotherUnitIsNotTheAnswer()
    {
        byte[] request = ModbusFrames.WriteRegister(2, 1);

        ModbusFrames.CheckReply(request, request, 3, 3);
        var refused = Assert.Throws<InvalidOperationException>(() => ModbusFrames.CheckReply(request, request, 3, 4));
        Assert.Equal("The PLC gave an unexpected answer. The write may have happened.", refused.Message);
    }
}

public sealed class ModbusValueTests
{
    private static ModbusWrite Number(string value, string format, string order = ModbusValue.HighWordFirst) =>
        ModbusValue.Encode(ModbusValue.Number, value, format, order, 0, string.Empty);

    [Fact]
    public void NumbersBecomeRegisters()
    {
        Assert.Equal([0xFFFF], Number("-1", ModbusValue.Signed16).Registers);
        Assert.Equal([65535], Number("65535", ModbusValue.Unsigned16).Registers);
        Assert.Equal([0x0001, 0x0000], Number("65536", ModbusValue.Unsigned32).Registers);
        Assert.Equal([0x0000, 0x0001], Number("65536", ModbusValue.Unsigned32, ModbusValue.LowWordFirst).Registers);
        Assert.Equal([0xFFFF, 0xFFFF], Number("-1", ModbusValue.Signed32).Registers);
        Assert.Equal([0x3FC0, 0x0000], Number("1.5", ModbusValue.Decimal32).Registers);
    }

    [Theory]
    [InlineData("32768", ModbusValue.Signed16)]
    [InlineData("-1", ModbusValue.Unsigned16)]
    [InlineData("1.5", ModbusValue.Unsigned16)]
    [InlineData("abc", ModbusValue.Decimal32)]
    [InlineData("NaN", ModbusValue.Decimal32)]
    [InlineData("4294967296", ModbusValue.Unsigned32)]
    public void ANumberThatDoesNotFitIsRefused(string value, string format)
    {
        Assert.Throws<FormatException>(() => Number(value, format));
    }

    [Theory]
    [InlineData("1e-50")]
    [InlineData("-1E-46")]
    [InlineData("0.00000000000000000000000000000000000000000000000001")]
    public void ANumberTooSmallForAFloatIsNotWrittenAsZero(string value)
    {
        var refused = Assert.Throws<FormatException>(() => Number(value, ModbusValue.Decimal32));
        Assert.Equal("This number is too small for a 32-bit float.", refused.Message);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("0.0", 0)]
    [InlineData("0e5", 0)]
    [InlineData("-0", 0x8000)]
    [InlineData("16777217", 0x4B80)]
    public void ZeroAndALargeWholeNumberStayFloats(string value, int high)
    {
        Assert.Equal(high, Number(value, ModbusValue.Decimal32).Registers[0]);
    }

    [Theory]
    [InlineData(65535, 1, true)]
    [InlineData(65536, 1, false)]
    [InlineData(65534, 2, true)]
    [InlineData(65535, 2, false)]
    public void AWriteStaysInsideTheAddresses(int address, int count, bool fits)
    {
        Assert.Equal(fits, ModbusValue.AddressProblem(address, count) is null);
    }

    [Fact]
    public void TheCountFollowsTheType()
    {
        Assert.Equal(1, ModbusValue.Count(ModbusValue.OnOff, ModbusValue.Signed32, 16));
        Assert.Equal(1, ModbusValue.Count(ModbusValue.Pulse, string.Empty, 16));
        Assert.Equal(1, ModbusValue.Count(ModbusValue.Number, ModbusValue.Unsigned16, 16));
        Assert.Equal(2, ModbusValue.Count(ModbusValue.Number, ModbusValue.Decimal32, 16));
        Assert.Equal(3, ModbusValue.Count(ModbusValue.Text, string.Empty, 5));
    }

    [Fact]
    public void TextIsPaddedToItsLength()
    {
        var write = ModbusValue.Encode(ModbusValue.Text, "ABC", string.Empty, string.Empty, 6, ModbusValue.FirstCharacterHigh);
        Assert.Equal([0x4142, 0x4300, 0x0000], write.Registers);
        var swapped = ModbusValue.Encode(ModbusValue.Text, "AB", string.Empty, string.Empty, 2, ModbusValue.FirstCharacterLow);
        Assert.Equal([0x4241], swapped.Registers);
    }

    [Theory]
    [InlineData("ABCDE", 4)]
    [InlineData("Grüße", 16)]
    public void TextThatDoesNotFitIsRefused(string value, int length)
    {
        Assert.Throws<FormatException>(() => ModbusValue.Encode(ModbusValue.Text, value, string.Empty, string.Empty, length, ModbusValue.FirstCharacterHigh));
    }

    [Theory]
    [InlineData("on", true)]
    [InlineData("1", true)]
    [InlineData("OFF", false)]
    [InlineData("false", false)]
    public void OnAndOffAreUnderstood(string value, bool on)
    {
        Assert.Equal(on, ModbusValue.Encode(ModbusValue.OnOff, value, string.Empty, string.Empty, 0, string.Empty).Coil);
    }
}

public sealed class ModbusSessionTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private static async Task<ModbusSession> OpenAsync(FakePlc plc, TimeProvider? time = null, TimeSpan? replyTimeout = null)
    {
        var session = new ModbusSession("127.0.0.1", plc.Port, 3, time, replyTimeout ?? Generous);
        await session.ConnectAsync(CancellationToken.None);
        return session;
    }

    private static TaskCompletionSource<string> DropOf(ModbusSession session)
    {
        var dropped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Dropped += (_, e) => dropped.TrySetResult(e.Reason);
        return dropped;
    }

    [Fact]
    public async Task AWriteGoesOutWithItsUnitIdAndIsConfirmed()
    {
        await using var plc = new FakePlc();
        await using var session = await OpenAsync(plc);

        await session.WriteCoilAsync(4, true, CancellationToken.None);
        await session.WriteRegistersAsync(8, [1], CancellationToken.None);
        await session.WriteRegistersAsync(8, [1, 2], CancellationToken.None);

        var requests = plc.Requests.ToArray();
        Assert.Equal([5, 6, 16], requests.Select(r => r[7]));
        Assert.All(requests, r => Assert.Equal(3, r[6]));
    }

    [Fact]
    public async Task AnExceptionReplyFailsTheWriteAndKeepsTheConnection()
    {
        await using var plc = new FakePlc { Answer = r => FakePlc.Reply(r, [(byte)(r[7] | 0x80), 2]) };
        await using var session = await OpenAsync(plc);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => session.WriteCoilAsync(4, true, CancellationToken.None));
        Assert.Equal("The PLC has no such address. Addresses count from 0.", refused.Message);

        plc.Answer = FakePlc.Echo;
        await session.WriteCoilAsync(4, true, CancellationToken.None);
    }

    [Fact]
    public async Task ALateReplyOfAnotherTransactionIsNotTheAnswer()
    {
        await using var plc = new FakePlc
        {
            Answer = r =>
            {
                byte[] stale = [.. r];
                stale[1] ^= 0x55;
                return [.. stale, .. r]; // first a reply with a wrong id, then the right one
            },
        };
        await using var session = await OpenAsync(plc);

        await session.WriteCoilAsync(4, true, CancellationToken.None);
    }

    [Fact]
    public async Task AClosedConnectionFailsTheWriteAndDropsTheSession()
    {
        await using var plc = new FakePlc { Answer = _ => null };
        await using var session = await OpenAsync(plc);
        var dropped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Dropped += (_, e) => dropped.TrySetResult(e.Reason);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.WriteCoilAsync(4, true, CancellationToken.None));

        Assert.Equal("The connection ended", await dropped.Task.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task AReplyCutShortDropsTheSession()
    {
        await using var plc = new FakePlc { Answer = r => r[..9] };
        await using var session = await OpenAsync(plc);
        var dropped = DropOf(session);

        var write = session.WriteCoilAsync(4, true, CancellationToken.None);
        await Eventually.TrueAsync(() => !plc.Requests.IsEmpty, "the request");
        plc.CloseConnection(); // in the middle of the frame

        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => write);
        Assert.Equal($"The connection to 127.0.0.1:{plc.Port} ended. The write may have happened.", failed.Message);
        Assert.Equal("The connection ended", await dropped.Task.WaitAsync(Generous));
    }

    [Fact]
    public async Task AWriteWithoutAnAnswerResetsTheConnection()
    {
        var time = new FakeTimeProvider();
        await using var plc = new FakePlc { Answer = FakePlc.Silent };
        await using var session = await OpenAsync(plc, time, TimeSpan.FromSeconds(5));
        var dropped = DropOf(session);

        var write = session.WriteCoilAsync(4, true, CancellationToken.None);
        await Eventually.TrueAsync(() => !plc.Requests.IsEmpty, "the request");
        time.Advance(TimeSpan.FromSeconds(5));

        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => write);
        Assert.Equal($"127.0.0.1:{plc.Port} did not answer. The write may have happened.", failed.Message);
        Assert.Equal($"127.0.0.1:{plc.Port} did not answer", await dropped.Task.WaitAsync(Generous));
        await Eventually.TrueAsync(() => plc.WasReset, "the reset reaches the PLC");
        var next = await Assert.ThrowsAsync<InvalidOperationException>(() => session.WriteCoilAsync(4, true, CancellationToken.None));
        Assert.Equal(RecipientSessionTexts.NotConnected(null), next.Message);
    }

    [Fact]
    public async Task ACancelAfterSendingResetsTheConnection()
    {
        await using var plc = new FakePlc { Answer = FakePlc.Silent };
        await using var session = await OpenAsync(plc);
        var dropped = DropOf(session);
        using var cancel = new CancellationTokenSource();

        var write = session.WriteCoilAsync(4, true, cancel.Token);
        await Eventually.TrueAsync(() => !plc.Requests.IsEmpty, "the request");
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
        Assert.Equal("A write was cancelled", await dropped.Task.WaitAsync(Generous));
        await Eventually.TrueAsync(() => plc.WasReset, "the reset reaches the PLC");
    }

    [Fact]
    public async Task AClosedSessionWithAWriteWaitingResetsTheConnection()
    {
        await using var plc = new FakePlc { Answer = FakePlc.Silent };
        await using var session = await OpenAsync(plc);

        var write = session.WriteCoilAsync(4, true, CancellationToken.None);
        await Eventually.TrueAsync(() => !plc.Requests.IsEmpty, "the request");
        await session.DisposeAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => write);
        await Eventually.TrueAsync(() => plc.WasReset, "the reset reaches the PLC");
    }

    [Fact]
    public async Task ACancelInTheQueueKeepsTheConnection()
    {
        using var turn = new ManualResetEventSlim();
        await using var plc = new FakePlc
        {
            Answer = r =>
            {
                turn.Wait(Generous);
                return FakePlc.Echo(r);
            },
        };
        await using var session = await OpenAsync(plc);
        var dropped = DropOf(session);
        using var cancel = new CancellationTokenSource();

        var first = session.WriteCoilAsync(4, true, CancellationToken.None);
        await Eventually.TrueAsync(() => !plc.Requests.IsEmpty, "the first request");
        var second = session.WriteCoilAsync(5, true, cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        turn.Set();
        await first;

        await session.WriteCoilAsync(6, true, CancellationToken.None);
        Assert.False(dropped.Task.IsCompleted);
        Assert.Equal(2, plc.Requests.Count);
    }

    [Fact]
    public async Task TheReplyTimeStartsAtTheTurnOfTheWrite()
    {
        var time = new FakeTimeProvider();
        using var firstReply = new ManualResetEventSlim();
        using var secondReply = new ManualResetEventSlim();
        await using var plc = new FakePlc();
        plc.Answer = r =>
        {
            (plc.Requests.Count == 1 ? firstReply : secondReply).Wait(Generous);
            return FakePlc.Echo(r);
        };
        await using var session = await OpenAsync(plc, time, TimeSpan.FromSeconds(5));

        var first = session.WriteCoilAsync(1, true, CancellationToken.None);
        await Eventually.TrueAsync(() => plc.Requests.Count == 1, "the first request");
        var second = session.WriteCoilAsync(2, true, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(4));
        firstReply.Set();
        await first.WaitAsync(Generous);
        await Eventually.TrueAsync(() => plc.Requests.Count == 2, "the second request");
        time.Advance(TimeSpan.FromSeconds(4)); // 8 s after it was asked for, 4 s after its turn
        secondReply.Set();

        await second.WaitAsync(Generous);
    }

    [Fact]
    public async Task AWriteInFlightAtTheCloseHasAPlainText()
    {
        await using var plc = new FakePlc { Answer = FakePlc.Silent };
        var session = await OpenAsync(plc);

        var inFlight = session.WriteCoilAsync(4, true, CancellationToken.None);
        await Eventually.TrueAsync(() => !plc.Requests.IsEmpty, "the request");
        var queued = session.WriteCoilAsync(5, true, CancellationToken.None);
        await session.DisposeAsync();

        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => inFlight);
        Assert.Equal($"The connection to 127.0.0.1:{plc.Port} ended. The write may have happened.", failed.Message);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => queued);
        Assert.Equal(RecipientSessionTexts.NotConnected(null), refused.Message);
    }

    [Fact]
    public async Task AWriteAfterTheCloseIsNotConnected()
    {
        await using var plc = new FakePlc();
        var session = await OpenAsync(plc);

        await session.DisposeAsync();
        await session.DisposeAsync();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => session.WriteCoilAsync(4, true, CancellationToken.None));
        Assert.Equal(RecipientSessionTexts.NotConnected(null), refused.Message);
    }

    [Fact]
    public async Task AReplyOfAnotherUnitIsNotTheAnswer()
    {
        await using var plc = new FakePlc
        {
            Answer = r =>
            {
                byte[] reply = [.. FakePlc.Echo(r)];
                reply[6] = 9;
                return reply;
            },
        };
        await using var session = await OpenAsync(plc);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => session.WriteCoilAsync(4, true, CancellationToken.None));

        Assert.Equal("The PLC gave an unexpected answer. The write may have happened.", refused.Message);
    }

    [Fact]
    public async Task TheOperatingSystemWatchesAnIdleConnection()
    {
        await using var plc = new FakePlc();
        await using var session = await OpenAsync(plc);

        Assert.True(session.KeepsAlive);
    }

    [Fact]
    public async Task AnotherServiceOnThePortIsNotModbus()
    {
        await using var plc = new FakePlc { Answer = _ => "HTTP/1.1 400 Bad Request\r\n\r\n"u8.ToArray() };
        await using var session = await OpenAsync(plc);
        var dropped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Dropped += (_, e) => dropped.TrySetResult(e.Reason);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.WriteCoilAsync(4, true, CancellationToken.None));

        Assert.Equal("The other side does not speak Modbus", await dropped.Task.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task AWriteOfManyActionsGoesOneAtATime()
    {
        await using var plc = new FakePlc();
        await using var session = await OpenAsync(plc);

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => session.WriteCoilAsync((ushort)i, true, CancellationToken.None)));

        Assert.Equal(40, plc.Requests.Count);
    }

    [Fact]
    public async Task NoPortMeansAPlainReason()
    {
        // Bound but not listening: the port refuses the connection and no other test can take it.
        using var taken = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        taken.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)taken.LocalEndPoint!).Port;
        await using var session = new ModbusSession("127.0.0.1", port, 1, replyTimeout: Generous);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => session.ConnectAsync(CancellationToken.None));

        Assert.StartsWith($"127.0.0.1:{port} cannot be reached", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFactoryReadsTheRecipient()
    {
        using var settings = JsonDocument.Parse("""{"host":"10.0.0.9","port":1502,"unitId":7}""");
        var request = new RecipientSessionRequest(settings.RootElement.Clone(), new Dictionary<string, IReadOnlyList<System.Security.Cryptography.X509Certificates.X509Certificate2>>(), NullLogger.Instance);

        Assert.IsType<ModbusSession>(new ModbusSessionFactory().Create(request));
    }
}

public sealed class WriteModbusActionTests
{
    private const string Pulse30 = """{"valueType":"pulse","address":7,"seconds":30}""";

    private sealed class RecordingWriter : IModbusWriter
    {
        private readonly ConcurrentQueue<string> _writes = new();

        public string Plc { get; init; } = "plc:502/1";

        /// <summary>Why the on write fails; null: it works.</summary>
        public string? OnFails { get; init; }

        /// <summary>Why the off write fails; null: it works.</summary>
        public string? OffFails { get; init; }

        public string[] Writes => [.. _writes];

        public bool OffOnACancelledToken { get; private set; }

        public Task WriteCoilAsync(ushort address, bool state, CancellationToken ct)
        {
            if ((state ? OnFails : OffFails) is { } reason)
            {
                throw new InvalidOperationException(reason);
            }

            OffOnACancelledToken |= !state && ct.IsCancellationRequested;
            _writes.Enqueue($"coil {address} {state}");
            return Task.CompletedTask;
        }

        public Task WriteRegistersAsync(ushort address, IReadOnlyList<ushort> values, CancellationToken ct)
        {
            _writes.Enqueue($"registers {address} {string.Join(",", values)}");
            return Task.CompletedTask;
        }
    }

    /// <summary>A pool as an action sees it: the session can change or be missing, and every request for it counts as use.</summary>
    private sealed class PoolContext(IModbusWriter? session, TimeProvider time) : IActionContext
    {
        private readonly ConcurrentQueue<DateTimeOffset> _asked = new();
        private volatile IModbusWriter? _session = session;

        public IModbusWriter? Session
        {
            get => _session;
            set => _session = value;
        }

        public IReadOnlyCollection<DateTimeOffset> Asked => _asked;

        public Oadm.Sdk.Vapix.IVapixClient? Vapix => null;

        public Microsoft.Extensions.Logging.ILogger Logger => NullLogger.Instance;

        public Oadm.Sdk.Plugins.ICorePlugin? Owner => null;

        public Oadm.Sdk.Plugins.IPluginSettings Settings { get; } = new FakeSettings();

        public void Log(string message)
        {
        }

        public string Text(string? text) => text ?? string.Empty;

        public Task<T> SessionAsync<T>(string fieldKey, CancellationToken ct)
            where T : class
        {
            _asked.Enqueue(time.GetUtcNow());
            return _session is T current ? Task.FromResult(current) : throw new InvalidOperationException(RecipientSessionTexts.NotConnected("The connection ended"));
        }
    }

    /// <summary>Moves a fake clock until the run ends; each step waits for what the last one started.</summary>
    private static Task RunOutAsync(FakeTimeProvider time, Task run, TimeSpan step) =>
        Eventually.TrueAsync(
            () =>
            {
                time.Advance(step);
                return run.IsCompleted;
            },
            "the run ends");

    private static JsonElement Settings(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static ActionInvocation Run(string json, ActionEdge edge = ActionEdge.Fired) =>
        new(Guid.Empty, "Rule", edge, Settings(json), null, new ActionTrigger(string.Empty, "Event", null, null, null, null, DateTimeOffset.UnixEpoch, new Dictionary<string, string>()));

    private static (FakeActionContext Context, RecordingWriter Writer) Context()
    {
        var writer = new RecordingWriter();
        var context = new FakeActionContext();
        context.Sessions["recipient"] = writer;
        return (context, writer);
    }

    [Fact]
    public async Task AnOnOrOffWritesOneCoil()
    {
        var (context, writer) = Context();

        await new WriteModbusAction().ExecuteAsync(context, Run("""{"valueType":"onoff","address":7,"state":"on"}"""), CancellationToken.None);
        await new WriteModbusAction().ExecuteAsync(context, Run("""{"valueType":"onoff","address":7,"state":"off"}"""), CancellationToken.None);

        Assert.Equal(["coil 7 True", "coil 7 False"], writer.Writes);
    }

    [Fact]
    public async Task APulseGoesOffAfterItsTime()
    {
        var (context, writer) = Context();
        var time = new FakeTimeProvider();
        var run = new WriteModbusAction(time).ExecuteAsync(context, Run("""{"valueType":"pulse","address":7,"seconds":3}"""), CancellationToken.None);
        Assert.Equal(["coil 7 True"], writer.Writes);

        time.Advance(TimeSpan.FromSeconds(3));
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(["coil 7 True", "coil 7 False"], writer.Writes);
    }

    [Fact]
    public async Task ACancelledPulseStillGoesOff()
    {
        var (context, writer) = Context();
        var time = new FakeTimeProvider();
        using var cancel = new CancellationTokenSource();
        var run = new WriteModbusAction(time).ExecuteAsync(context, Run(Pulse30), cancel.Token);

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(["coil 7 True", "coil 7 False"], writer.Writes);
        Assert.False(writer.OffOnACancelledToken);
    }

    [Fact]
    public async Task AnOnWriteThatFailedIsStillTurnedOff()
    {
        var time = new FakeTimeProvider();
        var writer = new RecordingWriter { OnFails = "The PLC did not answer. The write may have happened." };

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => new WriteModbusAction(time).ExecuteAsync(new PoolContext(writer, time), Run(Pulse30), CancellationToken.None));

        Assert.Equal("The PLC did not answer. The write may have happened.", refused.Message);
        Assert.Equal(["coil 7 False"], writer.Writes);
    }

    [Fact]
    public async Task APulseThatLostItsPlcSaysSoAndWritesNothingToTheNewOne()
    {
        var time = new FakeTimeProvider();
        var (first, second) = (new RecordingWriter(), new RecordingWriter { Plc = "other:502/1" });
        var context = new PoolContext(first, time);
        var run = new WriteModbusAction(time).ExecuteAsync(context, Run(Pulse30), CancellationToken.None);

        context.Session = second;
        await RunOutAsync(time, run, TimeSpan.FromSeconds(1));

        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => run);
        Assert.Contains("may still be on", failed.Message, StringComparison.Ordinal);
        Assert.Equal(["coil 7 True"], first.Writes);
        Assert.Empty(second.Writes);
    }

    [Fact]
    public async Task APulseGoesOffOnASessionThatReplacedTheFirst()
    {
        var time = new FakeTimeProvider();
        var (first, second) = (new RecordingWriter(), new RecordingWriter());
        var context = new PoolContext(first, time);
        var run = new WriteModbusAction(time).ExecuteAsync(context, Run(Pulse30), CancellationToken.None);

        context.Session = second;
        await RunOutAsync(time, run, TimeSpan.FromSeconds(1));

        await run;
        Assert.Equal(["coil 7 True"], first.Writes);
        Assert.Equal(["coil 7 False"], second.Writes);
    }

    [Fact]
    public async Task APulseWaitsForADroppedConnectionToComeBack()
    {
        var time = new FakeTimeProvider();
        var writer = new RecordingWriter();
        var context = new PoolContext(writer, time);
        var run = new WriteModbusAction(time).ExecuteAsync(context, Run(Pulse30), CancellationToken.None);
        context.Session = null;
        time.Advance(TimeSpan.FromSeconds(30));
        await Eventually.TrueAsync(() => context.Asked.Count >= 2, "the first try of the off write");

        context.Session = writer;
        await RunOutAsync(time, run, TimeSpan.FromSeconds(1));

        await run;
        Assert.Equal(["coil 7 True", "coil 7 False"], writer.Writes);
    }

    [Fact]
    public async Task AnOffWriteThatFailsSaysTheCoilMayStillBeOn()
    {
        var time = new FakeTimeProvider();
        var writer = new RecordingWriter { OffFails = "The PLC is busy." };
        var run = new WriteModbusAction(time).ExecuteAsync(new PoolContext(writer, time), Run(Pulse30), CancellationToken.None);

        await RunOutAsync(time, run, TimeSpan.FromSeconds(1));

        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => run);
        Assert.Equal("The PLC is busy. The coil may still be on.", failed.Message);
    }

    [Fact]
    public async Task ADropThatStaysSaysTheCoilMayStillBeOn()
    {
        var time = new FakeTimeProvider();
        var context = new PoolContext(new RecordingWriter(), time);
        var run = new WriteModbusAction(time).ExecuteAsync(context, Run(Pulse30), CancellationToken.None);

        context.Session = null;
        await RunOutAsync(time, run, TimeSpan.FromSeconds(1));

        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => run);
        Assert.Equal("Not connected: The connection ended. The coil may still be on.", failed.Message);
    }

    [Fact]
    public async Task APulseLongerThanTheIdleTimeKeepsItsSession()
    {
        var time = new FakeTimeProvider();
        var writer = new RecordingWriter();
        var context = new PoolContext(writer, time);
        var run = new WriteModbusAction(time).ExecuteAsync(context, Run("""{"valueType":"pulse","address":7,"seconds":900}"""), CancellationToken.None);

        await RunOutAsync(time, run, TimeSpan.FromSeconds(10));

        await run;
        var asked = context.Asked.ToArray();
        Assert.True(asked.Length >= 14, $"asked {asked.Length} times");
        Assert.All(asked.Zip(asked.Skip(1)), pair => Assert.True(pair.Second - pair.First < TimeSpan.FromMinutes(2), $"{pair.Second - pair.First} without use"));
        Assert.Equal(["coil 7 True", "coil 7 False"], writer.Writes);
    }

    [Fact]
    public void APulseMayTakeItsTimeAndTheConnection()
    {
        Assert.Equal(TimeSpan.FromSeconds(105), new WriteModbusAction().MaxRunTime(Settings("""{"valueType":"pulse","seconds":60}""")));
        Assert.Null(new WriteModbusAction().MaxRunTime(Settings("""{"valueType":"onoff"}""")));
    }

    [Fact]
    public void EachFieldShowsOnlyForItsValueType()
    {
        var fields = new WriteModbusAction().Fields;
        bool Shown(string key, string valueType) =>
            ActionSettings.IsVisible(fields.Single(f => f.Key == key), new System.Text.Json.Nodes.JsonObject { ["valueType"] = valueType, ["state"] = "on", ["numberFormat"] = ModbusValue.Signed32 }, statefulRule: false);

        Assert.True(Shown("seconds", ModbusValue.Pulse));
        Assert.False(Shown("seconds", ModbusValue.OnOff));
        Assert.False(Shown("seconds", ModbusValue.Number));
        Assert.True(Shown("wordOrder", ModbusValue.Number));
        Assert.False(Shown("wordOrder", ModbusValue.OnOff));
        Assert.False(Shown("wordOrder", ModbusValue.Text));
        Assert.False(Shown("state", ModbusValue.Pulse));
    }

    [Fact]
    public async Task AnUnknownPlaceholderWritesNothing()
    {
        var (context, writer) = Context();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => new WriteModbusAction().ExecuteAsync(
            context, Run("""{"valueType":"text","textLength":16,"address":1,"value":"Door {gone}"}"""), CancellationToken.None));

        Assert.Equal("The placeholder {gone} is unknown. Nothing was written.", refused.Message);
        Assert.Empty(writer.Writes);
    }

    [Fact]
    public async Task AValueWithBracesFromAPlaceholderIsWritten()
    {
        var (context, writer) = Context();
        context.Placeholders["name"] = "{A}";

        await new WriteModbusAction().ExecuteAsync(context, Run("""{"valueType":"text","textLength":4,"address":1,"value":"{name}"}"""), CancellationToken.None);

        Assert.Single(writer.Writes);
    }

    [Fact]
    public async Task AnEmptyTextFromAPlaceholderWritesNothing()
    {
        var (context, writer) = Context();
        context.Placeholders["site"] = string.Empty;

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => new WriteModbusAction().ExecuteAsync(
            context, Run("""{"valueType":"text","textLength":16,"address":1,"value":"{site}"}"""), CancellationToken.None));

        Assert.Equal("The value is empty. Nothing was written.", refused.Message);
        Assert.Empty(writer.Writes);
    }

    [Fact]
    public async Task AWritePastTheLastAddressWritesNothing()
    {
        var (context, writer) = Context();
        context.Placeholders["number"] = "1";

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => new WriteModbusAction().ExecuteAsync(
            context, Run("""{"valueType":"number","numberFormat":"int32","address":65535,"value":"{number}"}"""), CancellationToken.None));

        Assert.Equal("This write would go past address 65535. Nothing was written.", refused.Message);
        Assert.Empty(writer.Writes);
    }

    [Fact]
    public void AnAddressIsCheckedAtASave()
    {
        var action = new WriteModbusAction();

        Assert.Equal("This write would go past address 65535.", action.Validate(Settings("""{"valueType":"text","textLength":4,"address":65535,"value":"{a}"}"""), false)["address"]);
        Assert.Empty(action.Validate(Settings("""{"valueType":"text","textLength":4,"address":65534,"value":"{a}"}"""), false));
    }

    [Fact]
    public async Task APlaceholderIsFilledAndCheckedAtTheRun()
    {
        var (context, writer) = Context();
        context.Placeholders["number"] = "42";

        await new WriteModbusAction().ExecuteAsync(context, Run("""{"valueType":"number","numberFormat":"uint16","address":1,"value":"{number}"}"""), CancellationToken.None);

        Assert.Equal(["registers 1 42"], writer.Writes);
    }

    [Fact]
    public async Task AValueThatDoesNotFitWritesNothing()
    {
        var (context, writer) = Context();
        context.Placeholders["number"] = "70000";

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => new WriteModbusAction().ExecuteAsync(
            context, Run("""{"valueType":"number","numberFormat":"uint16","address":1,"value":"{number}"}"""), CancellationToken.None));

        Assert.Equal("Enter a whole number from 0 to 65535. Nothing was written.", refused.Message);
        Assert.Empty(writer.Writes);
    }

    [Fact]
    public void TheOffEdgeRunsOnlyWithAValue()
    {
        var action = new WriteModbusAction();

        Assert.False(action.RunsOn(ActionEdge.TurnedOff, Settings("""{"valueType":"onoff"}""")));
        Assert.True(action.RunsOn(ActionEdge.TurnedOff, Settings("""{"valueType":"onoff","offValue":"off"}""")));
        Assert.True(action.RunsOn(ActionEdge.TurnedOn, Settings("""{"valueType":"onoff"}""")));
    }

    [Fact]
    public async Task TheOffEdgeWritesItsOwnValue()
    {
        var (context, writer) = Context();

        await new WriteModbusAction().ExecuteAsync(context, Run("""{"valueType":"onoff","address":2,"state":"on","offValue":"off"}""", ActionEdge.TurnedOff), CancellationToken.None);

        Assert.Equal(["coil 2 False"], writer.Writes);
    }

    [Fact]
    public void TheValueIsCheckedAtASaveUnlessItIsAPlaceholder()
    {
        var action = new WriteModbusAction();

        Assert.Equal("Enter a whole number from 0 to 65535.", action.Validate(Settings("""{"valueType":"number","numberFormat":"uint16","value":"-5"}"""), false)["value"]);
        Assert.Empty(action.Validate(Settings("""{"valueType":"number","numberFormat":"uint16","value":"{number}"}"""), false));
        Assert.Equal("Enter on or off.", action.Validate(Settings("""{"valueType":"onoff","offValue":"maybe"}"""), true)["offValue"]);
        Assert.Empty(action.Validate(Settings("""{"valueType":"onoff","offValue":"maybe"}"""), false));
    }

    [Fact]
    public void TheRecipientIsPersistentAndItsFieldsAreChecked()
    {
        var type = Assert.Single(new WriteModbusAction().RecipientTypes);

        Assert.NotNull(type.Sessions);
        var errors = ActionSettings.Check(type.Fields, new System.Text.Json.Nodes.JsonObject { ["host"] = "999.1.1.1", ["port"] = 70000 }, statefulRule: true);
        Assert.Equal(ActionFormats.EnterHost, errors["host"]);
        Assert.Contains("port", errors.Keys);
    }
}
