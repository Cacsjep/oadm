using System.Globalization;
using System.Text;

using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;

using Oadm.Contracts.Text;
using Oadm.Server.Logging.Export;

namespace Oadm.Plugins.LicensePlates.Log;

/// <summary>The head of an exported log: the filter lines, who exported it when, the server, the counts, the columns.</summary>
/// <param name="Filter">"Time: Last 24 hours, 2026-11-26 10:00 to 2026-11-27 10:00", "Result: Allowed", "Search: W1".</param>
/// <param name="Total">How many reads the filters match; more than the rows when the export is limited.</param>
internal sealed record PlateExportHead(IReadOnlyList<string> Filter, string ServerName, string ExportedBy, DateTimeOffset ExportedAt, int Total)
{
    /// <summary>The Log's chosen columns in their order (<see cref="LogColumns"/> keys).</summary>
    public IReadOnlyList<string> Columns { get; init; } = LogColumns.Defaults;
}

/// <summary>
/// The CSV and PDF of the Log tab (docs/spec/license-plate-center.md "Log", decisions 2026-11-27): exactly the rows the filters
/// and the search show, newest first, at most <see cref="LicensePlateLimits.MaxExportRows"/>, in the columns the user chose
/// on the Log in their order (the CSV without Picture). The PDF has the look of the Logs page export (A4 landscape, Roboto
/// through the shared <see cref="LogPdfFonts"/>), with the plate crops while it has at most
/// <see cref="LicensePlateLimits.MaxPdfPictureRows"/> rows and at most <see cref="MaxPdfColumnsWithPicture"/> other columns;
/// else Picture is left out and its head says so.
/// </summary>
internal static class PlateLogExport
{
    public const string Title = "License plate log";

    /// <summary>More columns than this beside Picture leave no room for the crop on A4 landscape.</summary>
    public const int MaxPdfColumnsWithPicture = 11;

    private static readonly Color Text = new(0x20, 0x20, 0x20);
    private static readonly Color Secondary = new(0x6B, 0x6B, 0x6B);
    private static readonly Color Line = new(0xD9, 0xD9, 0xD9);
    private static readonly Color Surface = new(0xF2, 0xF2, 0xF2);
    private static readonly Unit TableWidth = Unit.FromCentimeter(29.7 - 2 * 1.5);

    /// <summary>The CSV's columns: the chosen ones without Picture.</summary>
    public static IReadOnlyList<string> CsvKeys(PlateExportHead head)
    {
        ArgumentNullException.ThrowIfNull(head);
        return [.. head.Columns.Where(k => k != LogColumns.Picture)];
    }

    /// <summary>Whether the PDF has the plate crops: Picture chosen, few enough rows and columns.</summary>
    public static bool PdfPictures(IReadOnlyList<PlateRead> rows, PlateExportHead head)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(head);
        return head.Columns.Contains(LogColumns.Picture) && rows.Count <= LicensePlateLimits.MaxPdfPictureRows
            && head.Columns.Count - 1 <= MaxPdfColumnsWithPicture;
    }

    /// <summary>The PDF's columns: the chosen ones, Picture only when <see cref="PdfPictures"/>.</summary>
    public static IReadOnlyList<string> PdfKeys(IReadOnlyList<PlateRead> rows, PlateExportHead head) =>
        PdfPictures(rows, head) ? head.Columns : [.. head.Columns.Where(k => k != LogColumns.Picture)];

    /// <summary>"27 Nov 2026 14:04:14" in the user's zone, like the Log.</summary>
    public static string TimeText(DateTimeOffset time, LogExportClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return LogColumns.TimeText(clock.Local(time), CultureInfo.InvariantCulture);
    }

    /// <summary>The text of one cell of the files.</summary>
    public static string Cell(PlateRead row, string key, LogExportClock clock) => key switch
    {
        LogColumns.Time => TimeText(row.Time, clock),
        LogColumns.Confidence => row.Gap ? string.Empty : row.Confidence.ToString(CultureInfo.InvariantCulture),
        LogColumns.Area => row.Gap ? string.Empty : row.Area?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        _ => LogColumns.Text(row, key),
    };

    /// <summary>The CSV: UTF-8 with BOM, a first line "The newest 10,000 of 48,213 reads" when limited.</summary>
    public static byte[] Csv(IReadOnlyList<PlateRead> rows, PlateExportHead head, LogExportClock clock)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(head);
        ArgumentNullException.ThrowIfNull(clock);
        var keys = CsvKeys(head);
        var sb = new StringBuilder(256 + rows.Count * 128);
        if (head.Total > rows.Count)
        {
            CsvWriter.AppendRecord(sb, [LicensePlateTexts.Newest(rows.Count, head.Total)]);
        }

        CsvWriter.AppendRecord(sb, [.. keys.Select(LogColumns.Header)]);
        var gapAt = GapColumn(keys);
        foreach (var row in rows)
        {
            var cells = keys.Select(k => Cell(row, k, clock)).ToArray();
            if (row.Gap && gapAt >= 0)
            {
                cells[gapAt] = GapText(row, clock);
            }

            CsvWriter.AppendRecord(sb, cells);
        }

        return CsvWriter.ToBytes(sb.ToString());
    }

    /// <summary>Where a gap row says "Connection lost from ... to ...": the first column other than Time, Camera and Picture.</summary>
    private static int GapColumn(IReadOnlyList<string> keys)
    {
        for (var i = 0; i < keys.Count; i++)
        {
            if (keys[i] is not (LogColumns.Time or LogColumns.Camera or LogColumns.Picture))
            {
                return i;
            }
        }

        return -1;
    }

    private static string GapText(PlateRead row, LogExportClock clock) =>
        LicensePlateTexts.Gap(clock.Local(row.Time), clock.Local(row.Until ?? row.Time));

    /// <summary>The head lines of the PDF; one more when Picture was chosen but is left out.</summary>
    public static IReadOnlyList<string> HeadLines(IReadOnlyList<PlateRead> rows, PlateExportHead head)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(head);
        var lines = new List<string>(head.Filter);
        var reads = rows.Where(r => !r.Gap).ToList();
        lines.Add(head.Total > rows.Count ? LicensePlateTexts.Newest(rows.Count, head.Total) : LicensePlateTexts.Reads(reads.Count));
        lines.Add(string.Create(CultureInfo.InvariantCulture,
            $"{LicensePlateTexts.Allowed}: {reads.Count(r => r.Result == ReadResult.Allowed):N0} · {LicensePlateTexts.NotAllowed}: {reads.Count(r => r.Result == ReadResult.NotAllowed):N0} · {LicensePlateTexts.Unknown}: {reads.Count(r => r.Result == ReadResult.Unknown):N0}"));
        if (head.Columns.Contains(LogColumns.Picture) && !PdfPictures(rows, head))
        {
            lines.Add(rows.Count > LicensePlateLimits.MaxPdfPictureRows
                ? string.Create(CultureInfo.InvariantCulture, $"Without pictures: more than {LicensePlateLimits.MaxPdfPictureRows:N0} reads.")
                : "Without pictures: too many columns.");
        }

        lines.Add("Server: " + head.ServerName);
        lines.Add(string.Create(CultureInfo.InvariantCulture, $"Exported {head.ExportedAt:yyyy-MM-dd HH:mm} by {head.ExportedBy}"));
        return lines;
    }

    /// <summary>The share of the table width of a column.</summary>
    private static double Weight(string key) => key switch
    {
        LogColumns.Picture => 1.6,
        LogColumns.Time => 2.0,
        LogColumns.Camera => 1.8,
        LogColumns.Plate or LogColumns.ReadAs => 1.5,
        LogColumns.Name => 2.0,
        LogColumns.Tags => 1.8,
        LogColumns.Result => 1.3,
        LogColumns.VehicleMake or LogColumns.VehicleModel or LogColumns.VehicleType or LogColumns.VehicleColor => 1.3,
        _ => 1.0,
    };

    /// <summary>The PDF; <paramref name="picture"/> gives the plate crop of a row (JPEG) or null.</summary>
    public static byte[] Pdf(IReadOnlyList<PlateRead> rows, PlateExportHead head, LogExportClock clock, Func<PlateRead, byte[]?> picture, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(head);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(picture);
        LogPdfFonts.EnsureInstalled();
        var keys = PdfKeys(rows, head);
        var document = new Document();
        document.Info.Title = Title;
        document.Info.Author = head.ExportedBy;
        document.Info.Subject = "OADM " + Title;
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = LogPdfFonts.Family;
        normal.Font.Size = keys.Count > 12 ? 7 : 8;
        normal.Font.Color = Text;
        var footerStyle = document.Styles[StyleNames.Footer]!;
        footerStyle.Font.Size = 7.5;
        footerStyle.Font.Color = Secondary;

        var section = document.AddSection();
        var setup = document.DefaultPageSetup.Clone();
        setup.PageWidth = Unit.FromCentimeter(29.7);
        setup.PageHeight = Unit.FromCentimeter(21);
        setup.Orientation = Orientation.Portrait;
        setup.TopMargin = Unit.FromCentimeter(1.3);
        setup.BottomMargin = Unit.FromCentimeter(1.6);
        setup.LeftMargin = Unit.FromCentimeter(1.5);
        setup.RightMargin = Unit.FromCentimeter(1.5);
        setup.FooterDistance = Unit.FromCentimeter(0.7);
        section.PageSetup = setup;

        var foot = section.Footers.Primary.AddTable();
        foot.Borders.Visible = false;
        foot.AddColumn(TableWidth * 0.75);
        foot.AddColumn(TableWidth * 0.25);
        var footRow = foot.AddRow();
        footRow.Cells[0].AddParagraph(Title + " · " + head.ServerName);
        var pages = footRow.Cells[1].AddParagraph();
        pages.Format.Alignment = ParagraphAlignment.Right;
        pages.AddText("Page ");
        pages.AddPageField();
        pages.AddText(" of ");
        pages.AddNumPagesField();

        var title = section.AddParagraph(Title);
        title.Format.Font.Size = 16;
        title.Format.Font.Bold = true;
        title.Format.SpaceAfter = Unit.FromPoint(4);
        foreach (var line in HeadLines(rows, head))
        {
            var p = section.AddParagraph(line);
            p.Format.Font.Color = Secondary;
            p.Format.Font.Size = 8.5;
        }

        section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(6);

        var share = keys.Sum(Weight);
        var grid = section.AddTable();
        grid.Borders.Visible = false;
        grid.LeftPadding = Unit.FromPoint(3);
        grid.RightPadding = Unit.FromPoint(3);
        grid.TopPadding = Unit.FromPoint(2);
        grid.BottomPadding = Unit.FromPoint(2);
        foreach (var key in keys)
        {
            grid.AddColumn(TableWidth * (Weight(key) / share));
        }

        var header = grid.AddRow();
        header.HeadingFormat = true;
        header.Shading.Color = Surface;
        header.Format.Font.Bold = true;
        for (var i = 0; i < keys.Count; i++)
        {
            var p = header.Cells[i].AddParagraph(LogColumns.Header(keys[i]));
            if (keys[i] == LogColumns.Confidence)
            {
                p.Format.Alignment = ParagraphAlignment.Right;
            }
        }

        var pictureWidth = TableWidth * (Weight(LogColumns.Picture) / share) - Unit.FromPoint(6);
        var inlineReadAs = !keys.Contains(LogColumns.ReadAs);
        var gapAt = GapColumn(keys);
        for (var r = 0; r < rows.Count; r++)
        {
            if ((r & 127) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            var read = rows[r];
            var row = grid.AddRow();
            row.Borders.Bottom.Color = Line;
            row.Borders.Bottom.Width = Unit.FromPoint(0.5);
            for (var c = 0; c < keys.Count; c++)
            {
                var key = keys[c];
                if (read.Gap && c == gapAt)
                {
                    row.Cells[c].MergeRight = keys.Count - c - 1;
                    row.Cells[c].AddParagraph(GapText(read, clock)).Format.Font.Color = Secondary;
                    break;
                }

                switch (key)
                {
                    case LogColumns.Picture:
                        if (!read.Gap && read.HasPicture && picture(read) is { Length: > 0 } jpeg)
                        {
                            var image = row.Cells[c].AddParagraph().AddImage("base64:" + Convert.ToBase64String(jpeg));
                            image.LockAspectRatio = true;
                            image.Width = pictureWidth;
                        }

                        break;
                    case LogColumns.Plate:
                        var plate = row.Cells[c].AddParagraph(read.Plate);
                        if (inlineReadAs && read.ReadAs is { Length: > 0 } readAs)
                        {
                            plate.AddLineBreak();
                            plate.AddFormattedText("read as " + readAs).Color = Secondary;
                        }

                        break;
                    case LogColumns.Confidence:
                        row.Cells[c].AddParagraph(Cell(read, key, clock)).Format.Alignment = ParagraphAlignment.Right;
                        break;
                    default:
                        row.Cells[c].AddParagraph(Cell(read, key, clock));
                        break;
                }
            }
        }

        if (rows.Count == 0)
        {
            section.AddParagraph("No reads.");
        }

        ct.ThrowIfCancellationRequested();
        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        renderer.PdfDocument.Options.CompressContentStreams = true;
        using var stream = new MemoryStream();
        renderer.PdfDocument.Save(stream, closeStream: false);
        return stream.ToArray();
    }
}
