using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Events;
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
        var whole = await ocr.SaveAsync(ocr.NewReader("Meter"));

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
        Assert.Equal([ReaderRules.WholePictureName], ocr.Plugin.EventTypes.Single(t => t.Id == OcrInfo.TextReadTypeId(whole.Id)).DataFields[0].Values);

        await ocr.CallAsync(OcrMethods.DeleteReader, new ReaderRequest { ReaderId = reader.Id });
        Assert.Equal(3, changed);
        Assert.Single(ocr.Plugin.EventTypes);
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
    public void TheNumberFieldHoldsTheValueWhenItIsANumber(string value, string number) =>
        Assert.Equal(number, TextReadEvents.Number(value));

    [Fact]
    public void TheValuesOfAnArea()
    {
        var area = new AreaResult { Area = "Container", Text = "MSCU 123456 7", TextRead = "MSCU 123456 7\nMAX GROSS", Confidence = 91.46, MatchesFormat = true };
        var data = TextReadEvents.Data(area, new AreaDetails { AllMatches = "MSCU 123456 7", Number = string.Empty });

        Assert.Equal("Container", data[OcrInfo.AreaField]);
        Assert.Equal("MSCU 123456 7", data[OcrInfo.TextField]);
        Assert.Equal("MSCU 123456 7 MAX GROSS", data[OcrInfo.TextReadField]);
        Assert.Equal("MSCU 123456 7", data[OcrInfo.AllMatchesField]);
        Assert.Equal(string.Empty, data[OcrInfo.NumberField]);
        Assert.Equal("91.5", data[OcrInfo.ConfidenceField]);
        Assert.Equal(OcrInfo.Yes, data[OcrInfo.MatchesFormatField]);
        Assert.True(RuleChecks.IsYes(data[OcrInfo.MatchesFormatField]));
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
        var reader = OcrJson.Deserialize<Reader>(await ocr.Context.InvokeAsync(plugin, ocr.Context.CallAsAllDevices(), OcrMethods.SaveReader,
            OcrJson.Serialize(new SaveReaderRequest { Reader = ocr.NewReader(areas: new Area { Name = "Order", Expected = ExpectedText.WithFormat("#####") }) })));
        ocr.Frames.Single = "anything";

        var reply = OcrJson.Deserialize<ReadingReply>(await ocr.Context.InvokeAsync(plugin, ocr.Context.CallAsAllDevices(), OcrMethods.ReadNow, OcrJson.Serialize(new ReaderRequest { ReaderId = reader.Id })));

        var details = Assert.Single(reply.Details);
        Assert.Equal("12345 67890", details.AllMatches);
        Assert.Equal("12345", details.Number);
        Assert.Equal(new RelativeBox(0.2, 0.2, 0.1, 0.1), details.MatchBox);
        Assert.Equal(64, Assert.Single(reply.Reading!.Areas).Confidence);
        var fired = Assert.Single(ocr.TextRead);
        Assert.Equal("12345 67890", fired.Data[OcrInfo.AllMatchesField]);
        Assert.Equal("64", fired.Data[OcrInfo.ConfidenceField]);
    }

    private sealed class TwoMatches : ITextMatcher, IAreaValueMatcher
    {
        public AreaMatch Match(IReadOnlyList<TextLine> lines, ExpectedText expected) => throw new InvalidOperationException("The full matcher is used.");

        public AreaValue MatchValue(IReadOnlyList<TextLine> lines, ExpectedText expected) =>
            new(new AreaMatch("12345", true, "12345\n67890"), ["12345", "67890", "12345"], 64, new RelativeBox(0.2, 0.2, 0.1, 0.1));
    }
}
