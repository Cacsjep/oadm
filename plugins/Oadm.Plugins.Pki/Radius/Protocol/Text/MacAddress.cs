namespace Oadm.Plugins.Radius.Protocol.Text;

/// <summary>
/// The MAC address of a <c>Calling-Station-Id</c> (RFC 3580 3.21 recommends <c>AC-CC-8E-12-34-56</c>; switches differ).
/// Accepted (docs/spec/radius-server.md "Parsing and RFC compliance"): separators <c>-</c>, <c>:</c>, <c>.</c> or none,
/// upper or lower case, groups of 2 (<c>ac:cc:8e:12:34:56</c>), 4 (Cisco <c>accc.8e12.3456</c>) or 6 (HPE
/// <c>accc8e-123456</c>), surrounding spaces or quotes, a suffix after the 12 digits (<c>…:SSID</c> of wireless) ignored.
/// Anything else is shown as sent and never matched to a device.
/// </summary>
public readonly record struct MacAddress
{
    private static readonly System.Buffers.SearchValues<char> AddressCharacters = System.Buffers.SearchValues.Create("0123456789abcdefABCDEF:-.");

    private MacAddress(string? normalized, string display)
    {
        Normalized = normalized;
        Display = display;
    }

    /// <summary><c>AC:CC:8E:12:34:56</c>, or null when the value is not a MAC address.</summary>
    public string? Normalized { get; }

    /// <summary>What the page shows: the normalized form, else the sanitized value as sent (empty when none was sent).</summary>
    public string Display { get; }

    /// <summary>True when <see cref="Normalized"/> holds a MAC address.</summary>
    public bool IsValid => Normalized is not null;

    /// <summary>The 12 hex digits in upper case (an Axis serial number is its MAC address), or null.</summary>
    public string? Serial => Normalized?.Replace(":", string.Empty, StringComparison.Ordinal);

    /// <summary>No Calling-Station-Id.</summary>
    public static MacAddress None { get; } = new(null, string.Empty);

    /// <summary>Reads a Calling-Station-Id value (already decoded text).</summary>
    public static MacAddress Parse(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return None;
        }

        var display = RadiusText.Sanitize(value).Trim();
        var text = display.Trim('"', '\'', ' ', '\t');
        var digits = TryDigits(text);
        return digits is null ? new MacAddress(null, display) : new MacAddress(Format(digits), Format(digits));
    }

    private static string? TryDigits(string text)
    {
        Span<char> digits = stackalloc char[12];
        var count = 0;
        var position = 0;
        char? separator = null;
        var groupLength = -1;
        var currentGroup = 0;
        while (position < text.Length && count < 12)
        {
            var c = text[position];
            if (char.IsAsciiHexDigit(c))
            {
                digits[count++] = char.ToUpperInvariant(c);
                currentGroup++;
                position++;
                continue;
            }

            if (c is not ('-' or ':' or '.'))
            {
                return null;
            }

            // A separator: the same one everywhere, between groups of one size (2, 4 or 6).
            if (currentGroup == 0 || (separator is { } s && s != c))
            {
                return null;
            }

            if (groupLength < 0)
            {
                groupLength = currentGroup;
                if (groupLength is not (2 or 4 or 6))
                {
                    return null;
                }
            }
            else if (currentGroup != groupLength)
            {
                return null;
            }

            separator = c;
            currentGroup = 0;
            position++;
        }

        if (count < 12 || (groupLength > 0 && currentGroup != groupLength))
        {
            return null;
        }

        // After the 12 digits: the end, or a suffix after ":" (RFC 3580 3.20 "MAC:SSID"). More address digits (an EUI-64
        // "…:56:78") are not a suffix.
        if (position < text.Length)
        {
            if (text[position] != ':')
            {
                return null;
            }

            var rest = text.AsSpan(position + 1);
            if (!rest.IsEmpty && char.IsAsciiHexDigit(rest[0]) && !rest.ContainsAnyExcept(AddressCharacters))
            {
                return null;
            }
        }

        return new string(digits);
    }

    private static string Format(string digits) => string.Create(17, digits, static (span, d) =>
    {
        for (var i = 0; i < 6; i++)
        {
            span[i * 3] = d[i * 2];
            span[(i * 3) + 1] = d[(i * 2) + 1];
            if (i < 5)
            {
                span[(i * 3) + 2] = ':';
            }
        }
    });
}
