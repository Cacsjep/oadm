using System.Text;

namespace Oadm.Plugins.Ocr.Matching;

/// <summary>
/// The event field Number (docs/spec/ocr.md "Events and the rule action"): a value that is one number, written the same way
/// everywhere. The digits are kept as they are, never through a floating point number, so a long run of digits (a counter, a
/// serial number) loses none.
/// </summary>
public static class NumberText
{
    /// <summary>
    /// <paramref name="value"/> as a number in invariant form when it is one: digits with at most one "." or "," as the decimal
    /// sign and an optional sign; spaces around are ignored. Leading zeros and trailing decimal zeros go ("0042,50" gives
    /// "42.5", "-0" gives "0"). Empty otherwise (text, several numbers, thousands separators).
    /// </summary>
    public static string Of(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var negative = text[0] == '-';
        var start = text[0] is '-' or '+' ? 1 : 0;
        var separator = text.AsSpan(start).IndexOfAny('.', ',');
        var whole = separator < 0 ? text.AsSpan(start) : text.AsSpan(start, separator);
        var fraction = separator < 0 ? [] : text.AsSpan(start + separator + 1);
        if (whole.Length == 0 || !IsDigits(whole) || (separator >= 0 && (fraction.Length == 0 || !IsDigits(fraction))))
        {
            return string.Empty;
        }

        whole = whole.TrimStart('0');
        fraction = fraction.TrimEnd('0');
        var number = new StringBuilder(whole.Length + fraction.Length + 3);
        if (negative && (whole.Length > 0 || fraction.Length > 0))
        {
            number.Append('-');
        }

        number.Append(whole.Length == 0 ? "0" : whole);
        if (fraction.Length > 0)
        {
            number.Append('.').Append(fraction);
        }

        return number.ToString();
    }

    private static bool IsDigits(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
