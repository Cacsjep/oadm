using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Oadm.Plugins.Ocr.Matching;
using Oadm.Plugins.Ocr.Model;
using SkiaSharp;
using Xunit.Abstractions;

namespace Oadm.Plugins.Ocr.Tests.Recognition;

/// <summary>
/// The measured rows (docs/spec/ocr.md "Several lines"): receipts of the accuracy set's generator, Search over the whole full
/// HD picture with Any text, read on the CPU. Per test the article and price pairs the recognition delivered ("both read":
/// grouping can reach no more), those that share a row of their own, how often the price came straight after its article in
/// the order before rows, and the pictures whose rows come in the receipt's order. Writes a table to the test output; Perf
/// (about a minute, not in <c>manage test unit</c>). Asserts only that every reading ended.
/// </summary>
[Trait("Category", "Perf")]
[Collection(RecognitionGroup.Name)]
public sealed class RowsSweepTests(RecognizerFixture fixture, ITestOutputHelper output)
{
    private static readonly Area Whole = new() { Name = "Counter", Box = new RelativeBox(0, 0, 1, 1) };

    /// <summary>What a set of pictures gave: pairs in all, both texts read, in their own row, the price straight after its article in the order before rows.</summary>
    private sealed record Score(string Test, int Pairs, int BothRead, int InRow, int OldOrder, int RowsInOrder, int Pictures);

    [Fact]
    public async Task Rows_of_receipts()
    {
        var scores = new List<Score>();
        float[] every15 = [.. Enumerable.Range(0, 24).Select(i => i * 15f)];
        scores.Add(await ScoreAsync("Every 15 degrees, scale 0.8", every15.Select(a => Turned(a, 0.8f))));
        scores.Add(await ScoreAsync("Every 15 degrees, scale 1.3", every15.Select(a => Turned(a, 1.3f))));
        scores.Add(await ScoreAsync("Up to 10 degrees from level or upside down", new float[] { 0, 5, 10, 350, 355, 180, 185, 190, 170, 175 }.Select(a => Turned(a, 1f))));
        scores.Add(await ScoreAsync("Wide table, price 25 characters away", new float[] { 0, 10, 90, 100, 180, 190, 270, 280 }.Select(a => Turned(a, 1f, priceGap: 20))));
        scores.Add(await ScoreAsync("Tilt to the side 20 degrees", Tilted(new SweepPose(0, TiltAroundVertical: 20))));
        scores.Add(await ScoreAsync("Tilt from the top 50 degrees", Tilted(new SweepPose(0, TiltAroundHorizontal: 50))));
        scores.Add(await ScoreAsync("Tilt to the side 50 degrees", Tilted(new SweepPose(0, TiltAroundVertical: 50))));
        scores.Add(await ScoreAsync("Tilt both ways 40 degrees, turned 30 degrees", Tilted(new SweepPose(30, TiltAroundVertical: 40, TiltAroundHorizontal: 40)).Take(2)));
        var set = AccuracySet.Load().Where(p => p.Group is "receipt-search" or "hard-receipt-search").Select(p => p.File).Distinct()
            .Select(f => (Func<byte[]>)(() => File.ReadAllBytes(Path.Combine(AccuracySet.Folder, f))));
        scores.Add(await ScoreAsync("Receipts of the accuracy set", set));

        var table = new StringBuilder();
        table.AppendLine("| Test | Both read | In the right row | Price straight after its article before rows | Rows in order |");
        table.AppendLine("|---|---|---|---|---|");
        foreach (var s in scores)
        {
            table.AppendLine(CultureInfo.InvariantCulture, $"| {s.Test} | {s.BothRead}/{s.Pairs} | {s.InRow}/{s.Pairs} | {s.OldOrder}/{s.Pairs} | {s.RowsInOrder}/{s.Pictures} |");
        }

        output.WriteLine(table.ToString());
        Assert.All(scores, s => Assert.True(s.Pairs > 0));
    }

    private static Func<byte[]> Turned(float degrees, float scale, int priceGap = 0) =>
        () => AccuracySetGenerator.ReceiptPicture("23F7H", degrees, scale, seed: 23 + (int)degrees, priceGap);

    /// <summary>The receipt on a tilted plane, level and upside down, at two sizes.</summary>
    private static IEnumerable<Func<byte[]>> Tilted(SweepPose pose) =>
        from scale in new[] { 1f, 1.3f }
        from turn in new[] { 0f, 180f }
        select (Func<byte[]>)(() => AccuracySetGenerator.ReceiptPicture("23F7H", 0, scale, seed: 23, pose: receipt => SKMatrix.Concat(
            AngleSweep.Pose(pose with { Rotation = pose.Rotation + turn }, receipt.Width * scale, receipt.Height * scale, new SKPoint(960, 540)),
            SKMatrix.CreateScale(scale, scale))));

    private async Task<Score> ScoreAsync(string test, IEnumerable<Func<byte[]>> pictures)
    {
        var (pairs, both, inRow, old, ordered, count) = (0, 0, 0, 0, 0, 0);
        foreach (var make in pictures)
        {
            var lines = (await fixture.Recognizer.ReadAsync(make(), [Whole], CancellationToken.None)).LinesPerArea[0];
            var rows = LineOrder.Rows(lines).Select(r => Clean(string.Join(' ', r.Select(l => l.Text)))).ToList();
            var before = OrderBeforeRows(lines).Select(l => Clean(l.Text)).ToList();
            var texts = lines.Select(l => Clean(l.Text)).ToList();
            var places = new List<int>();
            foreach (var (article, price) in AccuracySetGenerator.ReceiptPairs)
            {
                pairs++;
                if (!texts.Any(t => t.Contains(article, StringComparison.Ordinal)) || !texts.Any(t => t.Contains(price, StringComparison.Ordinal)))
                {
                    continue;
                }

                both++;
                var row = rows.IndexOf($"{article} {price}");
                if (row >= 0)
                {
                    inRow++;
                    places.Add(row);
                }

                var at = before.IndexOf(article);
                old += before.Contains($"{article} {price}") || (at >= 0 && at + 1 < before.Count && before[at + 1] == price) ? 1 : 0;
            }

            count++;
            ordered += places.SequenceEqual(places.Order()) ? 1 : 0; // the rows found come in the receipt's order
            output.WriteLine($"{test}: {string.Join(" / ", rows)}");
        }

        return new Score(test, pairs, both, inRow, old, ordered, count);
    }

    private static string Clean(string text) => Regex.Replace(text.Trim(), @"\s+", " ");

    /// <summary>The order before rows, for comparison: by the level boxes, top to bottom, boxes that overlap by half a height left to right.</summary>
    private static List<TextLine> OrderBeforeRows(IReadOnlyList<TextLine> lines)
    {
        var rows = new List<(double Top, double Bottom, List<TextLine> Lines)>();
        foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).OrderBy(l => l.Box.Y).ThenBy(l => l.Box.X))
        {
            var (top, bottom) = (line.Box.Y, line.Box.Y + line.Box.Height);
            if (rows.Count > 0)
            {
                var row = rows[^1];
                var overlap = Math.Min(bottom, row.Bottom) - Math.Max(top, row.Top);
                if (overlap > 0 && overlap >= 0.5 * Math.Min(line.Box.Height, row.Bottom - row.Top))
                {
                    row.Lines.Add(line);
                    rows[^1] = (Math.Min(row.Top, top), Math.Max(row.Bottom, bottom), row.Lines);
                    continue;
                }
            }

            rows.Add((top, bottom, [line]));
        }

        return [.. rows.SelectMany(r => r.Lines.OrderBy(l => l.Box.X))];
    }
}
