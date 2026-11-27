using System.Text;

namespace Oadm.Plugins.LicensePlates.Plates;

/// <summary>
/// The text of a plate as OADM keeps and compares it: upper case, without spaces, dashes and dots ("W-12 345.A" is
/// "W12345A"). A plate of the list holds only letters and digits and has 2 to 12 characters.
/// </summary>
public static class PlateText
{
    /// <summary>The text without spaces, dashes and dots, in upper case; other characters stay (and make it invalid).</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || c is '-' or '.' or '–' or '—' or '·')
            {
                continue;
            }

            sb.Append(char.ToUpperInvariant(c));
        }

        return sb.ToString();
    }

    /// <summary>
    /// The text a camera read, for matching: <see cref="Normalize"/> and only letters and digits (ALPV may send other
    /// separators).
    /// </summary>
    public static string OfRead(string? text)
    {
        var normalized = Normalize(text);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>The error of a plate text (normalized), null when it is valid.</summary>
    public static string? Problem(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return "Enter the plate.";
        }

        foreach (var c in text)
        {
            if (!char.IsLetterOrDigit(c))
            {
                return "Use only letters and digits.";
            }
        }

        return text.Length is < LicensePlateLimits.MinPlateLength or > LicensePlateLimits.MaxPlateLength
            ? $"A plate has {LicensePlateLimits.MinPlateLength} to {LicensePlateLimits.MaxPlateLength} characters."
            : null;
    }
}
