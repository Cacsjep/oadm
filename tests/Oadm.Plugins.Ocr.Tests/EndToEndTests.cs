using System.Text.Json;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Matching;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Recognition;
using Oadm.Plugins.Ocr.Tests.Readers;
using Oadm.Plugins.Ocr.Tests.Recognition;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Ocr.Tests;

/// <summary>
/// The real parts together (docs/spec/ocr.md "Readers", "Events and the rule action", "Accuracy"): the ONNX recognition on the
/// CPU, <see cref="TextMatcher"/>, the vote and the SkiaSharp cut-outs, with recorded pictures of the accuracy set in place
/// of a camera. A watching reader on the coffee box (recorded read-only from 10.0.0.48) publishes each value once the vote
/// holds and an empty value once when the box is gone; the action Read text reads a container number with its format.
/// </summary>
[Collection(RecognitionGroup.Name)]
public sealed class EndToEndTests(RecognizerFixture fixture)
{
    private static ReadOnlyMemory<byte> Picture(string file) => File.ReadAllBytes(Path.Combine(AccuracySet.Folder, file));

    private OcrHarness Harness() => new(recognizer: new Borrowed(fixture.Recognizer), matcher: new TextMatcher(), cutter: new PictureCutter());

    [Fact]
    public async Task AWatchingReaderPublishesTheBoxsTextsOnceAndEmptyOnceWhenTheBoxIsGone()
    {
        await using var ocr = Harness();
        await ocr.Plugin.StartAsync(ocr.Context, CancellationToken.None);
        var reader = await ocr.SaveAsync(ocr.NewReader("Coffee", ReaderMode.Watching, areas:
        [
            new Area { Name = "Brand", Box = new RelativeBox(0.28, 0.54, 0.16, 0.1) },
            new Area { Name = "Intensity", Box = RelativeBox.Whole, Expected = ExpectedText.WithFormat("#/##") },
        ]));
        await Eventually.TrueAsync(() => ocr.Frames.Open == 1, "the reader watches");

        var box = Picture("box-1920x1080.jpg");
        var yard = Picture("yard-1280x720.jpg");
        await SendAsync(ocr, box);
        Assert.Empty(ocr.TextRead); // one frame is no vote yet
        await SendAsync(ocr, box);
        await SendAsync(ocr, box);

        var first = ocr.TextRead.ToList();
        Assert.Equal([("Brand", "BARISSIMO"), ("Intensity", "4/12")], first.Select(e => (e.Data[OcrInfo.AreaField], e.Data[OcrInfo.TextField])));
        Assert.All(first, e => Assert.Equal(OcrInfo.TextReadTypeId(reader.Id), e.TypeId));
        Assert.Equal([OcrInfo.No, OcrInfo.Yes], first.Select(e => e.Data[OcrInfo.MatchesFormatField])); // Any text has no format to match
        Assert.All(first, e => Assert.InRange(double.Parse(e.Data[OcrInfo.ConfidenceField], System.Globalization.CultureInfo.InvariantCulture), 50, 100));
        Assert.Equal("4/12", first[1].Data[OcrInfo.AllMatchesField]);
        Assert.Equal(string.Empty, first[1].Data[OcrInfo.NumberField]);

        // The box is gone: after the vote turned, both areas publish once with an empty text, and nothing more.
        for (var i = 0; i < 4; i++)
        {
            await SendAsync(ocr, yard);
        }

        var gone = ocr.TextRead.Skip(first.Count).ToList();
        Assert.Equal([("Brand", ""), ("Intensity", "")], gone.Select(e => (e.Data[OcrInfo.AreaField], e.Data[OcrInfo.TextField])));
        Assert.All(gone, e => Assert.Equal(OcrInfo.No, e.Data[OcrInfo.MatchesFormatField]));
    }

    [Fact]
    public async Task TheActionReadTextReadsAContainerNumberWithItsFormat()
    {
        await using var ocr = Harness();
        await ocr.Plugin.StartAsync(ocr.Context, CancellationToken.None);
        var reader = await ocr.SaveAsync(ocr.NewReader("Gate 2", areas:
            new Area { Name = "Container number", Box = new RelativeBox(0.4501, 0.1235, 0.4602, 0.1648), Expected = ExpectedText.WithFormat("@@@U ###### #") }));
        ocr.Frames.SinglePicture = Picture("container-01.jpg");
        var log = new FakeActionContext();

        await Assert.Single(ocr.Plugin.ActionPlugins).ExecuteAsync(log, Invocation(reader.Id), CancellationToken.None);

        var fired = Assert.Single(ocr.TextRead);
        Assert.Equal("CSQU 383172 9", fired.Data[OcrInfo.TextField]);
        Assert.Equal("CSQU 383172 9", fired.Data[OcrInfo.AllMatchesField]);
        Assert.Equal(OcrInfo.Yes, fired.Data[OcrInfo.MatchesFormatField]);
        Assert.Equal(ocr.Inside.Id, fired.DeviceId);
        Assert.Equal("Container number: CSQU 383172 9", Assert.Single(log.Lines));

        // The page gets the value's cut-out (memory only).
        var reply = OcrJson.Deserialize<ReadingReply>(await ocr.CallAsync(OcrMethods.GetReading, new ReaderRequest { ReaderId = reader.Id }));
        var details = Assert.Single(reply.Details);
        Assert.NotNull(details.MatchBox);
        Assert.True(details.Cutout is [0xFF, 0xD8, ..], "the cut-out is a JPEG");
    }

    private static ActionInvocation Invocation(string readerId) => new(
        Guid.NewGuid(),
        "Truck at gate 2",
        ActionEdge.Fired,
        JsonSerializer.SerializeToElement(new Dictionary<string, string> { [OcrInfo.ReaderActionField] = readerId }),
        null,
        new ActionTrigger("oadm.test", "Test", null, null, null, null, DateTimeOffset.UnixEpoch, new Dictionary<string, string>()));

    private static async Task SendAsync(OcrHarness ocr, ReadOnlyMemory<byte> picture)
    {
        var before = ocr.Readings;
        ocr.Frames.Send(ocr.Inside.Id, picture);
        await Eventually.TrueAsync(() => ocr.Readings > before, "the picture was read");
    }

    /// <summary>The fixture's recognizer, never disposed by the plugin's stop.</summary>
    private sealed class Borrowed(ITextRecognizer inner) : ITextRecognizer
    {
        public Task<RecognizedPicture> ReadAsync(ReadOnlyMemory<byte> jpeg, IReadOnlyList<Area> areas, CancellationToken ct) => inner.ReadAsync(jpeg, areas, ct);
    }
}
