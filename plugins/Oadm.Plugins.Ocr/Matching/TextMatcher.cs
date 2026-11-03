using Oadm.Plugins.Ocr.Model;

namespace Oadm.Plugins.Ocr.Matching;

/// <summary>
/// Makes an area's value from its lines (docs/spec/ocr.md "Several lines", "Expected text", "Events and the rule action").
/// Orders the lines (<see cref="LineOrder"/>); without a format the value is all lines joined; with one the matches are
/// looked for in each line on its own, then in neighbouring lines joined (a number split over two lines). Of several
/// matches the value is the one read with the highest confidence, on a tie the larger text. Pure and thread-safe.
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
        var ordered = LineOrder.Order(lines);
        var texts = ordered.Select(l => l.Text.Trim()).ToList();
        var textRead = LineOrder.JoinForEvents(texts);
        var raw = string.Join('\n', texts);
        if (ordered.Count == 0)
        {
            return TextMatch.Empty;
        }

        if (expected.Kind != ExpectedTextKind.Format || string.IsNullOrWhiteSpace(expected.Format))
        {
            // Any text: the value is everything read; Matches format stays No (there is no format to match).
            return new TextMatch
            {
                Text = textRead,
                TextRead = textRead,
                RawText = raw,
                Number = NumberText.Of(textRead),
                Confidence = ordered.Min(l => l.Confidence),
                Lines = ordered,
                TextBox = Union(ordered),
            };
        }

        var format = TextFormat.Parse(expected.Format);
        var candidates = Candidates(ordered, texts, format);
        if (candidates.Count == 0)
        {
            return new TextMatch { TextRead = textRead, RawText = raw, Lines = ordered };
        }

        var best = candidates
            .OrderByDescending(c => c.Confidence)
            .ThenByDescending(c => c.TextHeight)
            .First();
        return new TextMatch
        {
            Text = best.Value,
            TextRead = textRead,
            RawText = raw,
            AllMatches = [.. candidates.Select(c => c.Value).Distinct(StringComparer.Ordinal).Take(MaxMatches)],
            Number = NumberText.Of(best.Value),
            Confidence = best.Confidence,
            MatchesFormat = true,
            Lines = ordered,
            TextBox = best.Box,
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
                found.Add((i, m.Start, new Candidate(m.Value, line.Confidence, line.Box.Height, line.Box)));
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
                            Union([a, b]))));
                    }
                }
            }
        }

        return [.. found.OrderBy(f => f.Line).ThenBy(f => f.Start).Select(f => f.Match).DistinctBy(c => (c.Value, c.Box))];
    }

    private static RelativeBox? Union(IReadOnlyList<TextLine> lines)
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

    private sealed record Candidate(string Value, double Confidence, double TextHeight, RelativeBox? Box);
}
