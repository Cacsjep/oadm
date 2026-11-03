using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Events;
using Oadm.Plugins.Ocr.Model;

namespace Oadm.Plugins.Ocr.Readers;

/// <summary>One reading of a reader with what the events and the page need beyond <see cref="Reading"/>.</summary>
/// <param name="Reading">Every area's result.</param>
/// <param name="Details">Every area's further values with the cut-outs, in the same order.</param>
/// <param name="Jpeg">The picture (memory only).</param>
public sealed record ReaderReading(Reading Reading, IReadOnlyList<AreaDetails> Details, ReadOnlyMemory<byte> Jpeg);

/// <summary>
/// Turns one picture into a reading: recognition per area, then the area's value from its lines. No cut-outs here: they are
/// cut only when a page asks for the reading (<see cref="ReaderRuntime.ReplyOf"/>), never per frame.
/// </summary>
internal sealed class ReadingMaker(ITextRecognizer recognizer, ITextMatcher matcher, TimeProvider time, ReaderStats? stats = null)
{
    /// <summary>Reads <paramref name="jpeg"/> with the areas of <paramref name="reader"/>.</summary>
    /// <param name="reuseSource">
    /// Watching and previews: who reads (the reader's or the preview's id); an area whose pixels have barely changed since that
    /// source's last reading gives that reading again without the models (docs/spec/ocr.md "Unchanged areas"). Null (the
    /// action's single reading): every area is read.
    /// </param>
    public async Task<ReaderReading> ReadAsync(Reader reader, ReadOnlyMemory<byte> jpeg, string? reuseSource, CancellationToken ct)
    {
        var taken = time.GetUtcNow();
        var areas = reader.Areas;
        if (areas.Count == 0)
        {
            // Text is read only inside areas: no area, no reading (the picture is not even decoded).
            return new ReaderReading(new Reading { ReaderId = reader.Id, Time = taken, Resolution = reader.Resolution }, [], jpeg);
        }

        var started = time.GetTimestamp();
        var picture = reuseSource is not null && recognizer is Recognition.IDetailedTextRecognizer detailed
            ? (await detailed.ReadDetailedAsync(jpeg, areas, new Recognition.RecognitionOptions { ReuseUnchanged = true, Source = reuseSource }, ct).ConfigureAwait(false)).ToPicture()
            : await recognizer.ReadAsync(jpeg, areas, ct).ConfigureAwait(false);
        if (picture.ReusedAreas >= areas.Count)
        {
            // Every area unchanged: no model ran, the picture is counted apart (pictures per second stays the real readings).
            stats?.CountReused();
        }
        else
        {
            stats?.CountPicture(time.GetElapsedTime(started));
        }

        var results = new List<AreaResult>(areas.Count);
        var details = new List<AreaDetails>(areas.Count);
        for (var i = 0; i < areas.Count; i++)
        {
            var lines = i < picture.LinesPerArea.Count ? picture.LinesPerArea[i] : [];
            var (result, detail) = Area(areas[i], lines);
            results.Add(result);
            details.Add(detail with { TooSmall = i < picture.TooSmallPerArea.Count && picture.TooSmallPerArea[i] });
        }

        var reading = new Reading
        {
            ReaderId = reader.Id,
            Time = taken,
            Resolution = picture.Resolution,
            Areas = results,
            Note = picture.TooManyTexts ? ReaderRules.TooManyTexts : null,
        };
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
