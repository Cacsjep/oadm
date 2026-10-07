using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Oadm.Sdk.Client.Controls;

/// <summary>Language of a <see cref="CodeView"/>; <see cref="Auto"/> detects it from the content type and the text.</summary>
public enum CodeLanguage
{
    Auto,
    Json,
    Xml,
    KeyValue,
    Plain,
}

/// <summary>Kind of a highlighted piece of code; each kind has a theme brush <c>Oadm.Code.&lt;Kind&gt;Brush</c>.</summary>
public enum CodeTokenKind
{
    Text,
    Key,
#pragma warning disable CA1720 // The kind names the theme brush (Oadm.Code.StringBrush).
    String,
#pragma warning restore CA1720
    Number,
    Literal,
    Punctuation,
    Tag,
    Attribute,
    Comment,
    Error,
}

/// <summary>A highlighted range of <see cref="CodeDocument.Text"/>.</summary>
public readonly record struct CodeToken(int Start, int Length, CodeTokenKind Kind);

/// <summary>What a <see cref="CodeView"/> shows: the (pretty-printed) text, the language used and its tokens (empty = plain text).</summary>
public sealed record CodeDocument(string Text, CodeLanguage Language, IReadOnlyList<CodeToken> Tokens)
{
    public static readonly CodeDocument Empty = new(string.Empty, CodeLanguage.Plain, []);

    public bool IsHighlighted => Tokens.Count > 0;
}

/// <summary>
/// Detection, pretty-printing and tokenizing behind <see cref="CodeView"/> (no UI types, testable on its own).
/// JSON and XML are re-indented with 2 spaces; text that does not parse is shown as it is, without highlighting.
/// Texts longer than the highlight limit are shown plain (still pretty-printed up to <see cref="MaxFormatChars"/>).
/// </summary>
public static class CodeText
{
    /// <summary>Default highlight limit: above it the text is shown without colors (keeps the view fast).</summary>
    public const int DefaultHighlightLimit = 512 * 1024;

    /// <summary>Above this size JSON and XML are not re-indented either.</summary>
    public const int MaxFormatChars = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonDocumentOptions JsonRead = new() { MaxDepth = 256 };

    /// <summary>Language from the content type ("application/json", "text/xml", "application/soap+xml"), else from the text itself.</summary>
    public static CodeLanguage Detect(string? text, string? contentType = null)
    {
        var type = contentType?.ToLowerInvariant() ?? string.Empty;
        if (type.Contains("json", StringComparison.Ordinal))
        {
            return CodeLanguage.Json;
        }

        if (type.Contains("html", StringComparison.Ordinal))
        {
            return CodeLanguage.Plain;
        }

        if (type.Contains("xml", StringComparison.Ordinal) || type.Contains("soap", StringComparison.Ordinal))
        {
            return CodeLanguage.Xml;
        }

        if (string.IsNullOrEmpty(text))
        {
            return CodeLanguage.Plain;
        }

        var start = 0;
        while (start < text.Length && (char.IsWhiteSpace(text[start]) || text[start] == '﻿'))
        {
            start++;
        }

        if (start == text.Length)
        {
            return CodeLanguage.Plain;
        }

        var first = text[start];
        if (first is '{' or '[')
        {
            return CodeLanguage.Json;
        }

        if (first == '<')
        {
            var head = text.AsSpan(start, Math.Min(64, text.Length - start));
            return head.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
                ? CodeLanguage.Plain
                : CodeLanguage.Xml;
        }

        return LooksLikeKeyValue(text) ? CodeLanguage.KeyValue : CodeLanguage.Plain;
    }

    /// <summary>
    /// Prepares <paramref name="text"/> for display. <paramref name="format"/> false keeps the text exactly as it is
    /// (still highlighted when it parses). Never throws for any input.
    /// </summary>
    public static CodeDocument Prepare(string? text, CodeLanguage language = CodeLanguage.Auto, string? contentType = null, bool format = true, int highlightLimit = DefaultHighlightLimit)
    {
        if (string.IsNullOrEmpty(text))
        {
            return CodeDocument.Empty;
        }

        if (language == CodeLanguage.Auto)
        {
            language = Detect(text, contentType);
        }

        string shown;
        switch (language)
        {
            case CodeLanguage.Json:
            case CodeLanguage.Xml:
                var pretty = text.Length > MaxFormatChars ? null : language == CodeLanguage.Json ? FormatJson(text) : FormatXml(text);
                if (pretty is null)
                {
                    // Does not parse (or is too large to check): exactly the device text, no colors.
                    return new CodeDocument(text, CodeLanguage.Plain, []);
                }

                shown = format ? pretty : text;
                break;
            case CodeLanguage.KeyValue:
                shown = text;
                break;
            default:
                return new CodeDocument(text, CodeLanguage.Plain, []);
        }

        if (shown.Length > highlightLimit)
        {
            return new CodeDocument(shown, language, []);
        }

        var tokens = Tokenize(shown, language);
        return new CodeDocument(shown, language, tokens);
    }

    /// <summary>JSON indented with 2 spaces and LF line ends (non-ASCII kept readable), or null when it does not parse.</summary>
    public static string? FormatJson(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            using var json = JsonDocument.Parse(text.TrimStart('﻿'), JsonRead);
            return JsonSerializer.Serialize(json.RootElement, IndentedJson);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>XML (also SOAP) indented with 2 spaces and LF line ends, declaration kept, or null when it does not parse. DTDs are refused.</summary>
    public static string? FormatXml(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = true };
            using var reader = XmlReader.Create(new StringReader(text.Trim().TrimStart('﻿')), settings);
            var xml = XDocument.Load(reader);
            var body = xml.ToString(SaveOptions.None).ReplaceLineEndings("\n");
            return xml.Declaration is null ? body : xml.Declaration + "\n" + body;
        }
        catch (XmlException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Tokens of <paramref name="text"/>; adjacent tokens of the same kind separated only by white space are merged.</summary>
    public static IReadOnlyList<CodeToken> Tokenize(string text, CodeLanguage language)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = new TokenList(text);
        switch (language)
        {
            case CodeLanguage.Json:
                TokenizeJson(text, tokens);
                break;
            case CodeLanguage.Xml:
                TokenizeXml(text, tokens);
                break;
            case CodeLanguage.KeyValue:
                TokenizeKeyValue(text, tokens);
                break;
            default:
                break;
        }

        return tokens.Items;
    }

    /// <summary>param.cgi style: every non-empty line is "key=value" or a "#" line, and at least one is "key=value" or "# Error".</summary>
    private static bool LooksLikeKeyValue(string text)
    {
        var pairs = 0;
        var lines = 0;
        foreach (var range in text.AsSpan().EnumerateLines())
        {
            var line = range.Trim();
            if (line.IsEmpty)
            {
                continue;
            }

            if (++lines > 200)
            {
                break;
            }

            if (line[0] == '#')
            {
                if (IsErrorLine(line))
                {
                    pairs++;
                }

                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0 || line[..eq].IndexOfAny(" \t<{") >= 0)
            {
                return false;
            }

            pairs++;
        }

        return pairs > 0;
    }

    private static void TokenizeJson(string text, TokenList tokens)
    {
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '"')
            {
                var end = i + 1;
                while (end < text.Length && text[end] != '"')
                {
                    end += text[end] == '\\' ? 2 : 1;
                }

                end = Math.Min(end + 1, text.Length);
                var next = end;
                while (next < text.Length && char.IsWhiteSpace(text[next]))
                {
                    next++;
                }

                tokens.Add(i, end - i, next < text.Length && text[next] == ':' ? CodeTokenKind.Key : CodeTokenKind.String);
                i = end;
            }
            else if (c is '-' or (>= '0' and <= '9'))
            {
                var end = i + 1;
                while (end < text.Length && text[end] is (>= '0' and <= '9') or '.' or 'e' or 'E' or '+' or '-')
                {
                    end++;
                }

                tokens.Add(i, end - i, CodeTokenKind.Number);
                i = end;
            }
            else if (char.IsAsciiLetter(c))
            {
                var end = i + 1;
                while (end < text.Length && char.IsAsciiLetter(text[end]))
                {
                    end++;
                }

                tokens.Add(i, end - i, CodeTokenKind.Literal);
                i = end;
            }
            else
            {
                tokens.Add(i, 1, c is '{' or '}' or '[' or ']' or ':' or ',' ? CodeTokenKind.Punctuation : CodeTokenKind.Text);
                i++;
            }
        }
    }

    private static void TokenizeXml(string text, TokenList tokens)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] != '<')
            {
                // Character data between tags stays in the normal text color.
                var next = text.IndexOf('<', i);
                i = next < 0 ? text.Length : next;
                continue;
            }

            if (string.CompareOrdinal(text, i, "<!--", 0, 4) == 0)
            {
                i = Until(text, i, "-->", tokens, CodeTokenKind.Comment);
            }
            else if (string.CompareOrdinal(text, i, "<![CDATA[", 0, 9) == 0)
            {
                tokens.Add(i, 9, CodeTokenKind.Punctuation);
                var close = text.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                var contentEnd = close < 0 ? text.Length : close;
                tokens.Add(i + 9, contentEnd - i - 9, CodeTokenKind.String);
                if (close >= 0)
                {
                    tokens.Add(close, 3, CodeTokenKind.Punctuation);
                }

                i = close < 0 ? text.Length : close + 3;
            }
            else if (string.CompareOrdinal(text, i, "<!", 0, 2) == 0)
            {
                i = Until(text, i, ">", tokens, CodeTokenKind.Comment);
            }
            else
            {
                i = TokenizeTag(text, i, tokens);
            }
        }
    }

    /// <summary>"&lt;name attr="v"&gt;", "&lt;/name&gt;", "&lt;?xml ...?&gt;"; returns the index after the tag.</summary>
    private static int TokenizeTag(string text, int i, TokenList tokens)
    {
        var open = text.Length > i + 1 && text[i + 1] is '/' or '?' ? 2 : 1;
        tokens.Add(i, open, CodeTokenKind.Punctuation);
        i += open;
        var nameEnd = i;
        while (nameEnd < text.Length && IsNameChar(text[nameEnd]))
        {
            nameEnd++;
        }

        tokens.Add(i, nameEnd - i, CodeTokenKind.Tag);
        i = nameEnd;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '>')
            {
                tokens.Add(i, 1, CodeTokenKind.Punctuation);
                return i + 1;
            }

            if (c is '/' or '?' && i + 1 < text.Length && text[i + 1] == '>')
            {
                tokens.Add(i, 2, CodeTokenKind.Punctuation);
                return i + 2;
            }

            if (c is '"' or '\'')
            {
                var close = text.IndexOf(c, i + 1);
                var end = close < 0 ? text.Length : close + 1;
                tokens.Add(i, end - i, CodeTokenKind.String);
                i = end;
            }
            else if (c == '=')
            {
                tokens.Add(i, 1, CodeTokenKind.Punctuation);
                i++;
            }
            else if (IsNameChar(c))
            {
                var end = i + 1;
                while (end < text.Length && IsNameChar(text[end]))
                {
                    end++;
                }

                tokens.Add(i, end - i, CodeTokenKind.Attribute);
                i = end;
            }
            else if (c == '<')
            {
                return i; // broken tag: the next one starts here
            }
            else
            {
                i++;
            }
        }

        return i;
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is ':' or '_' or '-' or '.';

    private static int Until(string text, int start, string terminator, TokenList tokens, CodeTokenKind kind)
    {
        var close = text.IndexOf(terminator, start + 1, StringComparison.Ordinal);
        var end = close < 0 ? text.Length : close + terminator.Length;
        tokens.Add(start, end - start, kind);
        return end;
    }

    private static void TokenizeKeyValue(string text, TokenList tokens)
    {
        var lineStart = 0;
        while (lineStart < text.Length)
        {
            var lineEnd = text.IndexOf('\n', lineStart);
            if (lineEnd < 0)
            {
                lineEnd = text.Length;
            }

            var contentEnd = lineEnd > lineStart && text[lineEnd - 1] == '\r' ? lineEnd - 1 : lineEnd;
            var first = lineStart;
            while (first < contentEnd && char.IsWhiteSpace(text[first]))
            {
                first++;
            }

            if (first < contentEnd)
            {
                if (text[first] == '#')
                {
                    var line = text.AsSpan(first, contentEnd - first);
                    tokens.Add(first, line.Length, IsErrorLine(line) ? CodeTokenKind.Error : CodeTokenKind.Comment);
                }
                else
                {
                    var eq = text.IndexOf('=', first, contentEnd - first);
                    if (eq > first)
                    {
                        tokens.Add(first, eq - first, CodeTokenKind.Key);
                        tokens.Add(eq, 1, CodeTokenKind.Punctuation);
                        var value = text.AsSpan(eq + 1, contentEnd - eq - 1);
                        if (!value.IsEmpty)
                        {
                            tokens.Add(eq + 1, value.Length, ValueKind(value));
                        }
                    }
                }
            }

            lineStart = lineEnd + 1;
        }
    }

    private static bool IsErrorLine(ReadOnlySpan<char> line) => line.TrimStart('#').TrimStart().StartsWith("Error", StringComparison.OrdinalIgnoreCase);

    private static CodeTokenKind ValueKind(ReadOnlySpan<char> value)
    {
        if (value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value.Equals("no", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return CodeTokenKind.Literal;
        }

        return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)
            ? CodeTokenKind.Number
            : CodeTokenKind.String;
    }

    /// <summary>Collects tokens in order and merges same-kind neighbours separated only by white space (fewer runs to render).</summary>
    private sealed class TokenList(string text)
    {
        public List<CodeToken> Items { get; } = [];

        public void Add(int start, int length, CodeTokenKind kind)
        {
            if (length <= 0 || kind == CodeTokenKind.Text)
            {
                return;
            }

            if (Items.Count > 0)
            {
                var last = Items[^1];
                var lastEnd = last.Start + last.Length;
                if (last.Kind == kind && OnlyWhiteSpace(lastEnd, start))
                {
                    Items[^1] = last with { Length = start + length - last.Start };
                    return;
                }
            }

            Items.Add(new CodeToken(start, length, kind));
        }

        private bool OnlyWhiteSpace(int from, int to)
        {
            for (var i = from; i < to; i++)
            {
                if (!char.IsWhiteSpace(text[i]))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
