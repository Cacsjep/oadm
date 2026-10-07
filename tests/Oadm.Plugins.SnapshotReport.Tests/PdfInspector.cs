using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using PdfSharp.Pdf;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.IO;

namespace Oadm.Plugins.SnapshotReport.Tests;

/// <summary>Reads a produced PDF with PDFsharp: pages, text (via the fonts' ToUnicode maps) and embedded images.</summary>
internal sealed partial class PdfInspector
{
    private PdfInspector(PdfDocument document)
    {
        Document = document;
    }

    public PdfDocument Document { get; }

    public int PageCount => Document.PageCount;

    public static PdfInspector Open(byte[] pdf)
    {
        using var stream = new MemoryStream(pdf);
        return new PdfInspector(PdfReader.Open(stream, PdfDocumentOpenMode.Import));
    }

    /// <summary>
    /// All text of a page. MigraDoc writes one run per word (and splits after hyphens), so runs are joined with
    /// single spaces and "- " is folded back to "-".
    /// </summary>
    public string PageText(int index)
    {
        var page = Document.Pages[index];
        var fonts = new Dictionary<string, Dictionary<int, string>>(StringComparer.Ordinal);
        var fontResources = page.Resources.Elements.GetDictionary("/Font");
        if (fontResources is not null)
        {
            foreach (var name in fontResources.Elements.Keys)
            {
                var font = fontResources.Elements.GetDictionary(name);
                var toUnicode = font?.Elements.GetDictionary("/ToUnicode");
                fonts[name] = toUnicode?.Stream is { } s ? ParseCMap(Encoding.ASCII.GetString(s.UnfilteredValue)) : [];
            }
        }

        var text = new StringBuilder();
        Dictionary<int, string> current = [];
        foreach (var item in Flatten(ContentReader.ReadContent(page)))
        {
            if (item is not COperator op)
            {
                continue;
            }

            switch (op.OpCode.Name)
            {
                case "Tf" when op.Operands.Count > 0 && op.Operands[0] is CName font:
                    current = fonts.TryGetValue(font.Name, out var map) ? map : [];
                    break;
                case "Tj" or "'" or "\"":
                    foreach (var s in op.Operands.OfType<CString>())
                    {
                        text.Append(Decode(s, current));
                    }

                    text.Append('\n');
                    break;
                case "TJ":
                    foreach (var array in op.Operands.OfType<CArray>())
                    {
                        foreach (var s in array.OfType<CString>())
                        {
                            text.Append(Decode(s, current));
                        }
                    }

                    text.Append('\n');
                    break;
            }
        }

        var words = text.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return HyphenBreak().Replace(string.Join(' ', words), "$1-");
    }

    /// <summary>The image XObjects (also inside form XObjects) of every page, with their filter and raw bytes.</summary>
    public IReadOnlyList<(int Page, string? Filter, byte[] Data)> Images()
    {
        var result = new List<(int, string?, byte[])>();
        for (var i = 0; i < Document.PageCount; i++)
        {
            Collect(Document.Pages[i].Resources, i, result, depth: 0);
        }

        return result;
    }

    private static void Collect(PdfDictionary? resources, int page, List<(int, string?, byte[])> result, int depth)
    {
        var objects = resources?.Elements.GetDictionary("/XObject");
        if (objects is null || depth > 4)
        {
            return;
        }

        foreach (var name in objects.Elements.Keys)
        {
            var xobject = objects.Elements.GetDictionary(name);
            if (xobject is null)
            {
                continue;
            }

            var subtype = xobject.Elements.GetName("/Subtype");
            if (subtype == "/Image")
            {
                result.Add((page, xobject.Elements.GetName("/Filter"), xobject.Stream?.Value ?? []));
            }
            else if (subtype == "/Form")
            {
                Collect(xobject.Elements.GetDictionary("/Resources"), page, result, depth + 1);
            }
        }
    }

    private static IEnumerable<CObject> Flatten(CSequence sequence)
    {
        foreach (var item in sequence)
        {
            if (item is CSequence inner)
            {
                foreach (var nested in Flatten(inner))
                {
                    yield return nested;
                }
            }
            else
            {
                yield return item;
            }
        }
    }

    private static string Decode(CString value, Dictionary<int, string> map)
    {
        var raw = value.Value;
        if (map.Count == 0)
        {
            return raw;
        }

        var text = new StringBuilder();
        for (var i = 0; i + 1 < raw.Length; i += 2)
        {
            var code = (raw[i] << 8) | raw[i + 1];
            text.Append(map.TryGetValue(code, out var s) ? s : "?");
        }

        return text.ToString();
    }

    private static Dictionary<int, string> ParseCMap(string cmap)
    {
        var map = new Dictionary<int, string>();
        foreach (Match block in BfCharBlock().Matches(cmap))
        {
            foreach (Match pair in HexPair().Matches(block.Groups[1].Value))
            {
                map[int.Parse(pair.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)] = Utf16(pair.Groups[2].Value);
            }
        }

        foreach (Match block in BfRangeBlock().Matches(cmap))
        {
            foreach (Match range in HexRange().Matches(block.Groups[1].Value))
            {
                var from = int.Parse(range.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var to = int.Parse(range.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                var start = int.Parse(range.Groups[3].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                for (var code = from; code <= to; code++)
                {
                    map[code] = char.ConvertFromUtf32(start + code - from);
                }
            }
        }

        return map;
    }

    private static string Utf16(string hex)
    {
        var chars = new StringBuilder();
        for (var i = 0; i + 3 < hex.Length; i += 4)
        {
            chars.Append((char)int.Parse(hex.AsSpan(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }

        return chars.ToString();
    }

    [GeneratedRegex(@"(\S)- ")]
    private static partial Regex HyphenBreak();

    [GeneratedRegex(@"beginbfchar(.*?)endbfchar", RegexOptions.Singleline)]
    private static partial Regex BfCharBlock();

    [GeneratedRegex(@"beginbfrange(.*?)endbfrange", RegexOptions.Singleline)]
    private static partial Regex BfRangeBlock();

    [GeneratedRegex(@"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>")]
    private static partial Regex HexPair();

    [GeneratedRegex(@"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>")]
    private static partial Regex HexRange();
}
