using System.Buffers.Binary;
using System.Globalization;

namespace Oadm.Plugins.Modbus;

/// <summary>What a value becomes on the PLC.</summary>
/// <param name="Coil">The state of a coil; null for registers.</param>
/// <param name="Registers">The registers; empty for a coil.</param>
public sealed record ModbusWrite(bool? Coil, IReadOnlyList<ushort> Registers);

/// <summary>Turns a value into a write, before anything is sent. A value that does not fit the type is refused.</summary>
public static class ModbusValue
{
    public const string OnOff = "onoff";
    public const string Number = "number";
    public const string Text = "text";

    public const string Signed16 = "int16";
    public const string Unsigned16 = "uint16";
    public const string Signed32 = "int32";
    public const string Unsigned32 = "uint32";
    public const string Decimal32 = "float32";

    public const string HighWordFirst = "high";
    public const string LowWordFirst = "low";

    public const string FirstCharacterHigh = "high";
    public const string FirstCharacterLow = "low";

    /// <summary>Two characters per register, the most one request holds.</summary>
    public const int MaxTextLength = ModbusFrames.MaxRegisters * 2;

    /// <summary>The write of <paramref name="value"/>; throws <see cref="FormatException"/> with a plain reason.</summary>
    public static ModbusWrite Encode(string valueType, string value, string numberFormat, string wordOrder, int textLength, string byteOrder)
    {
        ArgumentNullException.ThrowIfNull(value);
        return valueType switch
        {
            OnOff => new ModbusWrite(OnOffValue(value), []),
            Number => new ModbusWrite(null, NumberRegisters(value.Trim(), numberFormat, wordOrder)),
            Text => new ModbusWrite(null, TextRegisters(value, textLength, byteOrder)),
            _ => throw new FormatException("Choose a value type."),
        };
    }

    private static bool OnOffValue(string value) => value.Trim().ToLowerInvariant() switch
    {
        "on" or "true" or "1" => true,
        "off" or "false" or "0" => false,
        _ => throw new FormatException("Enter on or off."),
    };

    private static ushort[] NumberRegisters(string value, string format, string wordOrder)
    {
        const NumberStyles Whole = NumberStyles.AllowLeadingSign;
        var culture = CultureInfo.InvariantCulture;
        switch (format)
        {
            case Signed16 when short.TryParse(value, Whole, culture, out var number):
                return [unchecked((ushort)number)];
            case Unsigned16 when ushort.TryParse(value, Whole, culture, out var number):
                return [number];
            case Signed32 when int.TryParse(value, Whole, culture, out var number):
                return Words(unchecked((uint)number), wordOrder);
            case Unsigned32 when uint.TryParse(value, Whole, culture, out var number):
                return Words(number, wordOrder);
            case Decimal32 when float.TryParse(value, NumberStyles.Float, culture, out var number) && float.IsFinite(number):
                return Words(BitConverter.SingleToUInt32Bits(number), wordOrder);
            case Signed16: throw new FormatException("Enter a whole number from -32768 to 32767.");
            case Unsigned16: throw new FormatException("Enter a whole number from 0 to 65535.");
            case Signed32: throw new FormatException("Enter a whole number from -2147483648 to 2147483647.");
            case Unsigned32: throw new FormatException("Enter a whole number from 0 to 4294967295.");
            case Decimal32: throw new FormatException("Enter a number.");
            default: throw new FormatException("Choose a number format.");
        }
    }

    private static ushort[] Words(uint value, string wordOrder)
    {
        var high = (ushort)(value >> 16);
        var low = (ushort)value;
        return wordOrder == LowWordFirst ? [low, high] : [high, low];
    }

    private static ushort[] TextRegisters(string value, int length, string byteOrder)
    {
        if (length < 1 || length > MaxTextLength)
        {
            throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"Enter a length from 1 to {MaxTextLength}."));
        }

        if (value.Length > length)
        {
            throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"At most {length} characters."));
        }

        if (!value.All(char.IsAscii))
        {
            throw new FormatException("Use only plain letters, digits and signs.");
        }

        var registers = new ushort[(length + 1) / 2];
        Span<byte> pair = stackalloc byte[2];
        for (var i = 0; i < registers.Length; i++)
        {
            pair[0] = (byte)(i * 2 < value.Length ? value[i * 2] : 0);
            pair[1] = (byte)((i * 2) + 1 < value.Length ? value[(i * 2) + 1] : 0);
            registers[i] = byteOrder == FirstCharacterLow ? BinaryPrimitives.ReadUInt16LittleEndian(pair) : BinaryPrimitives.ReadUInt16BigEndian(pair);
        }

        return registers;
    }
}
