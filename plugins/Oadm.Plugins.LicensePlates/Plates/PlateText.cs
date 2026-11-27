using System.Text;

namespace Oadm.Plugins.LicensePlates.Plates;

/// <summary>
/// The text of a plate as OADM keeps and compares it: upper case, without spaces, dashes and dots ("W-12 345.A" is
/// "W12345A"). A plate of the list holds only letters and digits and has 2 to 12 characters; a wildcard plate also holds
/// <c>*</c>, which stands for exactly one letter or digit ("AXIS12**"; decisions of 2026-11-27).
/// </summary>
public static class PlateText
{
    /// <summary>The wildcard of a plate: exactly one letter or digit.</summary>
    public const char Wildcard = '*';

    /// <summary>Whether a plate text (normalized) is a wildcard plate.</summary>
    public static bool IsWildcard(string? text) => text is not null && text.Contains(Wildcard, StringComparison.Ordinal);

    /// <summary>The letters and digits of a plate text besides *.</summary>
    public static int FixedCount(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var count = 0;
        foreach (var c in text)
        {
            if (c != Wildcard)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Whether a plate starts with a search (normalized): a wildcard plate by its fixed part, a * taking any letter or digit
    /// ("AXIS12**" is found by "AXIS" and by "AXIS1234").
    /// </summary>
    public static bool Starts(string plate, string start)
    {
        ArgumentNullException.ThrowIfNull(plate);
        ArgumentNullException.ThrowIfNull(start);
        if (start.Length > plate.Length)
        {
            return false;
        }

        for (var i = 0; i < start.Length; i++)
        {
            if (plate[i] != start[i] && (plate[i] != Wildcard || !char.IsLetterOrDigit(start[i])))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether a read text fits a wildcard plate: the same length, every fixed character the same, a letter or digit for each *.</summary>
    public static bool Fits(string pattern, string read)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(read);
        if (pattern.Length != read.Length)
        {
            return false;
        }

        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == Wildcard ? !char.IsLetterOrDigit(read[i]) : pattern[i] != read[i])
            {
                return false;
            }
        }

        return true;
    }

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
            if (!char.IsLetterOrDigit(c) && c != Wildcard)
            {
                return "Use only letters, digits and *.";
            }
        }

        if (IsWildcard(text) && FixedCount(text) < LicensePlateLimits.MinWildcardFixed)
        {
            return LicensePlateTexts.TooFewFixed;
        }

        return text.Length is < LicensePlateLimits.MinPlateLength or > LicensePlateLimits.MaxPlateLength
            ? $"A plate has {LicensePlateLimits.MinPlateLength} to {LicensePlateLimits.MaxPlateLength} characters."
            : null;
    }
}
