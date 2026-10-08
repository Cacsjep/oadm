using System.Globalization;
using System.Text;

namespace Oadm.Plugins.HardeningScan.Client;

/// <summary>
/// The CSV export of the shown rows (UTF-8 with BOM, RFC 4180 quoting, CRLF): the device columns, then per check of the shown
/// level two columns: the result (<c>pass</c>, <c>warn</c>, <c>fail</c>, <c>n/a</c>, <c>error</c>, <c>info</c>, empty = not
/// scanned) and the value found.
/// </summary>
public static class CsvExport
{
    /// <summary>Byte order mark: spreadsheet programs then read the file as UTF-8.</summary>
    public const char Bom = '﻿';

    public static string Build(IReadOnlyList<LevelColumn> columns, IEnumerable<HardeningRow> rows)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        var text = new StringBuilder();
        text.Append(Bom);
        var header = new List<string> { "Address", "MAC address", "Model", "AXIS OS", "Last scan (UTC)", "Score", "Status" };
        foreach (var column in columns)
        {
            header.Add(column.Check.Title);
            header.Add(column.Check.Title + " (found)");
        }

        Line(text, header);
        foreach (var row in rows)
        {
            var cells = new List<string>
            {
                row.Address,
                row.Serial,
                row.Model,
                row.Firmware,
                row.ScannedUtc?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty,
                row.ScoreText,
                row.Status ?? (row.Kind == RowKind.NotScanned ? "Not scanned" : string.Empty),
            };
            for (var i = 0; i < columns.Count; i++)
            {
                cells.Add(CheckStateCodes.ToText(row.StateAt(i)));
                cells.Add(i < row.Values.Length ? row.Values[i] ?? string.Empty : string.Empty);
            }

            Line(text, cells);
        }

        return text.ToString();
    }

    /// <summary>RFC 4180: a field with a comma, quote or line break is quoted, quotes doubled.</summary>
    public static string Quote(string field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : field;
    }

    private static void Line(StringBuilder text, IEnumerable<string> cells)
    {
        text.AppendJoin(',', cells.Select(Quote));
        text.Append("\r\n");
    }
}
