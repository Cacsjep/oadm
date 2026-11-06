using Oadm.Plugins.Ocr.Model;

namespace Oadm.Plugins.Ocr.Matching;

/// <summary>
/// Makes an area's value from its lines (docs/spec/ocr.md "Several lines", "Expected text", "Events and the rule action").
/// Puts the lines in rows (<see cref="LineOrder"/>); without a format the value is all lines joined to one line; with one the
/// matches are looked for in each line on its own, then in neighbouring lines joined (a number split over two lines). Of
/// several matches the value is the one read with the highest confidence, on a tie the larger text. The value never holds a
/// line break; the raw text holds the rows. Pure and thread-safe.
/// </summary>
public sealed class TextMatcher : ITextMatcher
{
    /// <summary>The event field All matches holds at most this many different matches.</summary>
    public const int MaxMatches = 10;

    /// <inheritdoc />
    public AreaValue Match(IReadOnlyList<TextLine> lines, ExpectedText expected) => MatchAll(lines, expected).ToAreaValue();

    /// <summary>Every event field and the ordered lines of <paramref name="lines"/> for <paramref name="expected"/>.</summary>
    public static TextMatch MatchAll(IReadOnlyList<TextLine> lines, ExpectedText expected)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(expected);
        var rows = LineOrder.Rows(lines);
        if (rows.Count == 0)
        {
            return TextMatch.Empty;
        }

        var raw = LineOrder.JoinRows(rows);
        if (expected.Kind != ExpectedTextKind.Format || string.IsNullOrWhiteSpace(expected.Format))
        {
            // Any text: the value is everything read, on one line; Matches format stays No (there is no format to match).
            var read = new List<TextLine>(lines.Count);
            foreach (var row in rows)
            {
                read.AddRange(row);
            }

            var text = LineOrder.JoinForEvents(read.Select(l => l.Text));
            return new TextMatch
            {
                Text = text,
                RawText = raw,
                Number = NumberText.Of(text),
                Confidence = read.Min(l => l.Confidence),
                Lines = read,
                TextBox = Union(read),
                Turn = read.Count == 1 ? read[0].Turn : null,
            };
        }

        // A format looks at every line found, also at one the rows left out as found twice: a tile's cut piece can be the
        // wider one and the whole value the narrower.
        var ordered = LineOrder.Order(lines);
        var texts = ordered.Select(l => l.Text.Trim()).ToList();
        var format = TextFormat.Parse(expected.Format);
        var candidates = Candidates(ordered, texts, format);
        if (candidates.Count == 0)
        {
            return new TextMatch { RawText = raw, Lines = ordered };
        }

        var best = candidates
            .OrderByDescending(c => c.Confidence)
            .ThenByDescending(c => c.TextHeight)
            .First();
        return new TextMatch
        {
            Text = best.Value,
            RawText = raw,
            AllMatches = [.. candidates.Select(c => c.Value).Distinct(StringComparer.Ordinal).Take(MaxMatches)],
            Number = NumberText.Of(best.Value),
            Confidence = best.Confidence,
            MatchesFormat = true,
            Lines = ordered,
            TextBox = best.Box,
            Turn = best.Turn,
        };
    }

    /// <summary>The matches in reading order: per line, then across each pair of neighbouring lines.</summary>
    private static List<Candidate> Candidates(IReadOnlyList<TextLine> ordered, List<string> texts, TextFormat format)
    {
        var found = new List<(int Line, int Start, Candidate Match)>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var line = ordered[i];
            foreach (var m in format.FindAll(texts[i]))
            {
                found.Add((i, m.Start, new Candidate(m.Value, line.Confidence, line.Box.Height, line.Box, line.Turn)));
            }
        }

        // A value split over two lines: only matches that take characters from both lines count here; the rest were
        // found above. Joined with and without a space ("MSCU 123" + "4565").
        for (var i = 0; i + 1 < ordered.Count; i++)
        {
            foreach (var separator in (string[])["", " "])
            {
                var joined = texts[i] + separator + texts[i + 1];
                var seam = texts[i].Length;
                foreach (var m in format.FindAll(joined))
                {
                    if (m.Start < seam && m.Start + m.Length > seam + separator.Length)
                    {
                        var (a, b) = (ordered[i], ordered[i + 1]);
                        found.Add((i, m.Start, new Candidate(
                            m.Value,
                            Math.Min(a.Confidence, b.Confidence),
                            Math.Max(a.Box.Height, b.Box.Height),
                            Union([a, b]),
                            null))); // two lines have no one direction: the cut-out stays level
                    }
                }
            }
        }

        return [.. found.OrderBy(f => f.Line).ThenBy(f => f.Start).Select(f => f.Match).DistinctBy(c => (c.Value, c.Box))];
    }

    private static RelativeBox? Union(List<TextLine> lines)
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

    private sealed record Candidate(string Value, double Confidence, double TextHeight, RelativeBox? Box, LineTurn? Turn);
}
