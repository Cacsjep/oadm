using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Events;
using Oadm.Plugins.Ocr.Model;

namespace Oadm.Plugins.Ocr.Readers;

/// <summary>One reading of a reader with what the events and the page need beyond <see cref="Reading"/>.</summary>
/// <param name="Reading">Every area's result.</param>
/// <param name="Details">Every area's further values with the cut-outs, in the same order.</param>
/// <param name="Jpeg">The picture (memory only).</param>
public sealed record ReaderReading(Reading Reading, IReadOnlyList<AreaDetails> Details, ReadOnlyMemory<byte> Jpeg);

/// <summary>Turns one picture into a reading: recognition per area, then the area's value from its lines.</summary>
internal sealed class ReadingMaker(ITextRecognizer recognizer, ITextMatcher matcher, IPictureCutter? cutter, TimeProvider time)
{
    /// <summary>Reads <paramref name="jpeg"/> with the areas of <paramref name="reader"/>.</summary>
    public async Task<ReaderReading> ReadAsync(Reader reader, ReadOnlyMemory<byte> jpeg, CancellationToken ct)
    {
        var taken = time.GetUtcNow();
        var areas = ReaderRules.EffectiveAreas(reader);
        var picture = await recognizer.ReadAsync(jpeg, reader.Areas, ct).ConfigureAwait(false);
        var results = new List<AreaResult>(areas.Count);
        var details = new List<AreaDetails>(areas.Count);
        for (var i = 0; i < areas.Count; i++)
        {
            var lines = i < picture.LinesPerArea.Count ? picture.LinesPerArea[i] : [];
            var (result, detail) = Area(areas[i], lines);
            results.Add(result);
            details.Add(detail with { Cutout = detail.MatchBox is { } box && cutter is not null ? cutter.Cut(jpeg, box) : null });
        }

        var reading = new Reading { ReaderId = reader.Id, Time = taken, Resolution = picture.Resolution, Areas = results };
        return new ReaderReading(reading, details, jpeg);
    }

    /// <summary>The result and further values of one area.</summary>
    public (AreaResult Result, AreaDetails Details) Area(Area area, IReadOnlyList<TextLine> lines)
    {
        var value = matcher is IAreaValueMatcher full ? full.MatchValue(lines, area.Expected) : Fallback(lines, area.Expected);
        var result = new AreaResult
        {
            Area = area.Name,
            Text = value.Match.Value,
            TextRead = value.Match.RawText,
            Confidence = value.Match.Value.Length == 0 ? 0 : value.Confidence,
            MatchesFormat = value.Match.MatchesFormat,
            Lines = lines,
        };
        return (result, DetailsOf(result, value.AllMatches, value.MatchBox));
    }

    /// <summary>The further values of a (voted) result.</summary>
    public static AreaDetails DetailsOf(AreaResult result, IReadOnlyList<string> allMatches, RelativeBox? matchBox) => new()
    {
        Area = result.Area,
        AllMatches = string.Join(' ', allMatches.Distinct(StringComparer.Ordinal).Take(10)),
        Number = TextReadEvents.Number(TextReadEvents.Join(result.Text)),
        MatchBox = result.Text.Length == 0 ? null : matchBox,
    };

    /// <summary>
    /// Without <see cref="IAreaValueMatcher"/>: the single match (with a format), the lowest line confidence and the box
    /// around all lines.
    /// </summary>
    private AreaValue Fallback(IReadOnlyList<TextLine> lines, ExpectedText expected)
    {
        var match = matcher.Match(lines, expected);
        var matches = expected.Kind == ExpectedTextKind.Format && match.MatchesFormat && match.Value.Length > 0 ? new[] { match.Value } : [];
        var confidence = lines.Count == 0 ? 0 : lines.Min(l => l.Confidence);
        return new AreaValue(match, matches, confidence, Around(lines));
    }

    private static RelativeBox? Around(IReadOnlyList<TextLine> lines)
    {
        if (lines.Count == 0)
        {
            return null;
        }

        var left = lines.Min(l => l.Box.X);
        var top = lines.Min(l => l.Box.Y);
        var right = lines.Max(l => l.Box.X + l.Box.Width);
        var bottom = lines.Max(l => l.Box.Y + l.Box.Height);
        return new RelativeBox(left, top, right - left, bottom - top);
    }
}
