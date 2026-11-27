using System.Globalization;
using System.Text;

using Oadm.Client.Infrastructure;
using Oadm.Contracts.Text;

namespace Oadm.Plugins.LicensePlates.Plates;

/// <summary>One row of an import file as read, before it is compared with the list.</summary>
/// <param name="Line">The file's line (1-based).</param>
/// <param name="Plate">The plate as read (tags empty: <paramref name="TagNames"/>); null when the row cannot be read.</param>
/// <param name="TagNames">The tag names of the row (OADM's columns).</param>
/// <param name="HasTimes">The row has OADM's time columns (an ALPV row keeps a listed plate's times).</param>
/// <param name="Error">Why the row is refused; null when it was read.</param>
internal sealed record CsvPlateRow(int Line, Plate? Plate, IReadOnlyList<string> TagNames, bool HasTimes, string? Error, string RawPlate);

/// <summary>
/// The CSV of the plate list (docs/spec/license-plate-center.md "Import and export"): OADM's columns Plate, Name, Tags
/// (names, comma separated), Allowed from, Allowed to (yyyy-MM-dd HH:mm, server time), Delete when it ends (Yes or No);
/// and ALPV's export <c>plate,date,description</c> (the description becomes the name, the date is ignored). UTF-8 with BOM,
/// formula-safe cells (<see cref="CsvWriter"/>).
/// </summary>
internal static class PlateCsv
{
    public static readonly IReadOnlyList<string> Columns = ["Plate", "Name", "Tags", "Allowed from", "Allowed to", "Delete when it ends"];

    private const string TimeFormat = "yyyy-MM-dd HH:mm";

    public static byte[] Export(IEnumerable<Plate> plates, Func<IEnumerable<Guid>, IReadOnlyList<string>> tagNames)
    {
        ArgumentNullException.ThrowIfNull(plates);
        ArgumentNullException.ThrowIfNull(tagNames);
        var sb = new StringBuilder();
        CsvWriter.AppendRecord(sb, Columns);
        foreach (var plate in plates)
        {
            CsvWriter.AppendRecord(sb,
            [
                plate.Text,
                plate.Name,
                string.Join(", ", tagNames(plate.Tags)),
                Time(plate.AllowedFrom),
                Time(plate.AllowedTo),
                plate.DeleteWhenEnds ? "Yes" : "No",
            ]);
        }

        return CsvWriter.ToBytes(sb.ToString());
    }

    private static string Time(DateTime? time) => time?.ToString(TimeFormat, CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>Reads a file: its format and every data row.</summary>
    public static (ImportFormat Format, IReadOnlyList<CsvPlateRow> Rows) Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var firstLine = text.AsSpan().TrimStart('﻿');
        var end = firstLine.IndexOfAny('\r', '\n');
        var head = (end < 0 ? firstLine : firstLine[..end]).ToString();
        var separator = head.Contains(';', StringComparison.Ordinal) && !head.Contains(',', StringComparison.Ordinal) ? ';' : ',';
        var records = Csv.Read(text, separator).ToList();
        if (records.Count == 0)
        {
            return (ImportFormat.Oadm, []);
        }

        var first = records[0].Cells.Select(c => Csv.Unguard(c).Trim().ToLowerInvariant()).ToList();
        var hasHeader = first.Count > 0 && first[0] is "plate" or "plates" or "license plate" or "licence plate" or "number";
        ImportFormat format;
        if (hasHeader)
        {
            format = !first.Contains("name") && (first.Contains("description") || first.Contains("date")) ? ImportFormat.Alpv : ImportFormat.Oadm;
        }
        else
        {
            format = first.Count == 3 && (first[1].Length == 0 || DateTime.TryParse(first[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) ? ImportFormat.Alpv : ImportFormat.Oadm;
        }

        var rows = new List<CsvPlateRow>(records.Count);
        foreach (var record in hasHeader ? records.Skip(1) : records)
        {
            var cells = record.Cells.Select(c => Csv.Unguard(c).Trim()).ToList();
            if (cells.All(c => c.Length == 0))
            {
                continue;
            }

            rows.Add(record.Unterminated
                ? new CsvPlateRow(record.Line, null, [], false, "The row ends inside quotes.", cells.FirstOrDefault() ?? string.Empty)
                : format == ImportFormat.Alpv ? AlpvRow(record.Line, cells) : OadmRow(record.Line, cells));
        }

        return (format, rows);
    }

    private static CsvPlateRow AlpvRow(int line, List<string> cells)
    {
        var raw = Cell(cells, 0);
        var plate = new Plate { Text = PlateText.Normalize(raw), Name = Cell(cells, 2) };
        return new CsvPlateRow(line, plate, [], false, null, raw);
    }

    private static CsvPlateRow OadmRow(int line, List<string> cells)
    {
        var raw = Cell(cells, 0);
        var tags = Cell(cells, 2).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!TryTime(Cell(cells, 3), out var from))
        {
            return new CsvPlateRow(line, null, tags, true, "Allowed from is not a time (yyyy-MM-dd HH:mm).", raw);
        }

        if (!TryTime(Cell(cells, 4), out var to))
        {
            return new CsvPlateRow(line, null, tags, true, "Allowed to is not a time (yyyy-MM-dd HH:mm).", raw);
        }

        var delete = Cell(cells, 5).ToUpperInvariant() switch
        {
            "" => (bool?)(to is not null),
            "YES" or "TRUE" or "1" or "Y" => true,
            "NO" or "FALSE" or "0" or "N" => false,
            _ => null,
        };
        if (delete is null)
        {
            return new CsvPlateRow(line, null, tags, true, "Delete when it ends is Yes or No.", raw);
        }

        var plate = new Plate { Text = PlateText.Normalize(raw), Name = Cell(cells, 1), AllowedFrom = from, AllowedTo = to, DeleteWhenEnds = delete.Value };
        return new CsvPlateRow(line, plate, tags, true, null, raw);
    }

    private static string Cell(List<string> cells, int index) => index < cells.Count ? cells[index] : string.Empty;

    private static bool TryTime(string text, out DateTime? time)
    {
        time = null;
        if (text.Length == 0)
        {
            return true;
        }

        string[] formats = [TimeFormat, "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd"];
        if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            time = PlateRules.Minutes(parsed);
            return true;
        }

        return false;
    }
}
