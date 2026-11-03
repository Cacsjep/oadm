namespace Oadm.Plugins.Ocr.Recognition;

/// <summary>
/// The check digit of a container number (ISO 6346): owner code (3 letters), category (U, J or Z), 6 digits and the check
/// digit. Letters count 10 to 38 without the multiples of 11; position i is weighted 2^i; the sum modulo 11, then modulo 10.
/// </summary>
public static class Iso6346
{
    /// <summary>True when <paramref name="text"/> holds exactly 11 letters and digits (spaces and dashes ignored) and its last one is the right check digit.</summary>
    public static bool IsValid(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Span<char> chars = stackalloc char[11];
        var count = 0;
        foreach (var ch in text)
        {
            if (ch is ' ' or '-')
            {
                continue;
            }

            if (count == 11)
            {
                return false;
            }

            chars[count++] = ch;
        }

        if (count != 11)
        {
            return false;
        }

        var check = CheckDigit(chars[..10]);
        return check >= 0 && chars[10] == (char)('0' + check);
    }

    /// <summary>The check digit (0 to 9) of the first 10 characters, or -1 when they are not 4 capital letters and 6 digits.</summary>
    public static int CheckDigit(ReadOnlySpan<char> first10)
    {
        if (first10.Length != 10)
        {
            return -1;
        }

        var sum = 0;
        for (var i = 0; i < 10; i++)
        {
            var ch = first10[i];
            int value;
            if (i < 4)
            {
                if (ch is < 'A' or > 'Z')
                {
                    return -1;
                }

                value = LetterValue(ch);
            }
            else
            {
                if (ch is < '0' or > '9')
                {
                    return -1;
                }

                value = ch - '0';
            }

            sum += value << i;
        }

        return sum % 11 % 10;
    }

    private static int LetterValue(char letter)
    {
        // A = 10, then up by one, skipping 11, 22 and 33.
        var value = 10;
        for (var c = 'A'; c < letter; c++)
        {
            value++;
            if (value % 11 == 0)
            {
                value++;
            }
        }

        return value;
    }
}
