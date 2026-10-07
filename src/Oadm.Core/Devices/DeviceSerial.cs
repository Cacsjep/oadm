using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Oadm.Core.Devices;

/// <summary>Device serial (= MAC) normalization: upper hex, no separators, 12 digits.</summary>
public static class DeviceSerial
{
    /// <summary>Normalizes "ac:cc:8e-12.34 56" to "ACCC8E123456". Throws when not a 12 digit hex MAC.</summary>
    public static string Normalize(string serial)
    {
        if (!TryNormalize(serial, out var normalized))
        {
            throw new ArgumentException($"'{serial}' is not a valid device serial (12 hex digits).", nameof(serial));
        }

        return normalized;
    }

    public static bool TryNormalize(string? serial, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(serial))
        {
            return false;
        }

        var sb = new StringBuilder(12);
        foreach (var c in serial)
        {
            if (c is ':' or '-' or '.' or ' ')
            {
                continue;
            }

            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }

            sb.Append(char.ToUpper(c, CultureInfo.InvariantCulture));
        }

        if (sb.Length != 12)
        {
            return false;
        }

        normalized = sb.ToString();
        return true;
    }
}
