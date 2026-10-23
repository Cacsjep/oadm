using System.Globalization;
using System.Text;

namespace Oadm.Plugins.Radius.Protocol.Text;

/// <summary>
/// Text attributes as they may reach the UI or a log (docs/spec/radius-server.md "Parsing and RFC compliance"): decoded as
/// UTF-8 (RFC 8044 3.4), invalid bytes become replacement characters, control and format characters (including bidi
/// overrides that could disguise a log line) are removed.
/// </summary>
public static class RadiusText
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>The sanitized text of <paramref name="value"/>; empty for no bytes.</summary>
    public static string Decode(ReadOnlySpan<byte> value) => value.IsEmpty ? string.Empty : Sanitize(Utf8.GetString(value));

    /// <summary><paramref name="text"/> without control (Cc) and format (Cf) characters.</summary>
    public static string Sanitize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var clean = true;
        foreach (var c in text)
        {
            if (IsRemoved(c))
            {
                clean = false;
                break;
            }
        }

        if (clean)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!IsRemoved(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static bool IsRemoved(char c) =>
        char.GetUnicodeCategory(c) is UnicodeCategory.Control or UnicodeCategory.Format;
}
