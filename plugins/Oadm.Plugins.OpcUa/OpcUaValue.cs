using System.Globalization;

using Opc.Ua;

namespace Oadm.Plugins.OpcUa;

/// <summary>Turns a value into what a node gets, before anything is sent. A value that does not fit the type is refused.</summary>
public static class OpcUaValue
{
    public const string OnOff = "onoff";
    public const string Number = "number";
    public const string Text = "text";

    public const string Signed16 = "int16";
    public const string Unsigned16 = "uint16";
    public const string Signed32 = "int32";
    public const string Unsigned32 = "uint32";
    public const string Signed64 = "int64";
    public const string Decimal32 = "float";
    public const string Decimal64 = "double";

    private const string TooSmall = "This number is too small for its format.";

    /// <summary>The value to write; throws <see cref="FormatException"/> with a plain reason.</summary>
    public static Variant Encode(string valueType, string value, string numberFormat)
    {
        ArgumentNullException.ThrowIfNull(value);
        return valueType switch
        {
            OnOff => new Variant(OnOffValue(value)),
            Number => NumberValue(value.Trim(), numberFormat),
            Text => new Variant(value),
            _ => throw new FormatException("Choose a value type."),
        };
    }

    /// <summary>A number that is not zero and became zero: writing 0 is not what the rule asks for.</summary>
    private static bool IsLost(double number, string value)
    {
        var exponent = value.AsSpan().IndexOfAny('e', 'E');
        return number == 0 && (exponent < 0 ? value.AsSpan() : value.AsSpan(0, exponent)).IndexOfAnyInRange('1', '9') >= 0;
    }

    private static bool OnOffValue(string value) => value.Trim().ToLowerInvariant() switch
    {
        "on" or "true" or "1" => true,
        "off" or "false" or "0" => false,
        _ => throw new FormatException("Enter on or off."),
    };

    private static Variant NumberValue(string value, string format)
    {
        const NumberStyles Whole = NumberStyles.AllowLeadingSign;
        var culture = CultureInfo.InvariantCulture;
        switch (format)
        {
            case Signed16 when short.TryParse(value, Whole, culture, out var number):
                return new Variant(number);
            case Unsigned16 when ushort.TryParse(value, Whole, culture, out var number):
                return new Variant(number);
            case Signed32 when int.TryParse(value, Whole, culture, out var number):
                return new Variant(number);
            case Unsigned32 when uint.TryParse(value, Whole, culture, out var number):
                return new Variant(number);
            case Signed64 when long.TryParse(value, Whole, culture, out var number):
                return new Variant(number);
            case Decimal32 when float.TryParse(value, NumberStyles.Float, culture, out var number) && float.IsFinite(number):
                return !IsLost(number, value) ? new Variant(number) : throw new FormatException(TooSmall);
            case Decimal64 when double.TryParse(value, NumberStyles.Float, culture, out var number) && double.IsFinite(number):
                return !IsLost(number, value) ? new Variant(number) : throw new FormatException(TooSmall);
            case Signed16: throw new FormatException("Enter a whole number from -32768 to 32767.");
            case Unsigned16: throw new FormatException("Enter a whole number from 0 to 65535.");
            case Signed32: throw new FormatException("Enter a whole number from -2147483648 to 2147483647.");
            case Unsigned32: throw new FormatException("Enter a whole number from 0 to 4294967295.");
            case Signed64: throw new FormatException("Enter a whole number from -9223372036854775808 to 9223372036854775807.");
            case Decimal32 or Decimal64: throw new FormatException("Enter a number.");
            default: throw new FormatException("Choose a number format.");
        }
    }
}
