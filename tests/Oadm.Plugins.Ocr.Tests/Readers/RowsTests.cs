using System.Text.Json;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Matching;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Ocr.Tests.Readers;

/// <summary>
/// Rows through the readers (docs/spec/ocr.md "Several lines"): Text stays one line, so a row that splits in one frame is no
/// new value; Text read and the results row hold the rows; the log line of Read text stays one line. The real matcher over a
/// recognizer that gives a small receipt's lines.
/// </summary>
public sealed class RowsTests
{
    private const string Rows = "TOTAL EUR 10.60\nORDER 23F7H";
    private const string OneLine = "TOTAL EUR 10.60 ORDER 23F7H";

    /// <summary>
    /// The lines of a picture by its text: "level" has the price on its article's row, "split" has it 0.7 line heights lower,
    /// a row of its own; any other text is one line.
    /// </summary>
    private sealed class ReceiptRecognizer : ITextRecognizer
    {
        public Task<RecognizedPicture> ReadAsync(ReadOnlyMemory<byte> jpeg, IReadOnlyList<Area> areas, CancellationToken ct)
        {
            IReadOnlyList<TextLine> lines = Frame.TextOf(jpeg) switch
            {
                "" => [],
                "level" => Receipt(priceDown: 0),
                "split" => Receipt(priceDown: 0.7),
                var text => [new TextLine(text, new RelativeBox(0.1, 0.1, 0.5, 0.05), 90)],
            };
            return Task.FromResult(new RecognizedPicture(new Resolution(1920, 1080), [.. areas.Select(_ => lines)]));
        }

        private static TextLine[] Receipt(double priceDown) =>
        [
            new("10.60", new RelativeBox(0.6, 0.2 + (priceDown * 0.05), 0.1, 0.05), 95),
            new("ORDER 23F7H", new RelativeBox(0.1, 0.3, 0.3, 0.05), 90),
            new("TOTAL EUR", new RelativeBox(0.1, 0.2, 0.2, 0.05), 92),
        ];
    }

    private static async Task<OcrHarness> StartedAsync()
    {
        var ocr = new OcrHarness(recognizer: new ReceiptRecognizer(), matcher: new TextMatcher());
        await ocr.Plugin.StartAsync(ocr.Context, CancellationToken.None);
        return ocr;
    }

    private static async Task<IReadOnlyList<RecentRead>> ReadsAsync(OcrHarness ocr, string readerId) =>
        OcrJson.Deserialize<ReadingReply>(await ocr.CallAsync(OcrMethods.GetReading, new ReaderRequest { ReaderId = readerId })).Recent;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_row_that_splits_in_one_frame_fires_no_event(bool movingObjects)
    {
        await using var ocr = await StartedAsync();
        var reader = await ocr.SaveAsync(ocr.NewReader("Counter", ReaderMode.Watching) with { MovingObjects = movingObjects });
        await Eventually.TrueAsync(() => ocr.Frames.Open == 1, "the reader watches");

        await ocr.SendAndWaitAsync(ocr.Inside.Id, "level");
        await ocr.SendAndWaitAsync(ocr.Inside.Id, "level");
        var fired = Assert.Single(ocr.TextRead);
        Assert.Equal(OneLine, fired.Data[OcrInfo.TextField]);
        Assert.Equal(Rows, fired.Data[OcrInfo.TextReadField]);

        // The price drops out of its row for one frame, then for good: the rows change, the value does not.
        foreach (var frame in new[] { "split", "level", "split", "split", "split" })
        {
            await ocr.SendAndWaitAsync(ocr.Inside.Id, frame);
        }

        Assert.Single(ocr.TextRead);
        Assert.Empty(ocr.NoText);
        var last = ocr.Plugin.Runtime!.Last(reader.Id)!.Reading.Areas[0];
        Assert.Equal(OneLine, last.Text);
        Assert.Equal("TOTAL EUR\n10.60\nORDER 23F7H", last.TextRead); // the page's reading shows the rows as they are now
        Assert.Equal(Rows, Assert.Single(await ReadsAsync(ocr, reader.Id)).Text); // one read, as it was read
    }

    [Fact]
    public async Task Text_is_one_line_and_text_read_holds_the_rows()
    {
        await using var ocr = await StartedAsync();
        var reader = await ocr.SaveAsync(ocr.NewReader("Counter", areas:
        [
            new Area { Name = "Receipt", Box = new RelativeBox(0, 0, 1, 1) },
            new Area { Name = "Order", Box = new RelativeBox(0, 0, 1, 1), Expected = ExpectedText.WithFormat("##@#@") },
            new Area { Name = "Meter", Box = new RelativeBox(0, 0, 1, 1), Expected = ExpectedText.WithFormat("#####") },
        ]));
        ocr.Frames.Single = "level";

        var reading = await ocr.ReadOnceAsync(reader.Id);

        Assert.Equal(["Receipt", "Order", "Meter"], ocr.TextRead.Select(e => e.Data[OcrInfo.AreaField]));
        Assert.Equal([OneLine, "23F7H", string.Empty], ocr.TextRead.Select(e => e.Data[OcrInfo.TextField]));
        Assert.All(ocr.TextRead, e => Assert.Equal(Rows, e.Data[OcrInfo.TextReadField])); // with a format too: the whole area
        Assert.All(ocr.TextRead, e => Assert.DoesNotContain('\n', e.Data[OcrInfo.TextField]));
        Assert.All(ocr.TextRead, e => Assert.Equal(string.Empty, e.Data[OcrInfo.NumberField]));
        Assert.All(reading.Reading.Areas, a => Assert.DoesNotContain('\n', a.Text));

        // The results: without a format the rows, with one the match; no row for a format that did not match.
        var rows = await ReadsAsync(ocr, reader.Id);
        Assert.Equal([("Order", "23F7H"), ("Receipt", Rows)], rows.Select(r => (r.Area, r.Text)));
    }

    [Fact]
    public async Task The_log_line_of_read_text_is_one_line()
    {
        await using var ocr = await StartedAsync();
        var reader = await ocr.SaveAsync(ocr.NewReader("Counter", areas:
        [
            new Area { Name = "Receipt", Box = new RelativeBox(0, 0, 1, 1) },
            new Area { Name = "Meter", Box = new RelativeBox(0, 0, 1, 1), Expected = ExpectedText.WithFormat("#####") },
        ]));
        ocr.Frames.Single = "level";
        var log = new FakeActionContext();
        var invocation = new ActionInvocation(
            Guid.NewGuid(),
            "Receipt printed",
            ActionEdge.Fired,
            JsonSerializer.SerializeToElement(new Dictionary<string, string> { [OcrInfo.ReaderActionField] = reader.Id }),
            null,
            new ActionTrigger("oadm.test", "Test", null, null, null, null, DateTimeOffset.UnixEpoch, new Dictionary<string, string>()));

        await Assert.Single(ocr.Plugin.ActionPlugins).ExecuteAsync(log, invocation, CancellationToken.None);

        // The value of the first area, the raw text of the second (its format did not match): both on the one line.
        Assert.Equal([$"Receipt: {OneLine}; Meter: {OneLine}"], log.Lines);
        Assert.Equal(Rows, ocr.TextRead[1].Data[OcrInfo.TextReadField]);
    }

    [Theory]
    [InlineData("TOTAL EUR 10.60\nORDER 23F7H", "TOTAL EUR 10.60 ORDER 23F7H", true)]
    [InlineData("DECAF-\nFEINATO", "DECAF-FEINATO", true)]
    [InlineData("A", "A", true)]
    [InlineData("TOTAL EUR 10.60\nORDER 23F7H", "TOTAL EUR 10.60 ORDER 23F7M", false)] // the vote changed a character
    [InlineData("TOTAL EUR\n10.60", "TOTAL EUR", false)]
    [InlineData("", "A", false)]
    [InlineData("", "", false)]
    public void A_results_row_shows_the_rows_only_when_they_are_the_value(string rows, string value, bool same) =>
        Assert.Equal(same, ReaderRuntime.SameText(rows, value));
}
