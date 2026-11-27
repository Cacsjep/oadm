using PdfSharp.Fonts;

namespace Oadm.Server.Logging.Export;

/// <summary>
/// The font resolver of the log PDF: Roboto Regular and Bold embedded in the server (Apache-2.0, the files of the Snapshot
/// report plugin). Every family resolves to Roboto, so no system font is ever read. PDFsharp keeps the resolver in a static:
/// installed once. License Plate Center links this file and embeds the fonts under the same resource names.
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
