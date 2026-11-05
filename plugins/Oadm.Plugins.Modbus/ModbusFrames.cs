using System.Buffers.Binary;
using System.Globalization;

namespace Oadm.Plugins.Modbus;

/// <summary>The frames of Modbus TCP: the header (MBAP) and the three write functions. No read function exists here, on purpose.</summary>
public static class ModbusFrames
{
    public const byte WriteCoilFunction = 5;

    public const byte WriteRegisterFunction = 6;

    public const byte WriteRegistersFunction = 16;

    /// <summary>Transaction id, protocol id, length, unit id.</summary>
    public const int HeaderLength = 7;

    public const int MaxPduLength = 253;

    /// <summary>The most registers one request of function 16 may hold.</summary>
    public const int MaxRegisters = 123;

    private const byte ErrorFlag = 0x80;

    /// <summary>The reply to every write: function, address, value or count.</summary>
    private const int EchoLength = 5;

    private const string UnexpectedAnswer = "The PLC gave an unexpected answer. The write may have happened.";

    public static byte[] WriteCoil(ushort address, bool on)
    {
        var pdu = new byte[5];
        pdu[0] = WriteCoilFunction;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), on ? (ushort)0xFF00 : (ushort)0x0000);
        return pdu;
    }

    public static byte[] WriteRegister(ushort address, ushort value)
    {
        var pdu = new byte[5];
        pdu[0] = WriteRegisterFunction;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), value);
        return pdu;
    }

    public static byte[] WriteRegisters(ushort address, IReadOnlyList<ushort> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfZero(values.Count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(values.Count, MaxRegisters);
        var pdu = new byte[6 + (values.Count * 2)];
        pdu[0] = WriteRegistersFunction;
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1), address);
        BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3), (ushort)values.Count);
        pdu[5] = (byte)(values.Count * 2);
        for (var i = 0; i < values.Count; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(6 + (i * 2)), values[i]);
        }

        return pdu;
    }

    /// <summary>A request on the wire.</summary>
    public static byte[] Request(ushort transactionId, byte unitId, byte[] pdu)
    {
        ArgumentNullException.ThrowIfNull(pdu);
        var frame = new byte[HeaderLength + pdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(frame, transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(pdu.Length + 1));
        frame[6] = unitId;
        pdu.CopyTo(frame, HeaderLength);
        return frame;
    }

    /// <summary>Throws with a plain reason unless <paramref name="reply"/>, from unit <paramref name="repliedUnit"/>, confirms <paramref name="request"/> to <paramref name="unitId"/>.</summary>
    public static void CheckReply(byte[] request, byte[] reply, byte unitId, byte repliedUnit)
    {
        if (repliedUnit != unitId)
        {
            throw new InvalidOperationException(UnexpectedAnswer);
        }

        CheckReply(request, reply);
    }

    /// <summary>Throws with a plain reason unless <paramref name="reply"/> confirms <paramref name="request"/>.</summary>
    public static void CheckReply(byte[] request, byte[] reply)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reply);
        if (reply.Length >= 2 && reply[0] == (request[0] | ErrorFlag))
        {
            throw new InvalidOperationException(ExceptionText(reply[1]));
        }

        // Every write function echoes the function, the address and the value or count.
        if (reply.Length != EchoLength || !reply.AsSpan().SequenceEqual(request.AsSpan(0, EchoLength)))
        {
            throw new InvalidOperationException(UnexpectedAnswer);
        }
    }

    private static string ExceptionText(byte code) => code switch
    {
        1 => "The PLC does not support this write.",
        2 => "The PLC has no such address. Addresses count from 0.",
        3 => "The PLC refused the value.",
        4 => "The PLC failed to write.",
        5 => "The PLC accepted the write and is still working on it.",
        6 => "The PLC is busy.",
        10 or 11 => "The gateway cannot reach the PLC. Check the unit ID.",
        _ => string.Create(CultureInfo.InvariantCulture, $"The PLC refused the write (code {code})."),
    };
}
