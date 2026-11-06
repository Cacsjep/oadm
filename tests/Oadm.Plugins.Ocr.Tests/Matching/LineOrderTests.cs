using Oadm.Plugins.Ocr.Matching;
using Oadm.Plugins.Ocr.Model;

namespace Oadm.Plugins.Ocr.Tests.Matching;

/// <summary>
/// The rows of an area (docs/spec/ocr.md "Several lines"), from constructed <see cref="LineTurn"/> values: a receipt laid out
/// on its own paper and turned into the picture, each line with a small error of its own direction as the text finder gives it.
/// </summary>
public sealed class LineOrderTests
{
    private const string ReceiptRows =
        "CAFE CENTRAL\nHauptstrasse 12\n2x Espresso 5.80\n1x Croissant 2.90\n1x Water 1.90\nTOTAL EUR 10.60\nORDER 23F7H\nThank you!";

    /// <summary>A line on the paper: its text, its left edge, its centre from the top and its size (pixels of the paper).</summary>
    private sealed record Printed(string Text, double Left, double Y, double Width, double Height = 22);

    /// <summary>The receipt of the accuracy set: the articles left, the prices right, 34 px from row to row.</summary>
    private static Printed[] Receipt(double priceLeft = 176) =>
    [
        new("CAFE CENTRAL", 0, 0, 132),
        new("Hauptstrasse 12", 0, 34, 165),
        new("2x Espresso", 0, 68, 121), new("5.80", priceLeft, 68, 44),
        new("1x Croissant", 0, 102, 132), new("2.90", priceLeft, 102, 44),
        new("1x Water", 0, 136, 88), new("1.90", priceLeft, 136, 44),
        new("TOTAL EUR", 0, 170, 99), new("10.60", priceLeft, 170, 55),
        new("ORDER 23F7H", 0, 238, 180, 30),
        new("Thank you!", 0, 306, 110),
    ];

    /// <summary>
    /// The paper's lines in the picture: turned by <paramref name="degrees"/> clockwise around the paper's corner laid at
    /// (<paramref name="x"/>, <paramref name="y"/>), each line's own direction off by up to <paramref name="wobble"/> degrees,
    /// in a mixed order (the text finder gives no order).
    /// </summary>
    private static List<TextLine> Laid(Printed[] paper, double degrees, double x = 900, double y = 500, double wobble = 0)
    {
        var turn = degrees * Math.PI / 180;
        var (cos, sin) = (Math.Cos(turn), Math.Sin(turn));
        var lines = new List<TextLine>();
        for (var i = 0; i < paper.Length; i++)
        {
            var line = paper[i];
            var along = line.Left + (line.Width / 2);
            var cx = x + (along * cos) - (line.Y * sin);
            var cy = y + (along * sin) + (line.Y * cos);
            var own = turn + (wobble * ((((i * 7) % 5) - 2) / 2.0) * Math.PI / 180);
            lines.Add(Turned(line.Text, cx, cy, line.Width, line.Height, own));
        }

        // Every third line first, then the rest backwards.
        return [.. lines.Where((_, i) => i % 3 == 0), .. lines.Where((_, i) => i % 3 != 0).Reverse()];
    }

    /// <summary>A line with its turn and the level box around it, relative to a full HD picture.</summary>
    private static TextLine Turned(string text, double cx, double cy, double width, double height, double angle)
    {
        var w = (Math.Abs(width * Math.Cos(angle)) + Math.Abs(height * Math.Sin(angle))) / 1920;
        var h = (Math.Abs(width * Math.Sin(angle)) + Math.Abs(height * Math.Cos(angle))) / 1080;
        return new TextLine(text, new RelativeBox((cx / 1920) - (w / 2), (cy / 1080) - (h / 2), w, h), 90)
        {
            Turn = new LineTurn((float)cx, (float)cy, (float)width, (float)height, (float)angle),
        };
    }

    private static string RowsOf(IReadOnlyList<TextLine> lines) => LineOrder.JoinRows(LineOrder.Rows(lines));

    /// <summary>The rows as printed: the lines of one height left to right.</summary>
    private static string OnPaper(Printed[] paper) =>
        string.Join('\n', paper.GroupBy(l => l.Y).OrderBy(g => g.Key).Select(g => string.Join(' ', g.OrderBy(l => l.Left).Select(l => l.Text))));

    public static TheoryData<int> Every15Degrees() => [.. Enumerable.Range(0, 24).Select(i => i * 15)];

    [Theory]
    [MemberData(nameof(Every15Degrees))]
    public void A_receipt_keeps_its_rows_at_every_turn(int degrees)
    {
        var lines = Laid(Receipt(), degrees, wobble: 2);

        Assert.Equal(ReceiptRows, RowsOf(lines));
        Assert.Equal(ReceiptRows.Replace('\n', ' '), string.Join(' ', LineOrder.Order(lines).Select(l => l.Text)));
    }

    [Fact]
    public void An_upside_down_receipt_reads_from_its_own_top()
    {
        var lines = Laid(Receipt(), 180);

        // The level boxes alone give the old, mixed order: the last row first, the price before its article.
        var byLevelBox = lines.OrderBy(l => l.Box.Y).ThenBy(l => l.Box.X).Select(l => l.Text).ToList();
        Assert.Equal("Thank you!", byLevelBox[0]);
        Assert.True(byLevelBox.IndexOf("10.60") < byLevelBox.IndexOf("TOTAL EUR"));

        Assert.Equal(ReceiptRows, RowsOf(lines));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(100)]
    [InlineData(195)]
    public void A_wide_table_keeps_its_rows_on_one_axis(int degrees)
    {
        // The price 25 characters right of its article: a line's own direction, 2 degrees off, would miss the row by 17 px,
        // more than half a line height. The block's one axis holds it.
        var lines = Laid(Receipt(priceLeft: 500), degrees, x: 800, y: 400, wobble: 2);

        Assert.Equal(ReceiptRows, RowsOf(lines));
    }

    [Fact]
    public void Two_receipts_at_different_angles_stay_apart()
    {
        var second = Receipt().Select(l => l with { Text = l.Text + "*" }).ToArray();
        var left = Laid(Receipt(), 0, x: 200, y: 300);
        var right = Laid(second, 40, x: 1100, y: 250, wobble: 2);

        Assert.Equal(ReceiptRows + "\n" + OnPaper(second), RowsOf([.. right, .. left]));
    }

    [Fact]
    public void Blocks_read_top_to_bottom_whichever_holds_the_longest_line()
    {
        // Below a level sign a label turned by 30 degrees with the longest line of the area.
        var sign = Laid([new("GATE 12", 0, 0, 160, 40)], 0, x: 600, y: 200);
        var label = Laid([new("Deliveries only until 16:00", 0, 0, 420, 30), new("Ring the bell", 0, 40, 200, 30)], 30, x: 500, y: 500);

        Assert.Equal("GATE 12\nDeliveries only until 16:00\nRing the bell", RowsOf([.. label, .. sign]));
    }

    [Fact]
    public void Lines_without_a_turn_use_their_level_boxes()
    {
        static TextLine Level(string text, double x, double y, double width = 0.2) => new(text, new RelativeBox(x, y, width, 0.05), 90);
        var lines = new[] { Level("third", 0.1, 0.50), Level("B", 0.5, 0.12, 0.05), Level("A", 0.1, 0.10), Level("fourth", 0.5, 0.505) };

        Assert.Equal("A B\nthird fourth", RowsOf(lines));

        // One line without its turn: every line of the area uses its level box (pixels and shares of the picture never mix).
        var mixed = Laid(Receipt(), 0);
        mixed[3] = mixed[3] with { Turn = null };
        Assert.Equal(ReceiptRows, RowsOf(mixed));
    }

    [Theory]
    [InlineData("5.80", 44, 0)]   // found whole in both tiles
    [InlineData(".80", 33, 5.5)]  // cut by a tile's edge: the narrower piece
    public void A_text_found_twice_is_one_text(string again, double width, double shift)
    {
        var paper = Receipt().ToList();
        paper.Insert(4, new Printed(again, 176 + shift + (shift > 0 ? 5.5 : 2), 69, width));

        foreach (var degrees in new[] { 0, 90, 200 })
        {
            var lines = Laid([.. paper], degrees);

            Assert.Equal(ReceiptRows, RowsOf(lines));
            Assert.Equal(paper.Count, LineOrder.Order(lines).Count); // the boxes all stay: one per line found
        }
    }

    [Fact]
    public void Columns_and_stacked_small_print_are_never_taken_for_duplicates()
    {
        // Two prices in one row, far apart; and beside a large number two small lines, one above the other, which overlap
        // each other along the row but are not on one line.
        var paper = new Printed[]
        {
            new("Item", 0, 0, 60), new("1.90", 200, 0, 44), new("1.90", 320, 0, 44),
            new("4711", 0, 80, 150, 60), new("kWh", 170, 70, 50, 24), new("total", 170, 92, 60, 24),
        };

        Assert.Equal("Item 1.90 1.90\n4711 kWh total", RowsOf(Laid(paper, 0)));
        Assert.Equal("Item 1.90 1.90\n4711 kWh total", RowsOf(Laid(paper, 270)));
    }

    [Fact]
    public void A_small_line_beside_a_large_one_stays_on_its_row()
    {
        // A caption on the baseline of a value more than twice as high, and a raised small number after one: their centres
        // lie further apart than half the small height, but the small line lies on the large line's height.
        var caption = new Printed[] { new("Nr.", 0, 98, 40, 24), new("4711", 50, 80, 150, 60) };
        var raised = new Printed[] { new("12", 0, 80, 70, 60), new("99", 80, 62, 30, 24), new("EUR", 120, 98, 44, 24) };

        foreach (var degrees in new[] { 0, 90, 180, 270, 15 })
        {
            Assert.Equal("Nr. 4711", RowsOf(Laid(caption, degrees)));
            Assert.Equal("12 99 EUR", RowsOf(Laid(raised, degrees)));
        }

        // A small line that only touches the large line's edge is a row of its own.
        var below = new Printed[] { new("4711", 0, 80, 150, 60), new("total", 0, 116, 60, 24) };
        Assert.Equal("4711\ntotal", RowsOf(Laid(below, 0)));
    }

    [Fact]
    public void A_digit_line_read_the_wrong_way_round_does_not_turn_the_block()
    {
        foreach (var degrees in new[] { 0, 90, 180 })
        {
            var lines = Laid(Receipt(), degrees);
            var price = lines.FindIndex(l => l.Text == "10.60");
            lines[price] = lines[price] with { Turn = lines[price].Turn!.Value with { Angle = lines[price].Turn!.Value.Angle + MathF.PI } };

            Assert.Equal(ReceiptRows, RowsOf(lines));
        }
    }

    [Fact]
    public void A_short_line_joins_the_first_block_whatever_its_own_direction()
    {
        // "2x" and "A" are hardly longer than high: the text finder's direction of such a box says nothing.
        var paper = new Printed[] { new("Espresso doppio", 60, 0, 170), new("2x", 0, 0, 26), new("Croissant", 60, 34, 100), new("A", 0, 34, 16) };
        var lines = Laid(paper, 20);
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Text.Length <= 2)
            {
                lines[i] = lines[i] with { Turn = lines[i].Turn!.Value with { Angle = lines[i].Turn!.Value.Angle + 1.2f } };
            }
        }

        Assert.Equal("2x Espresso doppio\nA Croissant", RowsOf(lines));
    }

    [Fact]
    public void Lines_without_text_are_left_out_and_nothing_gives_no_row()
    {
        Assert.Empty(LineOrder.Rows([]));
        Assert.Empty(LineOrder.Rows([new TextLine("  ", new RelativeBox(0.1, 0.1, 0.2, 0.05), 90)]));
        Assert.Equal(string.Empty, LineOrder.JoinRows([]));
    }

    [Fact]
    public void The_rows_text_is_cut_at_500_characters_and_never_ends_in_a_line_break()
    {
        static Printed[] Paper(int characters) =>
            [.. Enumerable.Range(0, 30).Select(i => new Printed(new string((char)('a' + (i % 26)), characters), 0, i * 34, 260))];

        // Rows of 30 characters: the cut falls inside the 17th row.
        var text = RowsOf(Laid(Paper(30), 0));
        Assert.Equal(LineOrder.MaxEventChars, text.Length);
        Assert.Equal(16, text.Count(c => c == '\n'));

        // Rows of 24 characters: 20 rows and their 19 breaks are 499 characters, the 500th would be a line break.
        text = RowsOf(Laid(Paper(24), 0));
        Assert.Equal(499, text.Length);
        Assert.Equal(19, text.Count(c => c == '\n'));
        Assert.EndsWith(new string('t', 24), text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_receipt_of_these_tests_is_printed_in_its_rows() => Assert.Equal(ReceiptRows, OnPaper(Receipt()));
}
