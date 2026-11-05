using System.Buffers.Binary;
using System.Net.Sockets;

using Oadm.Plugins.Modbus;

namespace Oadm.RecipientStack.Tests;

/// <summary>
/// The real servers of tests/RecipientStack: each protocol connects and writes once. Only through "manage test recipients", which
/// starts them; HardwareWrite keeps these out of every other run.
/// </summary>
[Trait("Category", "HardwareWrite")]
[Trait("Category", "RecipientStack")]
public sealed class RecipientStackTests
{
    private const string Host = "127.0.0.1";
    private const int ModbusPort = 15020;

    /// <summary>A server in a container takes a moment after its port opens.</summary>
    private static async Task<T> RetryAsync<T>(Func<CancellationToken, Task<T>> open)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            try
            {
                return await open(limit.Token);
            }
            catch (InvalidOperationException) when (!limit.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), limit.Token);
            }
        }
    }

    [Fact]
    public async Task ModbusConnectsAndWrites()
    {
        var session = await RetryAsync(async ct =>
        {
            var opened = new ModbusSession(Host, ModbusPort, 1);
            await opened.ConnectAsync(ct);
            return opened;
        });
        await using (session)
        {
            await session.WriteCoilAsync(10, true, CancellationToken.None);
            await session.WriteRegistersAsync(20, [1234], CancellationToken.None);
            await session.WriteRegistersAsync(30, [0x1234, 0x5678], CancellationToken.None);
        }

        // OADM has no read function: the check reads with its own frames.
        Assert.Equal([1234], await ReadRegistersAsync(20, 1));
        Assert.Equal([0x1234, 0x5678], await ReadRegistersAsync(30, 2));
    }

    [Fact]
    public async Task ModbusSaysWhenTheAddressDoesNotExist()
    {
        await using var session = new ModbusSession(Host, ModbusPort, 1);
        await session.ConnectAsync(CancellationToken.None);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => session.WriteRegistersAsync(65535, [1, 2], CancellationToken.None));

        Assert.Contains("PLC", refused.Message, StringComparison.Ordinal);
    }

    private static async Task<ushort[]> ReadRegistersAsync(ushort address, ushort count)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(Host, ModbusPort);
        var stream = client.GetStream();
        var pdu = new byte[5];
        pdu[0] = 3;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), count);
        await stream.WriteAsync(ModbusFrames.Request(1, 1, pdu));
        var reply = new byte[9 + (count * 2)];
        await stream.ReadExactlyAsync(reply);
        return [.. Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(9 + (i * 2))))];
    }
}
