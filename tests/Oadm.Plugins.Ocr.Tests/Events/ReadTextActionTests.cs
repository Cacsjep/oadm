using System.Text.Json;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Plugins.Ocr.Tests.Readers;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Ocr.Tests.Events;

/// <summary>The rule action "Read text" (docs/spec/ocr.md "Events and the rule action").</summary>
public sealed class ReadTextActionTests
{
    private static ActionInvocation Invocation(string readerId) => new(
        Guid.NewGuid(),
        "Gate opens",
        ActionEdge.Fired,
        JsonSerializer.SerializeToElement(new Dictionary<string, string> { [OcrInfo.ReaderActionField] = readerId }),
        null,
        new ActionTrigger("oadm.test", "Test", null, null, null, null, DateTimeOffset.UnixEpoch, new Dictionary<string, string>()));

    private static IActionPlugin Action(OcrHarness ocr) => Assert.Single(ocr.Plugin.ActionPlugins);

    [Fact]
    public async Task TheActionReadsOnePictureAndPublishesEveryAreaChangedOrNot()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var reader = await ocr.SaveAsync(ocr.NewReader("Gate", areas: [new Area { Name = "Left", Box = new RelativeBox(0, 0, 0.5, 1) }, new Area { Name = "Right", Box = new RelativeBox(0.5, 0, 0.5, 1) }]));
        var action = Action(ocr);
        Assert.Equal((OcrInfo.ReadTextActionId, "Read text", false), (action.Id, action.DisplayName, action.NeedsDevice));
        ocr.Frames.Single = "MSCU";
        var log = new FakeActionContext();

        await action.ExecuteAsync(log, Invocation(reader.Id), CancellationToken.None);
        await action.ExecuteAsync(log, Invocation(reader.Id), CancellationToken.None);

        Assert.Equal(2, ocr.Frames.Singles);
        Assert.Equal(["Left", "Right", "Left", "Right"], ocr.TextRead.Select(e => e.Data[OcrInfo.AreaField]));
        Assert.All(ocr.TextRead, e => Assert.Equal("MSCU", e.Data[OcrInfo.TextField]));
        Assert.Equal("Left: MSCU; Right: MSCU", log.Lines[0]);
        Assert.Equal("Read text with Gate", action.Describe(Invocation(reader.Id).Settings));
    }

    [Fact]
    public async Task AReadingAlreadyRunningEndsTheActionWithAlreadyReading()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var reader = await ocr.SaveAsync(ocr.NewReader("Gate"));
        using var gate = new SemaphoreSlim(0);
        ocr.Recognizer.Gate = gate;
        ocr.Frames.Single = "A";

        var readNow = ocr.ReadOnceAsync(reader.Id);
        await Eventually.TrueAsync(() => ocr.Recognizer.Started == 1, "the first reading reads");
        var log = new FakeActionContext();
        await Action(ocr).ExecuteAsync(log, Invocation(reader.Id), CancellationToken.None);
        var busy = await Assert.ThrowsAsync<InvalidOperationException>(() => ocr.ReadOnceAsync(reader.Id));

        Assert.Equal([OcrInfo.AlreadyReading], log.Lines);
        Assert.Equal(OcrInfo.AlreadyReading, busy.Message);
        Assert.Equal(1, ocr.Frames.Singles); // nothing queued
        gate.Release();
        var reply = await readNow;
        Assert.Equal("A", Frame.TextOf(reply.Jpeg));
        Assert.Single(ocr.TextRead);
    }

    [Fact]
    public async Task AWatchingReaderDropsItsFramesWhileTheActionReads()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var reader = await ocr.SaveAsync(ocr.NewReader("Gate", ReaderMode.Watching));
        await Eventually.TrueAsync(() => ocr.Frames.Open == 1, "the reader watches");
        using var gate = new SemaphoreSlim(0);
        ocr.Recognizer.Gate = gate;
        ocr.Frames.Single = "A";

        var action = Action(ocr).ExecuteAsync(new FakeActionContext(), Invocation(reader.Id), CancellationToken.None);
        await Eventually.TrueAsync(() => ocr.Recognizer.Started == 1, "the action reads");
        ocr.Frames.Send(ocr.Inside.Id, "B");
        await Eventually.TrueAsync(() => ocr.Plugin.Runtime!.DroppedWhileBusy == 1, "the frame was dropped");
        gate.Release();
        await action;
        ocr.Recognizer.Gate = null;
        await ocr.SendAndWaitAsync(ocr.Inside.Id, "C");

        Assert.Equal(["A", "C"], ocr.Recognizer.Read);
    }

    [Fact]
    public async Task TheReadersAreTheChoicesAndAGoneReaderIsAnError()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var gate = await ocr.SaveAsync(ocr.NewReader("Gate"));
        var meter = await ocr.SaveAsync(ocr.NewReader("Meter"));
        var action = Action(ocr);

        var choices = await action.ListChoicesAsync(null!, OcrInfo.ReaderActionField, CancellationToken.None);
        Assert.Equal([(gate.Id, "Gate"), (meter.Id, "Meter")], choices.Select(c => (c.Value, c.Label)));
        Assert.Empty(action.Validate(Invocation(gate.Id).Settings, statefulRule: false));
        Assert.Equal(ReaderRules.Gone, action.Validate(Invocation("gone").Settings, statefulRule: false)[OcrInfo.ReaderActionField]);
        Assert.Equal(ReaderRules.ReaderMissing, action.Validate(JsonDocument.Parse("{}").RootElement, statefulRule: false)[OcrInfo.ReaderActionField]); // refused at the rule's save
        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => action.ExecuteAsync(new FakeActionContext(), Invocation("gone"), CancellationToken.None));
        Assert.Equal(ReaderRules.Gone, failed.Message);
    }

    [Fact]
    public async Task ReadNowFiresTheRealEventOfAnOnActionReader()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var reader = await ocr.SaveAsync(ocr.NewReader("Gate"));
        ocr.Frames.Single = "0815";

        await ocr.CallAsync(OcrMethods.ReadNow, new ReaderRequest { ReaderId = reader.Id });

        Assert.Equal(1, ocr.Frames.Singles);
        Assert.Equal("0815", Assert.Single(ocr.TextRead).Data[OcrInfo.TextField]);
    }

    [Fact]
    public async Task ReadNowRefusesAWatchingReaderAndAnUnknownOne()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var watching = await ocr.SaveAsync(ocr.NewReader("Gate", ReaderMode.Watching));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => ocr.CallAsync(OcrMethods.ReadNow, new ReaderRequest { ReaderId = watching.Id }));
        var gone = await Assert.ThrowsAsync<ArgumentException>(() => ocr.CallAsync(OcrMethods.ReadNow, new ReaderRequest { ReaderId = "nobody" }));

        Assert.Equal(ReaderRules.ReadNowOnActionOnly, refused.Message);
        Assert.Equal(ReaderRules.Gone, gone.Message);
        Assert.Empty(ocr.TextRead);
    }

    [Fact]
    public async Task NoTextFiresNoTextReadAndTheEventLogSaysSo()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var reader = await ocr.SaveAsync(ocr.NewReader("Gate"));
        ocr.Frames.Single = string.Empty;
        var log = new FakeActionContext();

        await Action(ocr).ExecuteAsync(log, Invocation(reader.Id), CancellationToken.None);

        Assert.Equal(1, ocr.Frames.Singles);
        Assert.Empty(ocr.TextRead);
        Assert.Equal(reader.Areas[0].Name, Assert.Single(ocr.NoText).Data[OcrInfo.AreaField]);
        Assert.EndsWith(": no text", Assert.Single(log.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReadingIsKeptForThePageWithoutItsPicture()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var reader = await ocr.SaveAsync(ocr.NewReader("Gate"));
        var empty = OcrJson.Deserialize<ReadingReply>(await ocr.CallAsync(OcrMethods.GetReading, new ReaderRequest { ReaderId = reader.Id }));
        Assert.Null(empty.Reading);
        ocr.Frames.Single = "0815";

        var reply = await ocr.ReadOnceAsync(reader.Id);

        Assert.Equal("0815", Assert.Single(reply.Reading.Areas).Text);
        Assert.Equal("0815", Frame.TextOf(reply.Jpeg));
        Assert.Equal("815", Assert.Single(reply.Details).Number);
        Assert.Equal("0815", Assert.Single(ocr.TextRead).Data[OcrInfo.TextField]);
        var json = await ocr.CallAsync(OcrMethods.GetReading, new ReaderRequest { ReaderId = reader.Id });
        var page = OcrJson.Deserialize<ReadingReply>(json);
        Assert.Equal(OcrJson.Serialize(new ReadingReply { Reading = reply.Reading, Details = reply.Details, Recent = page.Recent }), json);
        Assert.Equal("0815", Assert.Single(page.Recent).Text);
        Assert.DoesNotContain("jpeg", json, StringComparison.OrdinalIgnoreCase); // the page shows the live video, never the frame
    }
}
