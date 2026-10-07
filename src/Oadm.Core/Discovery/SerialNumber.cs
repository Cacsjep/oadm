using System.Text.RegularExpressions;

namespace Oadm.Core.Discovery;

/// <summary>
/// Helpers for Axis serial numbers. An Axis serial is the primary MAC address: 12 hex digits,
/// stored upper case without separators.
/// </summary>
public static partial class SerialNumber
{
    /// <summary>
    /// Normalizes "b8:a4:4f:12:34:56", "B8-A4-4F-12-34-56" or "b8a44f123456" to "B8A44F123456".
    /// Returns null when the input is not exactly 12 hex digits after removing ':', '-' and '.'.
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        Span<char> buffer = stackalloc char[12];
        var n = 0;
        foreach (var c in value.Trim())
        {
            if (c is ':' or '-' or '.')
            {
                continue;
            }

            if (!char.IsAsciiHexDigit(c) || n == 12)
            {
                return null;
            }

            buffer[n++] = char.ToUpperInvariant(c);
        }

        return n == 12 ? new string(buffer) : null;
    }

    /// <summary>
    /// Finds a trailing 12-digit hex token in a name such as "AXIS P3265-V - B8A44F123456" or
    /// "axis-b8a44f123456". Returns the normalized serial or null.
    /// </summary>
    public static string? FromName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var match = TrailingHex().Match(name);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    [GeneratedRegex("(?:^|[^0-9A-Fa-f])([0-9A-Fa-f]{12})$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingHex();
}
