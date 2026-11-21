using System.Globalization;
using System.Text;

using Oadm.Contracts.Text;

namespace Oadm.Server.Logging.Export;

/// <summary>One column of an exported log: its header, its share of the PDF's width, and where it shows.</summary>
/// <param name="Width">Relative width in the PDF (the columns share the page width by these numbers).</param>
/// <param name="InPdf">False for the columns only the CSV holds (the ISO time, the exception).</param>
public sealed record LogColumn(string Header, double Width, bool InPdf = true);

/// <summary>
/// An exported log as a table: the grid's columns and its rows, newest first (docs/spec/main-window.md "Logs page"). The one
/// input of <see cref="LogCsv"/> and <see cref="LogPdf"/>, for every log that is exported (Server, Client, Audit; later the
/// event log and the logs of plugins).
/// </summary>
public sealed record LogTable(IReadOnlyList<LogColumn> Columns, IReadOnlyList<string[]> Rows);

/// <summary>What the head of the PDF says: the log, the filter lines, the server, who exported when.</summary>
/// <param name="Filter">"Dates: Last 24 hours, 2026-11-20 10:00 to 2026-11-21 10:00", "Level: Warnings and errors", "Search: gate".</param>
public sealed record LogExportHead(string LogName, IReadOnlyList<string> Filter, string ServerName, string ExportedBy, DateTimeOffset ExportedAt, int Entries, bool Limited);

/// <summary>The CSV of an exported log: every column, UTF-8 with BOM, comma, CRLF (<see cref="CsvWriter"/>).</summary>
public static class LogCsv
{
    public static byte[] ToBytes(LogTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        var sb = new StringBuilder(128 + table.Rows.Count * 96);
        CsvWriter.AppendRecord(sb, table.Columns.Select(c => c.Header));
        foreach (var row in table.Rows)
        {
            CsvWriter.AppendRecord(sb, row);
        }

        return CsvWriter.ToBytes(sb.ToString());
    }
}

/// <summary>The exporting user's clock: their time zone (Windows or IANA id), else a fixed offset.</summary>
public sealed class LogExportClock
{
    private readonly TimeZoneInfo _zone;

    private LogExportClock(TimeZoneInfo zone) => _zone = zone;

    /// <summary>The zone of <paramref name="timeZoneId"/> when this server knows it, else UTC + <paramref name="offsetMinutes"/>.</summary>
    public static LogExportClock Of(string? timeZoneId, int offsetMinutes)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            try
            {
                return new LogExportClock(TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim()));
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // the offset below
            }
        }

        var offset = TimeSpan.FromMinutes(Math.Clamp(offsetMinutes, -14 * 60, 14 * 60));
        return new LogExportClock(offset == TimeSpan.Zero ? TimeZoneInfo.Utc : TimeZoneInfo.CreateCustomTimeZone("oadm-client", offset, "Client", "Client"));
    }

    public DateTimeOffset Local(DateTimeOffset time) => TimeZoneInfo.ConvertTime(time, _zone);

    /// <summary>"2026-11-21 10:04:05" in the user's time.</summary>
    public string Text(DateTimeOffset time) => Local(time).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>"2026-11-21T10:04:05+01:00".</summary>
    public string Iso(DateTimeOffset time) => Local(time).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    /// <summary>"2026-11-21 10:04" (the range of the header).</summary>
    public string Minutes(DateTimeOffset time) => Local(time).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
