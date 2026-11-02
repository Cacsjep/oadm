using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Oadm.Plugins.Firmware;

/// <summary>One row of a listing page of www.axis.com/ftp: a folder or a file.</summary>
/// <param name="Name">The name without the trailing "/" of a folder ("12_11_147", "P3265-V_12_11_147.bin").</param>
/// <param name="Path">The absolute path of the link ("/ftp/pub_soft/MPQT/P3265-V/12_11_147/").</param>
/// <param name="IsFolder">The row is a folder.</param>
/// <param name="Size">The size in bytes as the page shows it ("225.5M" in units of 1,024), null when it shows none.</param>
/// <param name="Sha256">The SHA-256 in lower case hex, null when the page shows none.</param>
public sealed record OnlineListingEntry(string Name, string Path, bool IsFolder, long? Size, string? Sha256);

/// <summary>
/// Reads the listing pages of www.axis.com/ftp/pub_soft (README "Firmware online"): an HTML table with one row per entry and
/// the cells icon, name (a link), modified, size and sha256. The parent row is left out. Never throws on odd HTML: rows it
/// cannot read are skipped.
/// </summary>
public static partial class OnlineListing
{
    /// <summary>The rows of a listing page.</summary>
    public static IReadOnlyList<OnlineListingEntry> Parse(string? html)
    {
        var entries = new List<OnlineListingEntry>();
        if (string.IsNullOrEmpty(html))
        {
            return entries;
        }

        foreach (Match row in Row().Matches(html))
        {
            var cells = row.Groups[1].Value;
            if (cells.Contains("[PARENTDIR]", StringComparison.Ordinal) || Link().Match(cells) is not { Success: true } link)
            {
                continue;
            }

            var href = WebUtility.HtmlDecode(link.Groups[1].Value).Trim();
            var text = WebUtility.HtmlDecode(link.Groups[2].Value).Trim();
            if (href is "../" or "" || text.Length == 0)
            {
                continue;
            }

            var isFolder = text.EndsWith('/') || cells.Contains("[DIR]", StringComparison.Ordinal);
            var name = text.TrimEnd('/');
            if (name.Length == 0 || name == "..")
            {
                continue;
            }

            var sha = Cell(cells, "sha256");
            entries.Add(new OnlineListingEntry(
                name,
                href,
                isFolder,
                isFolder ? null : ParseSize(Cell(cells, "size")),
                sha is not null && Sha256Text().IsMatch(sha) ? sha.ToLowerInvariant() : null));
        }

        return entries;
    }

    /// <summary>"225.5M" = 225.5 x 1,048,576 bytes (the listing counts in units of 1,024; "3.1M" is a file of 3,222,833 bytes).</summary>
    public static long? ParseSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = SizeText().Match(text.Trim());
        if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        var factor = match.Groups[2].Value.ToUpperInvariant() switch
        {
            "K" => 1024d,
            "M" => 1024d * 1024,
            "G" => 1024d * 1024 * 1024,
            _ => 1d,
        };
        return (long)Math.Round(value * factor);
    }

    /// <summary>A version folder: two to five numbers ("12_11_147", "10_12_356_2"); folders with a suffix (special builds) are not.</summary>
    public static AxisOsVersion? VersionOfFolder(string name) =>
        VersionFolder().IsMatch(name) ? AxisOsVersion.TryParse(name) : null;

    /// <summary>The folder name of a version ("12.11.147" -> "12_11_147").</summary>
    public static string FolderOf(AxisOsVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version.Text.Replace('.', '_');
    }

    /// <summary>The version in <c>latest/ver.txt</c>: only a bare version ("12.11.147"); anything else (a "Page not found" page) is none.</summary>
    public static AxisOsVersion? ParseVerTxt(string? text)
    {
        var trimmed = text?.Trim();
        return trimmed is not null && VerTxt().IsMatch(trimmed) ? AxisOsVersion.TryParse(trimmed) : null;
    }

    private static string? Cell(string cells, string cssClass)
    {
        foreach (Match cell in TdWithClass().Matches(cells))
        {
            if (string.Equals(cell.Groups[1].Value, cssClass, StringComparison.Ordinal))
            {
                var text = WebUtility.HtmlDecode(cell.Groups[2].Value).Trim();
                return text.Length == 0 || text == "-" ? null : text;
            }
        }

        return null;
    }

    [GeneratedRegex("<tr>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Row();

    [GeneratedRegex("""<a href="([^"]*)">([^<]*)</a>""", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 500)]
    private static partial Regex Link();

    [GeneratedRegex("""<td class="([a-z0-9]+)">([^<]*)</td>""", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 500)]
    private static partial Regex TdWithClass();

    [GeneratedRegex(@"^([0-9]+(?:\.[0-9]+)?)\s*([KMG]?)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex SizeText();

    [GeneratedRegex("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Text();

    [GeneratedRegex(@"^\d{1,4}(?:_\d{1,6}){1,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionFolder();

    [GeneratedRegex(@"^\d{1,4}(?:\.\d{1,6}){1,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex VerTxt();
}
