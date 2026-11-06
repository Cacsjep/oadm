using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Events;
using Oadm.Plugins.Ocr.Matching;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Plugins.Ocr.Tests.Readers;
using Oadm.Sdk.Events;

namespace Oadm.Plugins.Ocr.Tests.Events;

/// <summary>The event "Text read" of each reader (docs/spec/ocr.md "Events and the rule action").</summary>
public sealed class TextReadEventTests
{
    [Fact]
    public async Task EveryReaderHasItsTextReadEventWithTheSpecsFields()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var changed = 0;
        ocr.Plugin.EventTypesChanged += (_, _) => changed++;

        var reader = await ocr.SaveAsync(ocr.NewReader("Gate 2", areas: [new Area { Name = "Container number" }, new Area { Name = "Seal" }]));
        var whole = await ocr.SaveAsync(ocr.NewReader("Meter") with { Areas = [] });

        Assert.Equal(2, changed);
        var type = ocr.Plugin.EventTypes.Single(t => t.Id == OcrInfo.TextReadTypeId(reader.Id));
        Assert.Equal(("Text read", EventKind.Stateless, EventScope.Device), (type.DisplayName, type.Kind, type.Scope));
        Assert.Equal(["Text recognition", "Gate 2"], type.Folders);
        Assert.Equal([ocr.Inside.Id], type.DeviceIds);
        Assert.Equal("all", type.OwnerScopeKey);
        Assert.Equal(
            [("area", "Area", EventValueKind.Text), ("text", "Text", EventValueKind.Text), ("textRead", "Text read", EventValueKind.Text),
             ("allMatches", "All matches", EventValueKind.Text), ("number", "Number", EventValueKind.Number),
             ("confidence", "Confidence", EventValueKind.Number), ("matchesFormat", "Matches format", EventValueKind.YesNo)],
            type.DataFields.Select(f => (f.Name, f.DisplayName, f.Kind)));
        Assert.All(type.DataFields, f => Assert.True(f.Filter));
        Assert.Equal(["Container number", "Seal"], type.DataFields[0].Values);
        Assert.Empty(ocr.Plugin.EventTypes.Single(t => t.Id == OcrInfo.TextReadTypeId(whole.Id)).DataFields[0].Values ?? []); // no area: nothing to read

        await ocr.CallAsync(OcrMethods.DeleteReader, new ReaderRequest { ReaderId = reader.Id });
        Assert.Equal(3, changed);
        Assert.DoesNotContain(ocr.Plugin.EventTypes, t => t.Id == OcrInfo.TextReadTypeId(reader.Id) || t.Id == OcrInfo.ReaderProblemTypeId(reader.Id));
    }

    [Fact]
    public void LinesAreJoinedForTheEvents()
    {
        Assert.Equal("DECAF-FEINATO 250 g", TextReadEvents.Join("DECAF-\nFEINATO\n250 g"));
        Assert.Equal(string.Empty, TextReadEvents.Join(null));
        Assert.Equal(500, TextReadEvents.Join(new string('x', 600)).Length);
    }

    [Theory]
    [InlineData("1234", "1234")]
    [InlineData("1234,5", "1234.5")]
    [InlineData(" -0.75 ", "-0.75")]
    [InlineData("1.234,5", "")]
    [InlineData("12 345", "")]
    [InlineData("A12", "")]
    [InlineData("", "")]
    [InlineData("١٢", "")]
    [InlineData("+007", "7")]
    [InlineData("-0,00", "0")]
    [InlineData("12.50", "12.5")]
    [InlineData("1.2.3", "")]
    [InlineData("12.", "")]
    [InlineData("12345678901234567890123", "12345678901234567890123")] // a long counter keeps every digit
    [InlineData("0.12345678901234567890", "0.1234567890123456789")]
    public void TheNumberFieldHoldsTheValueWhenItIsANumber(string value, string number) =>
        Assert.Equal(number, NumberText.Of(value));

    [Fact]
    public void TheValuesOfAnArea()
    {
        var area = new AreaResult { Area = "Container", Text = "MSCU 123456 7", TextRead = "MSCU 123456 7\nMAX GROSS", Confidence = 91.46, MatchesFormat = true };
        var data = TextReadEvents.Data(area, new AreaDetails { AllMatches = "MSCU 123456 7", Number = string.Empty });

        Assert.Equal("Container", data[OcrInfo.AreaField]);
        Assert.Equal("MSCU 123456 7", data[OcrInfo.TextField]);
        Assert.Equal("MSCU 123456 7\nMAX GROSS", data[OcrInfo.TextReadField]); // the area's rows
        Assert.Equal("MSCU 123456 7", data[OcrInfo.AllMatchesField]);
        Assert.Equal(string.Empty, data[OcrInfo.NumberField]);
        Assert.Equal("91.5", data[OcrInfo.ConfidenceField]);
        Assert.Equal(OcrInfo.Yes, data[OcrInfo.MatchesFormatField]);
        Assert.True(RuleChecks.IsYes(data[OcrInfo.MatchesFormatField]));
    }

    [Fact]
    public void TextNeverHoldsALineBreakAndTextReadHoldsTheRows()
    {
        // Whatever a matcher gives as the value: the event's Text is one line. Text read keeps its rows.
        var area = new AreaResult { Area = "Receipt", Text = "TOTAL EUR 10.60\nORDER 23F7H", TextRead = "TOTAL EUR 10.60\nORDER 23F7H", Confidence = 90 };
        var data = TextReadEvents.Data(area, ReadingMaker.DetailsOf(area, [], null));

        Assert.Equal("TOTAL EUR 10.60 ORDER 23F7H", data[OcrInfo.TextField]);
        Assert.Equal("TOTAL EUR 10.60\nORDER 23F7H", data[OcrInfo.TextReadField]);
        Assert.Equal(string.Empty, data[OcrInfo.NumberField]);

        // Text read is cut at 500 characters and never ends in a line break.
        var rows = string.Join('\n', Enumerable.Repeat(new string('x', 24), 30));
        var cut = TextReadEvents.Data(area with { TextRead = rows }, new AreaDetails())[OcrInfo.TextReadField];
        Assert.Equal(499, cut.Length);
        Assert.Equal(rows[..499], cut);
        Assert.Equal(TextReadEvents.MaxTextLength, TextReadEvents.Rows(new string('x', 600)).Length);
        Assert.Equal(string.Empty, TextReadEvents.Rows(null));
    }

    [Theory]
    [InlineData("00815", "815")]
    [InlineData("12,50", "12.5")]
    [InlineData("12 kWh", "")]
    public void NumberIsMadeOfTheOneLineValueAsBefore(string text, string number)
    {
        var (result, details) = new ReadingMaker(new FakeRecognizer(), new TextMatcher(), TimeProvider.System)
            .Area(new Area { Name = "Meter" }, [new TextLine(text, new RelativeBox(0.1, 0.1, 0.3, 0.1), 90)]);

        Assert.Equal(number, details.Number);
        Assert.Equal(number, TextReadEvents.Data(result, details)[OcrInfo.NumberField]);
        Assert.Equal(text, result.Text);
        Assert.Equal(text, result.TextRead);
    }

    [Fact]
    public async Task AFormatThatDidNotMatchStillFiresWithTheRawText()
    {
        var ocr = new OcrHarness();
        var parts = new ReaderParts
        {
            Frames = _ => ocr.Frames,
            Recognizer = _ => ocr.Recognizer,
            Matcher = new NoMatch(),
            Time = ocr.Time,
        };
        await using var plugin = new OcrPlugin(parts);
        await plugin.StartAsync(ocr.Context, CancellationToken.None);
        var reader = OcrJson.Deserialize<SaveReaderReply>(await ocr.Context.InvokeAsync(plugin, ocr.Context.CallAsAllDevices(), OcrMethods.SaveReader,
            OcrJson.Serialize(new SaveReaderRequest { Reader = ocr.NewReader(areas: new Area { Name = "Order", Expected = ExpectedText.WithFormat("#####") }) }))).Reader!;
        ocr.Frames.Single = "anything";

        await plugin.ReadOnceAsync(plugin.FindReader(reader.Id)!, CancellationToken.None);

        Assert.Empty(ocr.NoText); // text was read, only not the format
        var fired = Assert.Single(ocr.TextRead);
        Assert.Equal(string.Empty, fired.Data[OcrInfo.TextField]);
        Assert.Equal("HELLO", fired.Data[OcrInfo.TextReadField]);
        Assert.Equal(OcrInfo.No, fired.Data[OcrInfo.MatchesFormatField]);
    }

    [Fact]
    public async Task AMatcherWithEveryMatchFillsAllMatchesAndTheConfidence()
    {
        var ocr = new OcrHarness();
        var parts = new ReaderParts
        {
            Frames = _ => ocr.Frames,
            Recognizer = _ => ocr.Recognizer,
            Matcher = new TwoMatches(),
            Time = ocr.Time,
        };
        await using var plugin = new OcrPlugin(parts);
        await plugin.StartAsync(ocr.Context, CancellationToken.None);
        var reader = OcrJson.Deserialize<SaveReaderReply>(await ocr.Context.InvokeAsync(plugin, ocr.Context.CallAsAllDevices(), OcrMethods.SaveReader,
            OcrJson.Serialize(new SaveReaderRequest { Reader = ocr.NewReader(areas: new Area { Name = "Order", Expected = ExpectedText.WithFormat("#####") }) }))).Reader!;
        ocr.Frames.Single = "anything";

        var reply = await plugin.ReadOnceAsync(plugin.FindReader(reader.Id)!, CancellationToken.None);

        var details = Assert.Single(reply.Details);
        Assert.Equal("12345 67890", details.AllMatches);
        Assert.Equal("12345", details.Number);
        Assert.Equal(new RelativeBox(0.2, 0.2, 0.1, 0.1), details.MatchBox);
        Assert.Equal(64, Assert.Single(reply.Reading!.Areas).Confidence);
        var fired = Assert.Single(ocr.TextRead);
        Assert.Equal("12345 67890", fired.Data[OcrInfo.AllMatchesField]);
        Assert.Equal("64", fired.Data[OcrInfo.ConfidenceField]);
    }

    private sealed class NoMatch : ITextMatcher
    {
        public AreaValue Match(IReadOnlyList<TextLine> lines, ExpectedText expected) =>
            new(new AreaMatch(string.Empty, false, "HELLO"), [], 0, null);
    }

    private sealed class TwoMatches : ITextMatcher
    {
        public AreaValue Match(IReadOnlyList<TextLine> lines, ExpectedText expected) =>
            new(new AreaMatch("12345", true, "12345\n67890"), ["12345", "67890", "12345"], 64, new RelativeBox(0.2, 0.2, 0.1, 0.1));
    }
}
