using System.Text;

namespace Oadm.Client.Infrastructure;

/// <summary>
/// RFC 4180 CSV: comma separated, fields with a comma, quote or line break in double quotes (quotes
/// doubled), CRLF between records. Written cells are safe to open in a spreadsheet: a cell that starts
/// with = + - @ (or a tab / carriage return) gets a leading apostrophe so it is never run as a formula.
/// </summary>
public static class Csv
{
    /// <summary>One cell as written: formula-safe and quoted when needed.</summary>
    public static string Field(string? value)
    {
        string text = value ?? "";
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            text = "'" + text;
        }

        return text.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : text;
    }

    /// <summary>Appends one record (cells and CRLF).</summary>
    public static void AppendRecord(StringBuilder sb, IEnumerable<string?> cells)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(cells);
        bool first = true;
        foreach (string? cell in cells)
        {
            if (!first)
            {
                sb.Append(',');
            }

            sb.Append(Field(cell));
            first = false;
        }

        sb.Append("\r\n");
    }

    /// <summary>Undoes the formula guard of <see cref="Field"/> ("'=1" reads as "=1").</summary>
    public static string Unguard(string cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        return cell.Length > 1 && cell[0] == '\'' && cell[1] is '=' or '+' or '-' or '@' or '\t' or '\r' ? cell[1..] : cell;
    }

    /// <summary>
    /// Reads every record of a CSV text. Accepts CRLF, LF and CR line ends and quoted fields over several
    /// lines; a quote inside an unquoted field is kept as text. <see cref="CsvRecord.Line"/> is the line the
    /// record starts on (1-based). Empty lines are skipped; a leading BOM is ignored. <paramref name="separator"/>
    /// is ',' or ';'.
    /// </summary>
    public static IEnumerable<CsvRecord> Read(string text, char separator = ',')
    {
        ArgumentNullException.ThrowIfNull(text);
        int i = text.Length > 0 && text[0] == '﻿' ? 1 : 0;
        int line = 1;
        var cells = new List<string>();
        var cell = new StringBuilder();
        while (i < text.Length)
        {
            int startLine = line;
            int startIndex = i;
            cells.Clear();
            bool unterminated = false;
            while (true)
            {
                cell.Clear();
                if (i < text.Length && text[i] == '"')
                {
                    i++;
                    while (true)
                    {
                        if (i >= text.Length)
                        {
                            unterminated = true;
                            break;
                        }

                        char c = text[i];
                        if (c == '"')
                        {
                            if (i + 1 < text.Length && text[i + 1] == '"')
                            {
                                cell.Append('"');
                                i += 2;
                                continue;
                            }

                            i++;
                            break;
                        }

                        if (c == '\n' || (c == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n')))
                        {
                            line++;
                        }

                        cell.Append(c);
                        i++;
                    }
                }

                // Unquoted text, or text after a closing quote, up to the separator belongs to the field.
                while (i < text.Length && text[i] != separator && text[i] is not ('\r' or '\n'))
                {
                    cell.Append(text[i++]);
                }

                cells.Add(cell.ToString());
                if (i < text.Length && text[i] == separator)
                {
                    i++;
                    continue;
                }

                break;
            }

            int length = i - startIndex;
            if (i < text.Length && text[i] == '\r')
            {
                i++;
            }

            if (i < text.Length && text[i] == '\n')
            {
                i++;
            }

            line++;
            bool empty = cells.Count == 1 && cells[0].Trim().Length == 0 && !unterminated;
            if (!empty)
            {
                yield return new CsvRecord(startLine, [.. cells], length, unterminated);
            }
        }
    }
}

/// <summary>One CSV record.</summary>
/// <param name="Line">Line the record starts on, 1-based.</param>
/// <param name="Cells">The fields, unquoted.</param>
/// <param name="Length">Characters of the record in the text (for the line length limit).</param>
/// <param name="Unterminated">A quoted field was not closed before the end of the text.</param>
public sealed record CsvRecord(int Line, IReadOnlyList<string> Cells, int Length, bool Unterminated);
