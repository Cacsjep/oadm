using System.Globalization;
using System.Text;

using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;

using Oadm.Contracts.Text;
using Oadm.Server.Logging.Export;

namespace Oadm.Plugins.LicensePlates.Log;

/// <summary>The head of an exported log: the filter lines, who exported it when, the server, the counts.</summary>
/// <param name="Filter">"Time: Last 24 hours, 2026-11-26 10:00 to 2026-11-27 10:00", "Result: Allowed", "Search: W1".</param>
/// <param name="Total">How many reads the filters match; more than the rows when the export is limited.</param>
internal sealed record PlateExportHead(IReadOnlyList<string> Filter, string ServerName, string ExportedBy, DateTimeOffset ExportedAt, int Total);

/// <summary>
/// The CSV and PDF of the Log tab (docs/spec/license-plate-center.md "Log", decision 2026-11-27): exactly the rows the filters
/// and the search show, newest first, at most <see cref="LicensePlateLimits.MaxExportRows"/>. The CSV has the grid's columns
/// plus Read as, Area, Country, Vehicle and the ISO time; the PDF has the look of the Logs page export (A4 landscape, Roboto
/// through the shared <see cref="LogPdfFonts"/>), with the plate crops while it has at most
/// <see cref="LicensePlateLimits.MaxPdfPictureRows"/> rows.
/// </summary>
internal static class PlateLogExport
{
    public const string Title = "License plate log";

    public static readonly IReadOnlyList<string> CsvColumns =
        ["Time", "Time (ISO 8601)", "Camera", "Plate", "Read as", "Name", "Tags", "Result", "Direction", "Confidence", "Area", "Country", "Vehicle"];

    private static readonly Color Text = new(0x20, 0x20, 0x20);
    private static readonly Color Secondary = new(0x6B, 0x6B, 0x6B);
    private static readonly Color Line = new(0xD9, 0xD9, 0xD9);
    private static readonly Color Surface = new(0xF2, 0xF2, 0xF2);
    private static readonly Unit TableWidth = Unit.FromCentimeter(29.7 - 2 * 1.5);

    /// <summary>The CSV: UTF-8 with BOM, a first line "The newest 10,000 of 48,213 reads" when limited.</summary>
    public static byte[] Csv(IReadOnlyList<PlateRead> rows, PlateExportHead head, LogExportClock clock)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(head);
        ArgumentNullException.ThrowIfNull(clock);
        var sb = new StringBuilder(256 + rows.Count * 128);
        if (head.Total > rows.Count)
        {
            CsvWriter.AppendRecord(sb, [LicensePlateTexts.Newest(rows.Count, head.Total)]);
        }

        CsvWriter.AppendRecord(sb, CsvColumns);
        foreach (var row in rows)
        {
            if (row.Gap)
            {
                CsvWriter.AppendRecord(sb, [clock.Text(row.Time), clock.Iso(row.Time), row.Camera, string.Empty, string.Empty, GapText(row, clock)]);
                continue;
            }

            CsvWriter.AppendRecord(sb,
            [
                clock.Text(row.Time),
                clock.Iso(row.Time),
                row.Camera,
                row.Plate,
                row.ReadAs ?? string.Empty,
                row.Name,
                string.Join(", ", row.Tags),
                LicensePlateTexts.Result(row.Result),
                LicensePlateTexts.Direction(row.Direction),
                row.Confidence.ToString(CultureInfo.InvariantCulture),
                row.Area?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                row.Country ?? string.Empty,
                Vehicle(row),
            ]);
        }

        return CsvWriter.ToBytes(sb.ToString());
    }

    /// <summary>"Rear, Car, Red, Volvo XC60".</summary>
    public static string Vehicle(PlateRead row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var make = string.Join(" ", new[] { row.VehicleMake, row.VehicleModel }.Where(v => !string.IsNullOrWhiteSpace(v)));
        return string.Join(", ", new[] { row.View, row.VehicleType, row.VehicleColor, make }.Where(v => !string.IsNullOrWhiteSpace(v)));
    }

    private static string GapText(PlateRead row, LogExportClock clock) =>
        LicensePlateTexts.Gap(clock.Local(row.Time), clock.Local(row.Until ?? row.Time));

    /// <summary>The head lines of the PDF.</summary>
    public static IReadOnlyList<string> HeadLines(IReadOnlyList<PlateRead> rows, PlateExportHead head)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(head);
        var lines = new List<string>(head.Filter);
        var reads = rows.Where(r => !r.Gap).ToList();
        lines.Add(head.Total > rows.Count ? LicensePlateTexts.Newest(rows.Count, head.Total) : LicensePlateTexts.Reads(reads.Count));
        lines.Add(string.Create(CultureInfo.InvariantCulture,
            $"{LicensePlateTexts.Allowed}: {reads.Count(r => r.Result == ReadResult.Allowed):N0} · {LicensePlateTexts.NotAllowed}: {reads.Count(r => r.Result == ReadResult.NotAllowed):N0} · {LicensePlateTexts.Unknown}: {reads.Count(r => r.Result == ReadResult.Unknown):N0}"));
        lines.Add("Server: " + head.ServerName);
        lines.Add(string.Create(CultureInfo.InvariantCulture, $"Exported {head.ExportedAt:yyyy-MM-dd HH:mm} by {head.ExportedBy}"));
        return lines;
    }

    /// <summary>The PDF; <paramref name="picture"/> gives the plate crop of a row (JPEG) or null.</summary>
    public static byte[] Pdf(IReadOnlyList<PlateRead> rows, PlateExportHead head, LogExportClock clock, Func<PlateRead, byte[]?> picture, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(head);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(picture);
        LogPdfFonts.EnsureInstalled();
        var pictures = rows.Count <= LicensePlateLimits.MaxPdfPictureRows;
        var document = new Document();
        document.Info.Title = Title;
        document.Info.Author = head.ExportedBy;
        document.Info.Subject = "OADM " + Title;
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = LogPdfFonts.Family;
        normal.Font.Size = 8;
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

        var list = new List<(string Header, double Width)>();
        if (pictures)
        {
            list.Add(("Picture", 1.6));
        }

        list.AddRange([("Time", 1.7), ("Camera", 1.8), ("Plate", 1.5), ("Name", 2.0), ("Tags", 1.8), ("Result", 1.3), ("Direction", 1.0), ("Confidence", 1.0)]);
        var columns = list.ToArray();
        var share = columns.Sum(c => c.Width);
        var grid = section.AddTable();
        grid.Borders.Visible = false;
        grid.LeftPadding = Unit.FromPoint(3);
        grid.RightPadding = Unit.FromPoint(3);
        grid.TopPadding = Unit.FromPoint(2);
        grid.BottomPadding = Unit.FromPoint(2);
        foreach (var (_, width) in columns)
        {
            grid.AddColumn(TableWidth * (width / share));
        }

        var header = grid.AddRow();
        header.HeadingFormat = true;
        header.Shading.Color = Surface;
        header.Format.Font.Bold = true;
        for (var i = 0; i < columns.Length; i++)
        {
            header.Cells[i].AddParagraph(columns[i].Header);
        }

        var pictureWidth = TableWidth * (1.6 / share) - Unit.FromPoint(6);
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
            var c = 0;
            if (pictures)
            {
                if (!read.Gap && read.HasPicture && picture(read) is { Length: > 0 } jpeg)
                {
                    var image = row.Cells[c].AddParagraph().AddImage("base64:" + Convert.ToBase64String(jpeg));
                    image.LockAspectRatio = true;
                    image.Width = pictureWidth;
                }

                c++;
            }

            row.Cells[c++].AddParagraph(clock.Text(read.Time));
            row.Cells[c++].AddParagraph(read.Camera);
            if (read.Gap)
            {
                row.Cells[c].MergeRight = columns.Length - c - 1;
                row.Cells[c].AddParagraph(GapText(read, clock)).Format.Font.Color = Secondary;
                continue;
            }

            var plate = row.Cells[c++].AddParagraph(read.Plate);
            if (read.ReadAs is { Length: > 0 } readAs)
            {
                plate.AddLineBreak();
                plate.AddFormattedText("read as " + readAs).Color = Secondary;
            }

            row.Cells[c++].AddParagraph(read.Name);
            row.Cells[c++].AddParagraph(string.Join(", ", read.Tags));
            row.Cells[c++].AddParagraph(LicensePlateTexts.Result(read.Result));
            row.Cells[c++].AddParagraph(LicensePlateTexts.Direction(read.Direction));
            var confidence = row.Cells[c].AddParagraph(read.Confidence.ToString(CultureInfo.InvariantCulture));
            confidence.Format.Alignment = ParagraphAlignment.Right;
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
