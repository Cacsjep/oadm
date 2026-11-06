using System.Diagnostics;
using System.Globalization;
using Oadm.Plugins.Ocr.Matching;
using Oadm.Plugins.Ocr.Model;
using Xunit.Abstractions;

namespace Oadm.Plugins.Ocr.Tests.Matching;

/// <summary>
/// What rows cost (docs/spec/ocr.md "Several lines"): an area with 30 lines, the most a reading has
/// (<see cref="OcrLimits.MaxTextBoxes"/>), made into its value 15 times a second, the fastest a reader watches. Arithmetic on
/// the boxes only; a small area's reading takes about 28 ms, so this must stay far below it.
/// </summary>
[Trait("Category", "Perf")]
public sealed class LineOrderSpeedTests(ITestOutputHelper output)
{
    /// <summary>One second of watching: measured 0.6 ms for the 15 values (a Debug build, 24 cores), the budget about ten times that.</summary>
    private static readonly TimeSpan BudgetPerSecond = TimeSpan.FromMilliseconds(6);

    [Fact]
    public void Thirty_lines_at_15_frames_per_second_cost_next_to_nothing()
    {
        var lines = Table(rows: 15, degrees: 20);
        Assert.Equal(OcrLimits.MaxTextBoxes, lines.Count);
        var fps = OcrLimits.FpsChoices.Max();
        Assert.Equal(15, fps);

        // Warm up, then 200 seconds of watching.
        for (var i = 0; i < 2000; i++)
        {
            TextMatcher.MatchAll(lines, ExpectedText.AnyText);
            LineOrder.Rows(lines);
        }

        const int Seconds = 200;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        TextMatch? match = null;
        for (var i = 0; i < Seconds * fps; i++)
        {
            match = TextMatcher.MatchAll(lines, ExpectedText.AnyText);
        }

        var values = Stopwatch.GetElapsedTime(started);
        var allocated = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / (double)(Seconds * fps);
        started = Stopwatch.GetTimestamp();
        for (var i = 0; i < Seconds * fps; i++)
        {
            LineOrder.Rows(lines);
        }

        var rows = Stopwatch.GetElapsedTime(started);

        Assert.Equal(15, match!.RawText.Count(c => c == '\n') + 1); // the work was done: 15 rows of an article and its price
        var perSecond = values / Seconds;
        var message = string.Create(CultureInfo.InvariantCulture,
            $"30 lines: the rows {rows.TotalMicroseconds / (Seconds * fps):0.0} µs, the whole value {values.TotalMicroseconds / (Seconds * fps):0.0} µs and {allocated / 1024:0.0} KB per reading; 15 readings a second: {perSecond.TotalMilliseconds:0.000} ms (budget {BudgetPerSecond.TotalMilliseconds:0} ms)");
        output.WriteLine(message);
        Assert.True(perSecond < BudgetPerSecond, message);
    }

    /// <summary>A table of articles and prices turned by <paramref name="degrees"/>, each line's direction a little off, in a mixed order.</summary>
    private static List<TextLine> Table(int rows, double degrees)
    {
        var turn = degrees * Math.PI / 180;
        var (cos, sin) = (Math.Cos(turn), Math.Sin(turn));
        var lines = new List<TextLine>();
        for (var i = 0; i < rows; i++)
        {
            Add($"Article {i:00}", 0, 140);
            Add(string.Create(CultureInfo.InvariantCulture, $"{i}.90"), 420, 50);

            void Add(string text, double left, double width)
            {
                var along = left + (width / 2);
                var y = i * 34;
                var own = turn + ((((lines.Count * 7) % 5) - 2) * Math.PI / 180);
                lines.Add(new TextLine(text, new RelativeBox(0.1, 0.1, 0.1, 0.02), 90)
                {
                    Turn = new LineTurn((float)(600 + (along * cos) - (y * sin)), (float)(200 + (along * sin) + (y * cos)), (float)width, 22, (float)own),
                });
            }
        }

        return [.. lines.Where((_, i) => i % 3 == 0), .. lines.Where((_, i) => i % 3 != 0).Reverse()];
    }
}
