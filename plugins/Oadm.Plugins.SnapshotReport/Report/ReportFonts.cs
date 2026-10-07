using PdfSharp.Fonts;

namespace Oadm.Plugins.SnapshotReport.Report;

/// <summary>
/// Font resolver of the PDF report: Roboto Regular and Bold embedded in the plugin (Apache-2.0), so the
/// report looks the same on Windows, Linux and macOS and never depends on installed fonts. Every family
/// name resolves to Roboto.
/// </summary>
public sealed class ReportFonts : IFontResolver
{
    public const string Family = "Roboto";

    private const string Regular = "Roboto-Regular";
    private const string Bold = "Roboto-Bold";

    private static readonly Lock Sync = new();

    /// <summary>Installs the resolver once per load context (PDFsharp keeps it in a static).</summary>
    public static void EnsureInstalled()
    {
        lock (Sync)
        {
            if (GlobalFontSettings.FontResolver is not ReportFonts)
            {
                GlobalFontSettings.FontResolver = new ReportFonts();
            }
        }
    }

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic) =>
        new(bold ? Bold : Regular, mustSimulateBold: false, mustSimulateItalic: italic);

    public byte[]? GetFont(string faceName)
    {
        var name = faceName == Bold ? Bold : Regular;
        using var stream = typeof(ReportFonts).Assembly.GetManifestResourceStream($"Oadm.Plugins.SnapshotReport.Fonts.{name}.ttf")
            ?? throw new InvalidOperationException($"Font {name} is missing in the plugin assembly.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
