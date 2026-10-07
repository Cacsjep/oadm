using System.Globalization;

using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;

namespace Oadm.Plugins.SnapshotReport.Report;

/// <summary>One picture of the report: the tile (device facts and source label) and its snapshot.</summary>
public sealed record ReportEntry(SnapshotTile Tile, CapturedSnapshot Snapshot);

/// <summary>The rendered PDF and its page count.</summary>
public sealed record RenderedReport(byte[] Pdf, int Pages);

/// <summary>Everything the PDF shows.</summary>
public sealed record ReportData(
    string Site,
    string Technician,
    DateOnly Date,
    string OadmVersion,
    DateTime GeneratedUtc,
    IReadOnlyList<ReportEntry> Entries);

/// <summary>Numbers of the cover page.</summary>
public sealed record ReportSummary(
    int Devices,
    int Sources,
    int Online,
    int Offline,
    int Snapshots,
    int FailedSnapshots,
    IReadOnlyList<(string Version, int Devices)> Firmware,
    IReadOnlyList<DeviceFacts> CertificatesDue)
{
    public static ReportSummary From(ReportData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var devices = data.Entries.Select(e => e.Tile.Device).DistinctBy(d => d.DeviceId).ToList();
        var online = devices.Count(FactsText.IsOnline);
        var firmware = devices
            .GroupBy(d => string.IsNullOrWhiteSpace(d.Firmware) ? "Unknown" : d.Firmware!)
            .Select(g => (g.Key, g.Count()))
            .OrderByDescending(g => g.Item2)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
        var due = devices
            .Where(d => FactsText.IsCertificateDue(d.CertNotAfterUtc, data.GeneratedUtc))
            .OrderBy(d => d.CertNotAfterUtc)
            .ToList();
        var ok = data.Entries.Count(e => e.Snapshot.IsOk);
        return new ReportSummary(devices.Count, data.Entries.Count, online, devices.Count - online, ok, data.Entries.Count - ok, firmware, due);
    }
}

/// <summary>
/// Builds the maintenance report with MigraDoc (PDFsharp, MIT): A4 portrait, a cover page with the summary,
/// then two snapshots per page with the device facts; footer with site, date and page numbers. Snapshots
/// are embedded as the JPEG files the devices sent (PDFsharp passes JPEG through without re-encoding).
/// </summary>
public static class ReportDocument
{
    public const string Title = "Maintenance report";

    /// <summary>Box of one snapshot on the page; the picture keeps its aspect ratio inside.</summary>
    public static readonly Unit PictureMaxWidth = Unit.FromCentimeter(17);
    public static readonly Unit PictureMaxHeight = Unit.FromCentimeter(8.5);

    public const int EntriesPerPage = 2;

    private static readonly Color Text = new(0x20, 0x20, 0x20);
    private static readonly Color Secondary = new(0x6B, 0x6B, 0x6B);
    private static readonly Color Accent = new(0x6C, 0x5C, 0xE7);
    private static readonly Color Line = new(0xD9, 0xD9, 0xD9);
    private static readonly Color Surface = new(0xF2, 0xF2, 0xF2);
    private static readonly Color Error = new(0xC7, 0x3B, 0x34);
    private static readonly Color Warning = new(0xA8, 0x6F, 0x0E);

    /// <summary>Renders the report to PDF bytes.</summary>
    public static RenderedReport Render(ReportData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        ReportFonts.EnsureInstalled();
        var document = Build(data);
        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        renderer.PdfDocument.Options.CompressContentStreams = true;
        var pages = renderer.PdfDocument.PageCount;
        using var stream = new MemoryStream();
        renderer.PdfDocument.Save(stream, closeStream: false);
        return new RenderedReport(stream.ToArray(), pages);
    }

    /// <summary>The MigraDoc document (tests and previews).</summary>
    public static Document Build(ReportData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var document = new Document();
        document.Info.Title = $"{Title} - {data.Site}";
        document.Info.Author = string.IsNullOrWhiteSpace(data.Technician) ? "OADM" : data.Technician;
        document.Info.Subject = "Snapshots and device facts";
        DefineStyles(document);

        var section = document.AddSection();
        var setup = document.DefaultPageSetup.Clone();
        setup.PageFormat = PageFormat.A4;
        setup.Orientation = Orientation.Portrait;
        setup.TopMargin = Unit.FromCentimeter(1.6);
        setup.BottomMargin = Unit.FromCentimeter(2.0);
        setup.LeftMargin = Unit.FromCentimeter(2.0);
        setup.RightMargin = Unit.FromCentimeter(2.0);
        setup.FooterDistance = Unit.FromCentimeter(0.9);
        section.PageSetup = setup;
        AddFooter(section, data);

        AddCover(section, data);
        for (var i = 0; i < data.Entries.Count; i++)
        {
            if (i % EntriesPerPage == 0)
            {
                section.AddPageBreak();
            }

            AddEntry(section, data.Entries[i], data.GeneratedUtc);
        }

        return document;
    }

    private static void DefineStyles(Document document)
    {
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = ReportFonts.Family;
        normal.Font.Size = 9;
        normal.Font.Color = Text;

        var title = document.Styles.AddStyle("ReportTitle", StyleNames.Normal);
        title.Font.Size = 24;
        title.Font.Bold = true;
        title.ParagraphFormat.SpaceAfter = Unit.FromPoint(4);

        var heading = document.Styles[StyleNames.Heading1]!;
        heading.Font.Name = ReportFonts.Family;
        heading.Font.Size = 13;
        heading.Font.Bold = true;
        heading.Font.Color = Text;
        heading.ParagraphFormat.SpaceBefore = Unit.FromPoint(16);
        heading.ParagraphFormat.SpaceAfter = Unit.FromPoint(6);
        heading.ParagraphFormat.KeepWithNext = true;

        var entry = document.Styles[StyleNames.Heading2]!;
        entry.Font.Name = ReportFonts.Family;
        entry.Font.Size = 11;
        entry.Font.Bold = true;
        entry.Font.Color = Text;
        entry.ParagraphFormat.SpaceBefore = Unit.FromPoint(0);
        entry.ParagraphFormat.SpaceAfter = Unit.FromPoint(5);
        entry.ParagraphFormat.KeepWithNext = true;

        var secondary = document.Styles.AddStyle("Secondary", StyleNames.Normal);
        secondary.Font.Color = Secondary;

        var footer = document.Styles[StyleNames.Footer]!;
        footer.Font.Size = 8;
        footer.Font.Color = Secondary;
    }

    private static void AddFooter(Section section, ReportData data)
    {
        var table = section.Footers.Primary.AddTable();
        table.Borders.Visible = false;
        table.AddColumn(Unit.FromCentimeter(12));
        table.AddColumn(Unit.FromCentimeter(5));
        var row = table.AddRow();
        row.Cells[0].AddParagraph(FooterText(data));
        var pages = row.Cells[1].AddParagraph();
        pages.Format.Alignment = ParagraphAlignment.Right;
        pages.AddText("Page ");
        pages.AddPageField();
        pages.AddText(" of ");
        pages.AddNumPagesField();
    }

    /// <summary>"Site · 2026-10-07 · Maintenance report".</summary>
    public static string FooterText(ReportData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(data.Site))
        {
            parts.Add(data.Site.Trim());
        }

        parts.Add(FormatDate(data.Date));
        parts.Add(Title);
        return string.Join(" · ", parts);
    }

    public static string FormatDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static void AddCover(Section section, ReportData data)
    {
        var summary = ReportSummary.From(data);

        var accent = section.AddParagraph();
        accent.Format.Borders.Top.Color = Accent;
        accent.Format.Borders.Top.Width = Unit.FromPoint(3);
        accent.Format.SpaceAfter = Unit.FromCentimeter(0.6);

        section.AddParagraph(Title, "ReportTitle");
        section.AddParagraph(string.IsNullOrWhiteSpace(data.Site) ? "Video devices" : data.Site.Trim(), "Secondary").Format.Font.Size = 13;

        section.AddParagraph().Format.SpaceAfter = Unit.FromCentimeter(0.5);
        var facts = NewKeyValueTable(section, Unit.FromCentimeter(4.5), Unit.FromCentimeter(12.5));
        AddKeyValue(facts, "Site / customer", data.Site);
        AddKeyValue(facts, "Technician", data.Technician);
        AddKeyValue(facts, "Date", FormatDate(data.Date));
        AddKeyValue(facts, "Created", data.GeneratedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " with OADM " + data.OadmVersion);

        section.AddParagraph("Summary", StyleNames.Heading1);
        var numbers = NewKeyValueTable(section, Unit.FromCentimeter(6), Unit.FromCentimeter(11));
        AddKeyValue(numbers, "Cameras in report", Count(summary.Devices));
        AddKeyValue(numbers, "Video sources (snapshots)", Count(summary.Sources));
        AddKeyValue(numbers, "Online", Count(summary.Online));
        AddKeyValue(numbers, "Offline or not OK", Count(summary.Offline), summary.Offline > 0 ? Warning : null);
        AddKeyValue(numbers, "Snapshots taken", summary.FailedSnapshots == 0
            ? Count(summary.Snapshots)
            : string.Create(CultureInfo.InvariantCulture, $"{summary.Snapshots} of {summary.Sources} ({summary.FailedSnapshots} failed)"), summary.FailedSnapshots > 0 ? Error : null);

        section.AddParagraph("Firmware versions in use", StyleNames.Heading1);
        var firmware = NewGridTable(section, ("Firmware", 6), ("Cameras", 3));
        foreach (var (version, devices) in summary.Firmware)
        {
            AddGridRow(firmware, null, version, Count(devices));
        }

        section.AddParagraph("Certificates expiring within 30 days or expired", StyleNames.Heading1);
        if (summary.CertificatesDue.Count == 0)
        {
            section.AddParagraph("None. Every HTTPS certificate is valid for more than 30 days.", "Secondary");
        }
        else
        {
            var certificates = NewGridTable(section, ("Address", 4.5), ("Model", 4.5), ("Valid until", 3), ("Expires", 5));
            foreach (var device in summary.CertificatesDue)
            {
                var expired = device.CertNotAfterUtc < data.GeneratedUtc;
                AddGridRow(
                    certificates,
                    expired ? Error : Warning,
                    device.Address,
                    device.Model ?? string.Empty,
                    device.CertNotAfterUtc?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                    FactsText.Expiry(device.CertNotAfterUtc, data.GeneratedUtc));
            }
        }
    }

    private static void AddEntry(Section section, ReportEntry entry, DateTime nowUtc)
    {
        var tile = entry.Tile;
        var device = tile.Device;
        section.AddParagraph(string.IsNullOrWhiteSpace(device.Model) ? tile.Title : $"{tile.Title} · {device.Model}", StyleNames.Heading2);

        var snapshot = entry.Snapshot;
        if (snapshot.IsOk)
        {
            var paragraph = section.AddParagraph();
            paragraph.Format.KeepWithNext = true;
            paragraph.Format.SpaceAfter = Unit.FromPoint(6);
            var image = paragraph.AddImage("base64:" + Convert.ToBase64String(snapshot.Jpeg!));
            image.LockAspectRatio = true;
            var (width, height) = Fit(snapshot.Width, snapshot.Height);
            image.Width = width;
            image.Height = height;
        }
        else
        {
            var box = section.AddTable();
            box.Borders.Visible = false;
            box.AddColumn(PictureMaxWidth);
            var row = box.AddRow();
            row.Height = Unit.FromCentimeter(3);
            row.HeightRule = RowHeightRule.AtLeast;
            row.VerticalAlignment = VerticalAlignment.Center;
            row.Shading.Color = Surface;
            var text = row.Cells[0].AddParagraph("No snapshot: " + (snapshot.Error ?? tile.Error ?? "unknown error"));
            text.Format.Alignment = ParagraphAlignment.Center;
            text.Format.Font.Color = Error;
            var spacer = section.AddParagraph();
            spacer.Format.SpaceAfter = Unit.FromPoint(2);
        }

        var facts = section.AddTable();
        facts.Borders.Visible = false;
        facts.Format.Font.Size = 8.5;
        facts.AddColumn(Unit.FromCentimeter(2.4));
        facts.AddColumn(Unit.FromCentimeter(6.1));
        facts.AddColumn(Unit.FromCentimeter(2.4));
        facts.AddColumn(Unit.FromCentimeter(6.1));
        AddFactsRow(facts, "Model", device.Model ?? "Unknown", "MAC address", FactsText.Mac(device.Serial));
        AddFactsRow(facts, "Address", FactsText.Address(device), "Firmware", device.Firmware ?? "Unknown");
        AddFactsRow(facts, "Status", FactsText.Status(device.Status), "Certificate", FactsText.Certificate(device, nowUtc),
            leftColor: FactsText.IsOnline(device) ? null : Warning,
            rightColor: FactsText.IsCertificateDue(device.CertNotAfterUtc, nowUtc) ? Warning : null);
        AddFactsRow(facts, "Snapshot", snapshot.IsOk ? $"{FactsText.Time(snapshot.CapturedUtc)} · {snapshot.Width}x{snapshot.Height}" : "Not taken", "Source", tile.SourceLabel ?? "Single source");
        var after = section.AddParagraph();
        after.Format.SpaceAfter = Unit.FromCentimeter(0.5);
    }

    /// <summary>Size of the picture inside the box (keeps the aspect ratio; unknown size fills the box width at 16:9).</summary>
    public static (Unit Width, Unit Height) Fit(int pixelWidth, int pixelHeight)
    {
        var aspect = pixelWidth > 0 && pixelHeight > 0 ? (double)pixelWidth / pixelHeight : 16.0 / 9.0;
        var width = PictureMaxWidth.Centimeter;
        var height = width / aspect;
        if (height > PictureMaxHeight.Centimeter)
        {
            height = PictureMaxHeight.Centimeter;
            width = height * aspect;
        }

        return (Unit.FromCentimeter(width), Unit.FromCentimeter(height));
    }

    private static void AddFactsRow(Table table, string leftLabel, string leftValue, string rightLabel, string rightValue, Color? leftColor = null, Color? rightColor = null)
    {
        var row = table.AddRow();
        row.TopPadding = Unit.FromPoint(1.5);
        row.BottomPadding = Unit.FromPoint(1.5);
        row.Borders.Bottom.Color = Line;
        row.Borders.Bottom.Width = Unit.FromPoint(0.5);
        Label(row.Cells[0], leftLabel);
        Value(row.Cells[1], leftValue, leftColor);
        Label(row.Cells[2], rightLabel);
        Value(row.Cells[3], rightValue, rightColor);
    }

    private static Table NewKeyValueTable(Section section, Unit labelWidth, Unit valueWidth)
    {
        var table = section.AddTable();
        table.Borders.Visible = false;
        table.AddColumn(labelWidth);
        table.AddColumn(valueWidth);
        return table;
    }

    private static void AddKeyValue(Table table, string label, string? value, Color? color = null)
    {
        var row = table.AddRow();
        row.TopPadding = Unit.FromPoint(3);
        row.BottomPadding = Unit.FromPoint(3);
        row.Borders.Bottom.Color = Line;
        row.Borders.Bottom.Width = Unit.FromPoint(0.5);
        Label(row.Cells[0], label);
        Value(row.Cells[1], string.IsNullOrWhiteSpace(value) ? "-" : value.Trim(), color);
    }

    private static Table NewGridTable(Section section, params (string Header, double Centimeters)[] columns)
    {
        var table = section.AddTable();
        table.Borders.Visible = false;
        foreach (var column in columns)
        {
            table.AddColumn(Unit.FromCentimeter(column.Centimeters));
        }

        var header = table.AddRow();
        header.HeadingFormat = true;
        header.BottomPadding = Unit.FromPoint(3);
        header.Borders.Bottom.Color = Line;
        header.Borders.Bottom.Width = Unit.FromPoint(0.75);
        for (var i = 0; i < columns.Length; i++)
        {
            Label(header.Cells[i], columns[i].Header);
        }

        return table;
    }

    private static void AddGridRow(Table table, Color? color, params string[] values)
    {
        var row = table.AddRow();
        row.TopPadding = Unit.FromPoint(2.5);
        row.BottomPadding = Unit.FromPoint(2.5);
        row.Borders.Bottom.Color = Line;
        row.Borders.Bottom.Width = Unit.FromPoint(0.5);
        for (var i = 0; i < values.Length; i++)
        {
            Value(row.Cells[i], values[i], i == values.Length - 1 ? color : null);
        }
    }

    private static void Label(Cell cell, string text)
    {
        var paragraph = cell.AddParagraph(text);
        paragraph.Format.Font.Color = Secondary;
    }

    private static void Value(Cell cell, string text, Color? color)
    {
        var paragraph = cell.AddParagraph(text);
        if (color is { } c)
        {
            paragraph.Format.Font.Color = c;
        }
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
