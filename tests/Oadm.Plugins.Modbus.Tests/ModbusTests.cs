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

    /// <summary>The reply to a frame; null closes the connection. Default: the echo a write gets.</summary>
    public Func<byte[], byte[]?> Answer { get; set; } = Echo;

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
    private static async Task<ModbusSession> OpenAsync(FakePlc plc)
    {
        var session = new ModbusSession("127.0.0.1", plc.Port, 3);
        await session.ConnectAsync(CancellationToken.None);
        return session;
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
        var dropped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Dropped += (_, e) => dropped.TrySetResult(e.Reason);
        using var cancel = new CancellationTokenSource();

        var write = session.WriteCoilAsync(4, true, cancel.Token);
        await Eventually.TrueAsync(() => !plc.Requests.IsEmpty, "the request");
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);

        plc.CloseConnection(); // in the middle of the frame
        Assert.Equal("The connection ended", await dropped.Task.WaitAsync(TimeSpan.FromSeconds(30)));
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
        using var unused = new TcpListener(IPAddress.Loopback, 0);
        unused.Start();
        var port = ((IPEndPoint)unused.LocalEndpoint).Port;
        unused.Stop();
        await using var session = new ModbusSession("127.0.0.1", port, 1);

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
    private sealed class RecordingWriter : IModbusWriter
    {
        public List<string> Writes { get; } = [];

        public Task WriteCoilAsync(ushort address, bool state, CancellationToken ct)
        {
            Writes.Add($"coil {address} {state}");
            return Task.CompletedTask;
        }

        public Task WriteRegistersAsync(ushort address, IReadOnlyList<ushort> values, CancellationToken ct)
        {
            Writes.Add($"registers {address} {string.Join(",", values)}");
            return Task.CompletedTask;
        }
    }

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
        var run = new WriteModbusAction(time).ExecuteAsync(context, Run("""{"valueType":"onoff","address":7,"state":"pulse","seconds":3}"""), CancellationToken.None);
        Assert.Equal(["coil 7 True"], writer.Writes);

        time.Advance(TimeSpan.FromSeconds(3));
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(["coil 7 True", "coil 7 False"], writer.Writes);
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
