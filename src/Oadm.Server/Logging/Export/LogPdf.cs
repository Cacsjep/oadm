using System.Globalization;

using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;

using PdfSharp.Fonts;

namespace Oadm.Server.Logging.Export;

/// <summary>The rendered PDF and its page count.</summary>
public sealed record RenderedLogPdf(byte[] Pdf, int Pages);

/// <summary>
/// The PDF of an exported log with MigraDoc (PDFsharp, MIT): A4 landscape, Roboto embedded (<see cref="LogPdfFonts"/>, never a
/// system font, so it looks the same on every system), a head with the log, the filter, the server and who exported it
/// when, then the table with its header row on every page, and "Page 1 of 3" in the footer.
/// </summary>
public static class LogPdf
{
    private static readonly Color Text = new(0x20, 0x20, 0x20);
    private static readonly Color Secondary = new(0x6B, 0x6B, 0x6B);
    private static readonly Color Line = new(0xD9, 0xD9, 0xD9);
    private static readonly Color Surface = new(0xF2, 0xF2, 0xF2);

    /// <summary>Width of the table: A4 landscape (29.7 cm) less the margins.</summary>
    private static readonly Unit TableWidth = Unit.FromCentimeter(29.7 - 2 * 1.5);

    public static RenderedLogPdf Render(LogTable table, LogExportHead head, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(head);
        LogPdfFonts.EnsureInstalled();
        var document = Build(table, head, ct);
        ct.ThrowIfCancellationRequested();
        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        renderer.PdfDocument.Options.CompressContentStreams = true;
        var pages = renderer.PdfDocument.PageCount;
        using var stream = new MemoryStream();
        renderer.PdfDocument.Save(stream, closeStream: false);
        return new RenderedLogPdf(stream.ToArray(), pages);
    }

    private static Document Build(LogTable table, LogExportHead head, CancellationToken ct)
    {
        var document = new Document();
        document.Info.Title = head.LogName;
        document.Info.Author = head.ExportedBy;
        document.Info.Subject = "OADM " + head.LogName;

        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = LogPdfFonts.Family;
        normal.Font.Size = 8;
        normal.Font.Color = Text;
        var footer = document.Styles[StyleNames.Footer]!;
        footer.Font.Size = 7.5;
        footer.Font.Color = Secondary;

        var section = document.AddSection();
        var setup = document.DefaultPageSetup.Clone();
        // A4 landscape as a wide page (not Orientation.Landscape, which keeps a portrait page and rotates it in some viewers).
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
        footRow.Cells[0].AddParagraph(head.LogName + " · " + head.ServerName);
        var pages = footRow.Cells[1].AddParagraph();
        pages.Format.Alignment = ParagraphAlignment.Right;
        pages.AddText("Page ");
        pages.AddPageField();
        pages.AddText(" of ");
        pages.AddNumPagesField();

        var title = section.AddParagraph(head.LogName);
        title.Format.Font.Size = 16;
        title.Format.Font.Bold = true;
        title.Format.SpaceAfter = Unit.FromPoint(4);
        foreach (var line in HeadLines(head))
        {
            var p = section.AddParagraph(line);
            p.Format.Font.Color = Secondary;
            p.Format.Font.Size = 8.5;
        }

        section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(6);

        var columns = table.Columns.Select((c, i) => (Column: c, Index: i)).Where(c => c.Column.InPdf).ToList();
        var share = columns.Sum(c => c.Column.Width);
        var grid = section.AddTable();
        grid.Borders.Visible = false;
        grid.LeftPadding = Unit.FromPoint(3);
        grid.RightPadding = Unit.FromPoint(3);
        grid.TopPadding = Unit.FromPoint(2);
        grid.BottomPadding = Unit.FromPoint(2);
        foreach (var (column, _) in columns)
        {
            grid.AddColumn(TableWidth * (column.Width / share));
        }

        var header = grid.AddRow();
        header.HeadingFormat = true;
        header.Shading.Color = Surface;
        header.Format.Font.Bold = true;
        for (var i = 0; i < columns.Count; i++)
        {
            header.Cells[i].AddParagraph(columns[i].Column.Header);
        }

        for (var r = 0; r < table.Rows.Count; r++)
        {
            if ((r & 255) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            var cells = table.Rows[r];
            var row = grid.AddRow();
            row.Borders.Bottom.Color = Line;
            row.Borders.Bottom.Width = Unit.FromPoint(0.5);
            for (var i = 0; i < columns.Count; i++)
            {
                var index = columns[i].Index;
                row.Cells[i].AddParagraph(Breakable(index < cells.Length ? cells[index] : string.Empty));
            }
        }

        if (table.Rows.Count == 0)
        {
            section.AddParagraph("No entries.");
        }

        return document;
    }

    /// <summary>The lines under the title: the filter, the count, the server, who exported it when.</summary>
    public static IReadOnlyList<string> HeadLines(LogExportHead head)
    {
        ArgumentNullException.ThrowIfNull(head);
        var lines = new List<string>(head.Filter);
        lines.Add(head.Limited
            ? string.Create(CultureInfo.InvariantCulture, $"The newest {head.Entries:N0} entries")
            : head.Entries == 1 ? "1 entry" : string.Create(CultureInfo.InvariantCulture, $"{head.Entries:N0} entries"));
        lines.Add("Server: " + head.ServerName);
        lines.Add(string.Create(CultureInfo.InvariantCulture, $"Exported {head.ExportedAt:yyyy-MM-dd HH:mm} by {head.ExportedBy}"));
        return lines;
    }

    /// <summary>
    /// A cell text MigraDoc can wrap: it breaks lines only at spaces, so a zero-width space follows / . , ; : = &amp; after
    /// every 30 characters without a space (paths, addresses) and control characters become spaces.
    /// </summary>
    internal static string Breakable(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder(text.Length + 8);
        var run = 0;
        foreach (var c in text)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                sb.Append(' ');
                run = 0;
                continue;
            }

            sb.Append(c);
            run++;
            if (run >= 30 && c is '/' or '\\' or '.' or ',' or ';' or ':' or '=' or '&' or '-' or '_' or '?')
            {
                sb.Append('​');
                run = 0;
            }
            else if (run >= 60)
            {
                sb.Append('​');
                run = 0;
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// The font resolver of the log PDF: Roboto Regular and Bold embedded in the server (Apache-2.0, the files of the Snapshot
/// report plugin). Every family resolves to Roboto, so no system font is ever read. PDFsharp keeps the resolver in a static:
/// installed once.
/// </summary>
public sealed class LogPdfFonts : IFontResolver
{
    public const string Family = "Roboto";

    private const string Regular = "Roboto-Regular";
    private const string Bold = "Roboto-Bold";

    private static readonly Lock Sync = new();

    public static void EnsureInstalled()
    {
        lock (Sync)
        {
            if (GlobalFontSettings.FontResolver is not LogPdfFonts)
            {
                GlobalFontSettings.FontResolver = new LogPdfFonts();
            }
        }
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic) =>
        new(bold ? Bold : Regular, mustSimulateBold: false, mustSimulateItalic: italic);

    public byte[]? GetFont(string faceName)
    {
        var name = faceName == Bold ? Bold : Regular;
        using var stream = typeof(LogPdfFonts).Assembly.GetManifestResourceStream($"Oadm.Server.Logging.Export.Fonts.{name}.ttf")
            ?? throw new InvalidOperationException($"Font {name} is missing in the server assembly.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
