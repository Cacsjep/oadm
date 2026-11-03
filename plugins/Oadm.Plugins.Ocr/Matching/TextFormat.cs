using System.Text;

namespace Oadm.Plugins.Ocr.Matching;

/// <summary>
/// A parsed format of an area's expected text (docs/spec/ocr.md "Expected text"): <c>#</c> a digit, <c>A</c> a letter,
/// <c>*</c> a letter or digit; every other character must appear as written (letters in any case), except spaces and dashes,
/// which may be missing in the text. A match is written in the format's form (its spaces, dashes and literals), with the
/// look-alike characters fixed by position as the last step: where a digit is expected O becomes 0, I and L 1, S 5, B 8,
/// Z 2, G 6; where a letter is expected the reverse.
/// </summary>
public sealed class TextFormat
{
    private readonly Token[] _tokens;

    private TextFormat(string pattern, Token[] tokens)
    {
        Pattern = pattern;
        _tokens = tokens;
        HasContainerCheckDigit = pattern.TrimStart().StartsWith("AAAU", StringComparison.Ordinal);
    }

    /// <summary>The format as written.</summary>
    public string Pattern { get; }

    /// <summary>
    /// The page's "Use as format" (docs/spec/ocr.md "Page"): a read text turned into a format. Every digit becomes <c>#</c>,
    /// every letter (umlauts too) <c>A</c>, everything else stays as written; line breaks and runs of spaces become one
    /// space, the ends are trimmed. "MSKU 123456 7" gives "AAAA ###### #", "4/12" "#/##", "DECAF-FEINATO" "AAAAA-AAAAAAA".
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

            format.Append(char.IsDigit(c) ? '#' : char.IsLetter(c) ? 'A' : c);
        }

        return format.ToString();
    }

    /// <summary>The format starts with <c>AAAU</c>: a container number whose last digit is its ISO 6346 check digit.</summary>
    public bool HasContainerCheckDigit { get; }

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
                'A' => TokenKind.Letter,
                '*' => TokenKind.LetterOrDigit,
                ' ' => TokenKind.Space,
                '-' => TokenKind.Dash,
                _ => TokenKind.Literal,
            };
            if (kind is TokenKind.Space or TokenKind.Dash && tokens.Count > 0 && tokens[^1].Kind is TokenKind.Space or TokenKind.Dash)
            {
                // "A - #": one separator; a dash wins over a space.
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
    /// the format's form). A match never starts or ends inside a run of letters and digits; matches do not overlap. A wrong
    /// ISO 6346 check digit is no match.
    /// </summary>
    public IReadOnlyList<FormatMatch> FindAll(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var found = new List<FormatMatch>();
        var start = 0;
        while (start < text.Length)
        {
            if ((start > 0 && char.IsLetterOrDigit(text[start - 1])) || text[start] is ' ' or '-')
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
            if ((pos == text.Length || !char.IsLetterOrDigit(text[pos])) && pos > best)
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
                // Spaces, an optional dash, spaces.
                var after = SkipSpaces(text, pos);
                Walk(text, after, token + 1, ref best);
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
                    pos = SkipSpaces(text, pos);
                    if (pos < text.Length && text[pos] == '-')
                    {
                        pos = SkipSpaces(text, pos + 1);
                    }

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

/// <summary>The ISO 6346 check digit of container numbers (docs/spec/ocr.md "Accuracy").</summary>
public static class ContainerCheck
{
    /// <summary>
    /// True when the letters and digits of <paramref name="value"/> are 4 letters, 6 digits and a check digit that fits them.
    /// </summary>
    public static bool IsValid(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var chars = value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray();
        if (chars.Length != 11 || !chars[..4].All(char.IsAsciiLetterUpper) || !chars[4..].All(char.IsAsciiDigit))
        {
            return false;
        }

        return CheckDigit(new string(chars, 0, 10)) == chars[10] - '0';
    }

    /// <summary>The check digit of the owner code, category and serial number (10 characters: 4 letters, 6 digits).</summary>
    public static int CheckDigit(string first10)
    {
        ArgumentNullException.ThrowIfNull(first10);
        if (first10.Length != 10)
        {
            throw new ArgumentException("A container number has 10 characters before its check digit.", nameof(first10));
        }

        var sum = 0;
        for (var i = 0; i < 10; i++)
        {
            sum += CharValue(first10[i]) << i;
        }

        return sum % 11 % 10;
    }

    /// <summary>Digits their value; letters from A = 10 up, skipping 11, 22 and 33.</summary>
    private static int CharValue(char c)
    {
        if (char.IsAsciiDigit(c))
        {
            return c - '0';
        }

        var value = 10 + (char.ToUpperInvariant(c) - 'A');
        value += value >= 11 ? 1 : 0;
        value += value >= 22 ? 1 : 0;
        value += value >= 33 ? 1 : 0;
        return value;
    }
}
