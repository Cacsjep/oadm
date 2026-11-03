using System.Text;

using Oadm.Plugins.Ocr.Model;

namespace Oadm.Plugins.Ocr.Matching;

/// <summary>
/// The reading order of an area's lines and their joined text (docs/spec/ocr.md "Several lines"): rows top to bottom (lines
/// whose boxes overlap vertically by at least half the smaller one's height share a row), in a row left to right.
/// </summary>
public static class LineOrder
{
    /// <summary>The events' texts are cut to this many characters.</summary>
    public const int MaxEventChars = 500;

    /// <summary>Two lines share a row when they overlap vertically by at least this share of the smaller line's height.</summary>
    private const double RowOverlap = 0.5;

    /// <summary><paramref name="lines"/> in reading order; lines without text are left out.</summary>
    public static IReadOnlyList<TextLine> Order(IReadOnlyList<TextLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var byTop = lines.Where(l => !string.IsNullOrWhiteSpace(l.Text))
            .OrderBy(l => l.Box.Y).ThenBy(l => l.Box.X).ToList();
        var rows = new List<Row>();
        foreach (var line in byTop)
        {
            var row = rows.Count > 0 ? rows[^1] : null;
            if (row is not null && row.Takes(line))
            {
                row.Add(line);
            }
            else
            {
                rows.Add(new Row(line));
            }
        }

        return [.. rows.SelectMany(r => r.Lines.OrderBy(l => l.Box.X))];
    }

    /// <summary>
    /// <paramref name="ordered"/> joined for the events: a space between lines, none after a line ending in "-" ("DECAF-" and
    /// "FEINATO" give "DECAF-FEINATO"); at most <see cref="MaxEventChars"/> characters.
    /// </summary>
    public static string JoinForEvents(IEnumerable<string> ordered)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        var text = new StringBuilder();
        foreach (var raw in ordered)
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (text.Length > 0 && text[^1] != '-')
            {
                text.Append(' ');
            }

            text.Append(line);
            if (text.Length >= MaxEventChars)
            {
                break;
            }
        }

        return text.Length > MaxEventChars ? text.ToString(0, MaxEventChars) : text.ToString();
    }

    private sealed class Row(TextLine first)
    {
        private double _top = first.Box.Y;
        private double _bottom = first.Box.Y + first.Box.Height;

        public List<TextLine> Lines { get; } = [first];

        public bool Takes(TextLine line)
        {
            var top = line.Box.Y;
            var bottom = line.Box.Y + line.Box.Height;
            var overlap = Math.Min(bottom, _bottom) - Math.Max(top, _top);
            var lower = Math.Min(line.Box.Height, _bottom - _top);
            return overlap > 0 && overlap >= RowOverlap * lower;
        }

        public void Add(TextLine line)
        {
            Lines.Add(line);
            _top = Math.Min(_top, line.Box.Y);
            _bottom = Math.Max(_bottom, line.Box.Y + line.Box.Height);
        }
    }
}
