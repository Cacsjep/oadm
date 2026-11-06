using System.Text;

using Oadm.Plugins.Ocr.Model;

namespace Oadm.Plugins.Ocr.Matching;

/// <summary>
/// The rows of an area's lines and their joined texts (docs/spec/ocr.md "Several lines"): what stands on one line of the text
/// is one row, at any turn of the text. Only arithmetic on the lines' rotated boxes (<see cref="TextLine.Turn"/>), no model:
/// <list type="number">
/// <item>Blocks by direction: the longest line sets a direction, every line within 15 degrees of it, either way round, joins
/// its block; a short line has no reliable direction and joins the first block; the next line left over starts the next
/// block (two receipts at different angles stay apart).</item>
/// <item>One axis per block: the length-weighted mean of the directions with the angles doubled (a line has no front); its
/// side is the way most of the text length reads. Never a line's own direction: 2 degrees are 17 px at 500 px.</item>
/// <item>Rows: two lines share a row when their centres lie, across the axis, within 0.55 of the smaller line's height, or
/// when the smaller one lies on the larger one's height, at most a quarter of it outside, and stands next to it (a small
/// line beside a large one); rows are the connected groups.</item>
/// <item>Order: rows by their mean place across the axis, the lines of a row along it.</item>
/// <item>Duplicates: two lines of a row that overlap along the axis by more than half of the shorter one are one text (the
/// tile overlap); the narrower one goes.</item>
/// </list>
/// A line without a turn has only its level box: then every line of the area uses its level box. Pure and thread-safe.
/// </summary>
public static class LineOrder
{
    /// <summary>The events' texts are cut to this many characters.</summary>
    public const int MaxEventChars = 500;

    /// <summary>Lines within this angle of a block's first line, either way round, join the block.</summary>
    private const double BlockAngle = 15 * Math.PI / 180;

    /// <summary>A line shorter than this many times its height has no reliable direction.</summary>
    private const double ShortLine = 1.5;

    /// <summary>Two lines share a row when their centres lie within this share of the smaller line's height across the axis.</summary>
    private const double RowDistance = 0.55;

    /// <summary>
    /// Two lines also share a row when the smaller one lies on the larger one's height with at most this share of its own
    /// height outside, and at most the larger height away along the axis. It only adds lines more than 1.6 times as high
    /// as their neighbour, never two lines of one size.
    /// </summary>
    private const double RowOutside = 0.25;

    /// <summary>Two lines of a row that overlap along the axis by more than this share of the shorter one are one text.</summary>
    private const double DuplicateOverlap = 0.5;

    /// <summary>Two blocks lie side by side when they overlap top to bottom by at least this share of the smaller one.</summary>
    private const double BlocksSideBySide = 0.5;

    /// <summary><paramref name="lines"/> in reading order, row by row; lines without text are left out, every other line stays.</summary>
    public static IReadOnlyList<TextLine> Order(IReadOnlyList<TextLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var rows = RowsOf(lines, dropDuplicates: false);
        var ordered = new List<TextLine>(lines.Count);
        foreach (var row in rows)
        {
            ordered.AddRange(row);
        }

        return ordered;
    }

    /// <summary>
    /// The rows of <paramref name="lines"/> in reading order, each with its lines in reading order; lines without text and
    /// duplicates are left out.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<TextLine>> Rows(IReadOnlyList<TextLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return RowsOf(lines, dropDuplicates: true);
    }

    /// <summary>
    /// <paramref name="ordered"/> joined to one line: a space between lines, none after a line ending in "-" ("DECAF-" and
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

    /// <summary>
    /// <paramref name="rows"/> as text: one row per line, the lines of a row joined with a space; at most
    /// <see cref="MaxEventChars"/> characters.
    /// </summary>
    public static string JoinRows(IReadOnlyList<IReadOnlyList<TextLine>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var text = new StringBuilder();
        foreach (var row in rows)
        {
            var start = text.Length;
            foreach (var line in row)
            {
                var part = line.Text.AsSpan().Trim();
                if (part.Length == 0)
                {
                    continue;
                }

                if (text.Length > start)
                {
                    text.Append(' ');
                }
                else if (start > 0)
                {
                    text.Append('\n');
                }

                text.Append(part);
            }

            if (text.Length >= MaxEventChars)
            {
                break;
            }
        }

        return text.Length > MaxEventChars ? text.ToString(0, MaxEventChars).TrimEnd() : text.ToString();
    }

    private static List<List<TextLine>> RowsOf(IReadOnlyList<TextLine> lines, bool dropDuplicates)
    {
        var placed = Place(lines);
        var rows = new List<List<TextLine>>();
        if (placed.Length == 0)
        {
            return rows;
        }

        foreach (var block in Blocks(placed))
        {
            AddRows(placed, block, dropDuplicates, rows);
        }

        return rows;
    }

    /// <summary>
    /// The lines with text and how each lies: from its turn (picture pixels), or, when a line has none, every line from its
    /// level box (relative to the picture, direction left to right).
    /// </summary>
    private static Placed[] Place(IReadOnlyList<TextLine> lines)
    {
        var count = 0;
        var turned = true;
        foreach (var line in lines)
        {
            if (!string.IsNullOrWhiteSpace(line.Text))
            {
                count++;
                turned &= line.Turn is not null;
            }
        }

        var placed = new Placed[count];
        var i = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line.Text))
            {
                continue;
            }

            placed[i++] = turned && line.Turn is { } turn
                ? new Placed(line, turn.CenterX, turn.CenterY, turn.Width, turn.Height, turn.Angle)
                : new Placed(line, line.Box.X + (line.Box.Width / 2), line.Box.Y + (line.Box.Height / 2), line.Box.Width, line.Box.Height, 0);
        }

        return placed;
    }

    /// <summary>
    /// The blocks, each a list of indexes into <paramref name="placed"/>, in reading order: top to bottom in the picture,
    /// blocks side by side left to right (the order never depends on which block holds the longest line).
    /// </summary>
    private static List<Block> Blocks(Placed[] placed)
    {
        // Longest first; equal lengths in their given order, so the result never depends on a sort's whim.
        var byLength = new int[placed.Length];
        for (var i = 0; i < byLength.Length; i++)
        {
            byLength[i] = i;
        }

        Array.Sort(byLength, (a, b) => placed[a].Along != placed[b].Along ? placed[b].Along.CompareTo(placed[a].Along) : a.CompareTo(b));

        var blocks = new List<Block>();
        var taken = new bool[placed.Length];
        foreach (var seed in byLength)
        {
            if (taken[seed] || placed[seed].IsShort)
            {
                continue;
            }

            var block = new Block();
            foreach (var i in byLength)
            {
                if (!taken[i] && !placed[i].IsShort && Between(placed[i].Angle, placed[seed].Angle) <= BlockAngle)
                {
                    taken[i] = true;
                    block.Lines.Add(i);
                }
            }

            blocks.Add(block);
        }

        if (blocks.Count == 0)
        {
            blocks.Add(new Block()); // only short lines: one block, its axis from all of them
        }

        var hasLong = blocks[0].Lines.Count > 0;
        foreach (var i in byLength)
        {
            if (!taken[i])
            {
                blocks[0].Lines.Add(i);
            }
        }

        foreach (var block in blocks)
        {
            block.Axis = AxisOf(placed, block.Lines, longOnly: hasLong);
            foreach (var i in block.Lines)
            {
                var line = placed[i];
                var reach = ((Math.Abs(line.Along * Math.Sin(line.Angle)) + Math.Abs(line.Across * Math.Cos(line.Angle))) / 2);
                block.Top = Math.Min(block.Top, line.Y - reach);
                block.Bottom = Math.Max(block.Bottom, line.Y + reach);
                block.X += line.X / block.Lines.Count;
            }
        }

        return blocks.Count == 1 ? blocks : InPictureOrder(blocks);
    }

    /// <summary>Top to bottom; blocks that overlap top to bottom by half of the smaller one lie side by side: left to right.</summary>
    private static List<Block> InPictureOrder(List<Block> blocks)
    {
        blocks.Sort((a, b) => a.Top != b.Top ? a.Top.CompareTo(b.Top) : a.X.CompareTo(b.X));
        var ordered = new List<Block>(blocks.Count);
        var band = new List<Block>();
        double top = 0, bottom = 0;
        foreach (var block in blocks)
        {
            var overlap = Math.Min(bottom, block.Bottom) - Math.Max(top, block.Top);
            if (band.Count > 0 && overlap >= BlocksSideBySide * Math.Min(bottom - top, block.Bottom - block.Top))
            {
                band.Add(block);
                bottom = Math.Max(bottom, block.Bottom);
                continue;
            }

            Flush();
            band.Add(block);
            (top, bottom) = (block.Top, block.Bottom);
        }

        Flush();
        return ordered;

        void Flush()
        {
            band.Sort((a, b) => a.X.CompareTo(b.X));
            ordered.AddRange(band);
            band.Clear();
        }
    }

    /// <summary>
    /// The direction a block reads in: the length-weighted mean of its lines' directions with the angles doubled, turned to the
    /// side most of the text length reads to (a line of digits read the wrong way round does not turn the block).
    /// </summary>
    private static double AxisOf(Placed[] placed, List<int> lines, bool longOnly)
    {
        double c = 0, s = 0;
        foreach (var i in lines)
        {
            if (longOnly && placed[i].IsShort)
            {
                continue;
            }

            c += placed[i].Along * Math.Cos(2 * placed[i].Angle);
            s += placed[i].Along * Math.Sin(2 * placed[i].Angle);
        }

        var axis = Math.Atan2(s, c) / 2;
        double forward = 0;
        foreach (var i in lines)
        {
            if (!longOnly || !placed[i].IsShort)
            {
                forward += placed[i].Along * Math.Sign(Math.Cos(placed[i].Angle - axis));
            }
        }

        return forward < 0 ? axis + Math.PI : axis;
    }

    /// <summary>The rows of one block, in order, added to <paramref name="rows"/>.</summary>
    private static void AddRows(Placed[] placed, Block block, bool dropDuplicates, List<List<TextLine>> rows)
    {
        var lines = block.Lines;
        var n = lines.Count;
        var cos = Math.Cos(block.Axis);
        var sin = Math.Sin(block.Axis);
        var along = new double[n];  // the centre along the axis
        var across = new double[n]; // the centre across the axis
        var width = new double[n];  // the line's size along the axis
        var height = new double[n]; // the line's size across the axis
        for (var k = 0; k < n; k++)
        {
            var line = placed[lines[k]];
            along[k] = (line.X * cos) + (line.Y * sin);
            across[k] = (-line.X * sin) + (line.Y * cos);

            // A short line lying across the axis (its direction is not reliable) is as high as it is long.
            var turned = Math.Abs(Math.Sin(line.Angle - block.Axis)) > Math.Abs(Math.Cos(line.Angle - block.Axis));
            (width[k], height[k]) = turned ? (line.Across, line.Along) : (line.Along, line.Across);
        }

        // Rows: the connected groups of "the centres lie within 0.55 of the smaller height" (at most 30 lines: 435 pairs).
        var row = new int[n];
        for (var k = 0; k < n; k++)
        {
            row[k] = k;
        }

        for (var a = 0; a < n; a++)
        {
            for (var b = a + 1; b < n; b++)
            {
                if (SameRow(a, b))
                {
                    var (ra, rb) = (Find(row, a), Find(row, b));
                    if (ra != rb)
                    {
                        row[Math.Max(ra, rb)] = Math.Min(ra, rb);
                    }
                }
            }
        }

        // Each row's lines along the axis, the rows by their mean place across it.
        var order = new int[n];
        var place = new double[n];
        var sizes = new int[n];
        for (var k = 0; k < n; k++)
        {
            order[k] = k;
            var r = Find(row, k);
            place[r] += across[k];
            sizes[r]++;
        }

        Array.Sort(order, (a, b) =>
        {
            var (ra, rb) = (Find(row, a), Find(row, b));
            if (ra != rb)
            {
                var (pa, pb) = (place[ra] / sizes[ra], place[rb] / sizes[rb]);
                return pa != pb ? pa.CompareTo(pb) : ra.CompareTo(rb);
            }

            return along[a] != along[b] ? along[a].CompareTo(along[b]) : a.CompareTo(b);
        });

        var start = 0;
        while (start < n)
        {
            var end = start + 1;
            while (end < n && Find(row, order[end]) == Find(row, order[start]))
            {
                end++;
            }

            var texts = new List<TextLine>(end - start);
            for (var k = start; k < end; k++)
            {
                if (!dropDuplicates || !IsDuplicate(order[k], start, end))
                {
                    texts.Add(placed[lines[order[k]]].Line);
                }
            }

            rows.Add(texts);
            start = end;
        }

        // On one row: the centres lie close, or the smaller line lies on the larger one's height and stands next to it, at
        // most the larger height away (a small caption on the baseline of a large value, a raised small number). Lines of
        // one size only ever meet the first: a looser rule would join a far price with the row above or below it, and
        // without "next to it" one high box found by mistake would join every row beside it.
        bool SameRow(int a, int b)
        {
            var distance = Math.Abs(across[a] - across[b]);
            var smaller = Math.Min(height[a], height[b]);
            if (distance <= RowDistance * smaller)
            {
                return true;
            }

            var larger = Math.Max(height[a], height[b]);
            var gap = Math.Abs(along[a] - along[b]) - ((width[a] + width[b]) / 2);
            return distance <= (larger / 2) - ((0.5 - RowOutside) * smaller) && gap <= larger;
        }

        // One text found twice: a wider line of the row, itself on this line's height, covers more than half of this one
        // (of two equal ones the later goes).
        bool IsDuplicate(int line, int from, int to)
        {
            for (var k = from; k < to; k++)
            {
                var other = order[k];
                if (other == line || !SameRow(line, other))
                {
                    continue;
                }

                var wider = width[other] > width[line] || (width[other] == width[line] && Earlier(other, line));
                var overlap = Math.Min(along[line] + (width[line] / 2), along[other] + (width[other] / 2))
                    - Math.Max(along[line] - (width[line] / 2), along[other] - (width[other] / 2));
                if (wider && overlap > DuplicateOverlap * width[line])
                {
                    return true;
                }
            }

            return false;
        }

        bool Earlier(int a, int b) => along[a] != along[b] ? along[a] < along[b] : a < b;
    }

    private static int Find(int[] parent, int i)
    {
        while (parent[i] != i)
        {
            parent[i] = parent[parent[i]];
            i = parent[i];
        }

        return i;
    }

    /// <summary>The angle between two line directions (a line has no front: 0 to 90 degrees).</summary>
    private static double Between(double a, double b)
    {
        var d = Math.Abs(a - b) % Math.PI;
        return Math.Min(d, Math.PI - d);
    }

    /// <summary>A line with its centre, its size along and across the text and the direction of the text.</summary>
    private readonly record struct Placed(TextLine Line, double X, double Y, double Along, double Across, double Angle)
    {
        public bool IsShort => Along < ShortLine * Across;
    }

    /// <summary>The lines of one direction, the direction they read in and where the block lies in the picture.</summary>
    private sealed class Block
    {
        public List<int> Lines { get; } = [];

        public double Axis { get; set; }

        public double Top { get; set; } = double.MaxValue;

        public double Bottom { get; set; } = double.MinValue;

        public double X { get; set; }
    }
}
