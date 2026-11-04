using System.Text;

namespace Oadm.Plugins.Ocr.Matching;

/// <summary>
/// A parsed format of an area's expected text (docs/spec/ocr.md "Expected text"): <c>#</c> a digit, <c>@</c> a letter,
/// <c>*</c> a letter or digit; every other character must appear as written (letters in any case), a dash with spaces around
/// it allowed; spaces may be missing in the text. A match is written in the format's form (its spaces, dashes and literals), with the
/// look-alike characters fixed by position as the last step: where a digit is expected O becomes 0, I and L 1, S 5, B 8,
/// Z 2, G 6; where a letter is expected the reverse.
/// </summary>
public sealed class TextFormat
{
    /// <summary>
    /// The area menu's "Use read text as format" (docs/spec/ocr.md "Page"): a read text turned into a format. Every digit
    /// becomes <c>#</c>, every letter (umlauts too) <c>@</c>, everything else stays as written; line breaks and runs of spaces
    /// become one space, the ends are trimmed. "MSKU 123456 7" gives "@@@@ ###### #", "4/12" "#/##".
    /// </summary>
    public static string FromText(string? text)
    {
        var format = new StringBuilder(text?.Length ?? 0);
        var space = false;
        foreach (var c in text ?? string.Empty)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                space = format.Length > 0;
                continue;
            }

            if (space)
            {
                format.Append(' ');
                space = false;
            }

            format.Append(char.IsDigit(c) ? '#' : char.IsLetter(c) ? '@' : c);
        }

        return format.ToString();
    }

    private readonly Token[] _tokens;

    private TextFormat(string pattern, Token[] tokens)
    {
        Pattern = pattern;
        _tokens = tokens;
        HasContainerCheckDigit = ContainerCheck.FormatHasCheckDigit(pattern);
    }

    /// <summary>The format as written.</summary>
    public string Pattern { get; }

    /// <summary>
    /// The format starts with a container number (<see cref="ContainerCheck.FormatHasCheckDigit"/>): its 11th letter or digit
    /// is the ISO 6346 check digit of the 10 before it.
    /// </summary>
    public bool HasContainerCheckDigit { get; }

    /// <summary>
    /// True when <paramref name="value"/> is one whole match of the format (a value a reading or a vote made), with its check
    /// digit where the format has one.
    /// </summary>
    public bool IsWholeMatch(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var matches = FindAll(value);
        return matches.Count == 1 && matches[0].Start == 0 && matches[0].Length == value.Length;
    }

    /// <summary>
    /// The parsed <paramref name="pattern"/>. Leading and trailing spaces and dashes are ignored.
    /// </summary>
    /// <exception cref="ArgumentException">The format has no character to match.</exception>
    public static TextFormat Parse(string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        var tokens = new List<Token>();
        foreach (var c in pattern.Trim(' ', '-'))
        {
            var kind = c switch
            {
                '#' => TokenKind.Digit,
                '@' => TokenKind.Letter,
                '*' => TokenKind.LetterOrDigit,
                ' ' => TokenKind.Space,
                '-' => TokenKind.Dash,
                _ => TokenKind.Literal,
            };
            if (kind is TokenKind.Space or TokenKind.Dash && tokens.Count > 0 && tokens[^1].Kind is TokenKind.Space or TokenKind.Dash)
            {
                // "@ - #": one separator; a dash wins over a space.
                if (kind == TokenKind.Dash)
                {
                    tokens[^1] = new Token(TokenKind.Dash, '-');
                }

                continue;
            }

            tokens.Add(new Token(kind, c));
        }

        if (tokens.Count == 0)
        {
            throw new ArgumentException("The format has no character to match.", nameof(pattern));
        }

        return new TextFormat(pattern, [.. tokens]);
    }

    /// <summary>
    /// Every match in <paramref name="text"/>, left to right, each as (start, length in <paramref name="text"/>, the value in
    /// the format's form). A match never starts or ends inside a run of letters and digits (a format that starts or ends with
    /// a sign such as "(" may touch them); matches do not overlap. A wrong ISO 6346 check digit is no match.
    /// </summary>
    public IReadOnlyList<FormatMatch> FindAll(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var found = new List<FormatMatch>();
        var start = 0;
        while (start < text.Length)
        {
            if ((start > 0 && char.IsLetterOrDigit(text[start - 1]) && char.IsLetterOrDigit(text[start])) || text[start] is ' ' or '-')
            {
                start++;
                continue;
            }

            var end = LongestEnd(text, start);
            if (end > start)
            {
                var value = Render(text, start);
                if (!HasContainerCheckDigit || ContainerCheck.IsValid(value))
                {
                    found.Add(new FormatMatch(start, end - start, value));
                    start = end;
                    continue;
                }
            }

            start++;
        }

        return found;
    }

    /// <summary>The end of the longest match starting at <paramref name="start"/>, or -1.</summary>
    private int LongestEnd(string text, int start)
    {
        var best = -1;
        Walk(text, start, 0, ref best);
        return best;
    }

    private void Walk(string text, int pos, int token, ref int best)
    {
        if (token == _tokens.Length)
        {
            if ((pos == text.Length || !char.IsLetterOrDigit(text[pos]) || (pos > 0 && !char.IsLetterOrDigit(text[pos - 1]))) && pos > best)
            {
                best = pos;
            }

            return;
        }

        var t = _tokens[token];
        switch (t.Kind)
        {
            case TokenKind.Space:
                // Zero or more spaces.
                for (var p = pos; ; p++)
                {
                    Walk(text, p, token + 1, ref best);
                    if (p >= text.Length || text[p] != ' ')
                    {
                        break;
                    }
                }

                break;
            case TokenKind.Dash:
                // A dash, spaces around it allowed.
                var after = SkipSpaces(text, pos);
                if (after < text.Length && text[after] == '-')
                {
                    Walk(text, SkipSpaces(text, after + 1), token + 1, ref best);
                }

                break;
            default:
                if (pos < text.Length && Accepts(t, text[pos]))
                {
                    Walk(text, pos + 1, token + 1, ref best);
                }

                break;
        }
    }

    private static int SkipSpaces(string text, int pos)
    {
        while (pos < text.Length && text[pos] == ' ')
        {
            pos++;
        }

        return pos;
    }

    private static bool Accepts(Token token, char c) => token.Kind switch
    {
        TokenKind.Digit => char.IsAsciiDigit(c) || LookAlike.AsDigit(c) is not null,
        TokenKind.Letter => char.IsLetter(c) || LookAlike.AsLetter(c) is not null,
        TokenKind.LetterOrDigit => char.IsLetterOrDigit(c),
        _ => char.ToUpperInvariant(c) == char.ToUpperInvariant(token.Char),
    };

    /// <summary>The value of the match at <paramref name="start"/> in the format's form, look-alikes fixed.</summary>
    private string Render(string text, int start)
    {
        var value = new StringBuilder(_tokens.Length);
        var pos = start;
        foreach (var t in _tokens)
        {
            switch (t.Kind)
            {
                case TokenKind.Space:
                    pos = SkipSpaces(text, pos);
                    value.Append(' ');
                    break;
                case TokenKind.Dash:
                    pos = SkipSpaces(text, SkipSpaces(text, pos) + 1);
                    value.Append('-');
                    break;
                case TokenKind.Digit:
                    value.Append(char.IsAsciiDigit(text[pos]) ? text[pos] : LookAlike.AsDigit(text[pos])!.Value);
                    pos++;
                    break;
                case TokenKind.Letter:
                    value.Append(char.IsLetter(text[pos]) ? text[pos] : LookAlike.AsLetter(text[pos])!.Value);
                    pos++;
                    break;
                case TokenKind.LetterOrDigit:
                    value.Append(text[pos]);
                    pos++;
                    break;
                default:
                    value.Append(t.Char);
                    pos++;
                    break;
            }
        }

        return value.ToString();
    }

    private enum TokenKind
    {
        Digit,
        Letter,
        LetterOrDigit,
        Space,
        Dash,
        Literal,
    }

    private readonly record struct Token(TokenKind Kind, char Char);
}

/// <summary>A match of a <see cref="TextFormat"/>.</summary>
/// <param name="Start">Where it starts in the searched text.</param>
/// <param name="Length">Its length in the searched text.</param>
/// <param name="Value">The value in the format's form, look-alikes fixed.</param>
public readonly record struct FormatMatch(int Start, int Length, string Value);

/// <summary>The look-alike characters fixed by position (docs/spec/ocr.md "Expected text").</summary>
public static class LookAlike
{
    /// <summary>The digit <paramref name="c"/> looks like where a digit is expected: O 0, I and L 1, S 5, B 8, Z 2, G 6; else null.</summary>
    public static char? AsDigit(char c) => char.ToUpperInvariant(c) switch
    {
        'O' => '0',
        'I' or 'L' => '1',
        'S' => '5',
        'B' => '8',
        'Z' => '2',
        'G' => '6',
        _ => null,
    };

    /// <summary>The letter <paramref name="c"/> looks like where a letter is expected: 0 O, 1 I, 5 S, 8 B, 2 Z, 6 G; else null.</summary>
    public static char? AsLetter(char c) => c switch
    {
        '0' => 'O',
        '1' => 'I',
        '5' => 'S',
        '8' => 'B',
        '2' => 'Z',
        '6' => 'G',
        _ => null,
    };
}

/// <summary>
/// The ISO 6346 check digit of container numbers (docs/spec/ocr.md "Accuracy"), the one rule of the matcher and the
/// recognition's format-guided decoding: a format whose first 11 letters and digits are <c>@@@U</c> and seven <c>#</c> is a
/// container number ("@@@U ###### #", also "@@@U ###### # 22G1" with the size and type code after it), and the 11th letter
/// or digit of its value is the check digit of the 10 before it. A value whose check digit does not fit is no match; no
/// character is ever changed to make it fit.
/// </summary>
public static class ContainerCheck
{
    /// <summary>The owner code, category and serial number: 4 letters and 6 digits.</summary>
    private const int CodeLength = 10;

    /// <summary>True when the first 11 letters and digits of <paramref name="format"/> are <c>@@@U</c> and seven <c>#</c>.</summary>
    public static bool FormatHasCheckDigit(string? format)
    {
        if (format is null)
        {
            return false;
        }

        Span<char> first = stackalloc char[CodeLength + 1];
        var count = 0;
        foreach (var c in format)
        {
            if (c is ' ' or '-')
            {
                continue;
            }

            first[count++] = c;
            if (count == first.Length)
            {
                return ((ReadOnlySpan<char>)first).StartsWith("@@@U", StringComparison.Ordinal) && first[4..].IndexOfAnyExcept('#') < 0;
            }
        }

        return false;
    }

    /// <summary>
    /// True when the first 11 letters and digits of <paramref name="value"/> are 4 letters, 6 digits and a check digit that fits
    /// them (spaces, dashes and other signs between them are left out; what follows the 11th, such as a type code, is not
    /// checked).
    /// </summary>
    public static bool IsValid(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Span<char> chars = stackalloc char[CodeLength + 1];
        var count = 0;
        foreach (var c in value)
        {
            if (!char.IsLetterOrDigit(c))
            {
                continue;
            }

            chars[count++] = char.ToUpperInvariant(c);
            if (count == chars.Length)
            {
                var check = CheckDigit(chars[..CodeLength]);
                return check >= 0 && chars[CodeLength] == (char)('0' + check);
            }
        }

        return false;
    }

    /// <summary>
    /// The check digit (0 to 9) of the owner code, category and serial number (10 characters: 4 letters, 6 digits; letters in
    /// either case), or -1 when they are not.
    /// </summary>
    public static int CheckDigit(ReadOnlySpan<char> first10)
    {
        if (first10.Length != CodeLength)
        {
            return -1;
        }

        var sum = 0;
        for (var i = 0; i < CodeLength; i++)
        {
            var c = first10[i];
            int value;
            if (i < 4)
            {
                if (!char.IsAsciiLetter(c))
                {
                    return -1;
                }

                value = LetterValue(char.ToUpperInvariant(c));
            }
            else
            {
                if (!char.IsAsciiDigit(c))
                {
                    return -1;
                }

                value = c - '0';
            }

            sum += value << i;
        }

        return sum % 11 % 10;
    }

    /// <summary>Letters from A = 10 up, skipping 11, 22 and 33.</summary>
    private static int LetterValue(char letter)
    {
        var value = 10 + (letter - 'A');
        value += value >= 11 ? 1 : 0;
        value += value >= 22 ? 1 : 0;
        value += value >= 33 ? 1 : 0;
        return value;
    }
}
