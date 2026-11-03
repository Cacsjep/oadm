using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Ocr.Tests.Readers;

/// <summary>
/// Watching readers (docs/spec/ocr.md "Readers", "Events and the rule action", "Accuracy"): one loop per reader, the newest
/// frame wins, a value is published once the vote holds in 2 frames in a row and only on change; the loops end with the plugin.
/// </summary>
public sealed class WatchingTests
{
    private static async Task<Reader> WatchAsync(OcrHarness ocr, Reader reader)
    {
        var opened = ocr.Frames.Opened;
        var saved = await ocr.SaveAsync(reader);
        await Eventually.TrueAsync(() => ocr.Frames.Opened > opened && ocr.Frames.Open > 0, "the reader watches");
        return saved;
    }

    private static IReadOnlyList<string> Texts(OcrHarness ocr) => [.. ocr.TextRead.Select(e => e.Data[OcrInfo.TextField])];

    [Fact]
    public async Task AValueIsPublishedOnceItHoldsInTwoFramesAndOnlyWhenItChanges()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var reader = await WatchAsync(ocr, ocr.NewReader("Gate", ReaderMode.Watching) with { Fps = 2 });
        Assert.Equal([2], ocr.Frames.Rates);

        // The vote of the last 5 frames (all read with the same confidence): the value most frames had, the newest on a tie.
        foreach (var text in new[] { "", "", "A", "B", "B", "B", "B", "A", "A", "A", "A", "", "", "", "" })
        {
            await ocr.SendAndWaitAsync(ocr.Inside.Id, text);
        }

        // Empty at the start: nothing. A for one frame: never the vote. B wins at the 4th frame and holds at the 5th: B once,
        // never again while it stays. A wins at its 3rd frame (B B A A A) and holds: A once. Empty wins at its 3rd frame and
        // holds: once, empty.
        Assert.Equal(["B", "A", ""], Texts(ocr));
        var last = ocr.TextRead[^1];
        Assert.Equal(OcrInfo.TextReadTypeId(reader.Id), last.TypeId);
        Assert.Equal(ocr.Inside.Id, last.DeviceId);
        Assert.Null(last.IsOn); // stateless
        Assert.Equal(OcrInfo.No, last.Data[OcrInfo.MatchesFormatField]);
    }

    [Fact]
    public async Task EveryAreaVotesOnItsOwn()
    {
        await using var ocr = await OcrHarness.StartAsync();
        await WatchAsync(ocr, ocr.NewReader("Gate", ReaderMode.Watching, areas: [new Area { Name = "Left", Box = new RelativeBox(0, 0, 0.5, 1) }, new Area { Name = "Right", Box = new RelativeBox(0.5, 0, 0.5, 1) }]));

        // A single bad frame between good ones changes nothing (the vote of the last 5 frames), in each area.
        foreach (var text in new[] { "MSCU", "MSCU", "M5CU", "MSCU", "MSCU" })
        {
            await ocr.SendAndWaitAsync(ocr.Inside.Id, text);
        }

        Assert.Equal(["Left", "Right"], ocr.TextRead.Select(e => e.Data[OcrInfo.AreaField]));
        Assert.All(ocr.TextRead, e => Assert.Equal("MSCU", e.Data[OcrInfo.TextField]));
    }

    [Fact]
    public async Task TheNewestFrameWinsAndOlderOnesAreDropped()
    {
        await using var ocr = await OcrHarness.StartAsync();
        await WatchAsync(ocr, ocr.NewReader("Gate", ReaderMode.Watching));
        using var gate = new SemaphoreSlim(0);
        ocr.Recognizer.Gate = gate;

        ocr.Frames.Send(ocr.Inside.Id, "1");
        await Eventually.TrueAsync(() => ocr.Recognizer.Started == 1, "the first frame is being read");
        foreach (var text in new[] { "2", "3", "4" })
        {
            ocr.Frames.Send(ocr.Inside.Id, text);
        }

        // The pump takes every frame from the camera while the reading waits; only the newest stays.
        await Eventually.TrueAsync(() => ocr.Frames.Taken == 4, "the pump took every frame");
        gate.Release();
        await Eventually.TrueAsync(() => ocr.Recognizer.Started == 2, "the next frame is being read");
        gate.Release();
        await Eventually.TrueAsync(() => ocr.Recognizer.Read.Count == 2, "both readings ended");
        Assert.Equal(["1", "4"], ocr.Recognizer.Read);
    }

    [Fact]
    public async Task AFailedReadingIsLoggedAndTheReaderGoesOn()
    {
        await using var ocr = await OcrHarness.StartAsync();
        await WatchAsync(ocr, ocr.NewReader("Gate", ReaderMode.Watching));
        ocr.Recognizer.FailNext = new InvalidOperationException("broken picture");

        ocr.Frames.Send(ocr.Inside.Id, "X");
        await Eventually.TrueAsync(() => ocr.Recognizer.FailNext is null, "the failing reading ran");
        await ocr.SendAndWaitAsync(ocr.Inside.Id, "A");
        await ocr.SendAndWaitAsync(ocr.Inside.Id, "A");

        Assert.Equal(["A"], Texts(ocr));
    }

    [Fact]
    public async Task TheLastReadingIsKeptAndToldToThePage()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var reader = await WatchAsync(ocr, ocr.NewReader("Meter", ReaderMode.Watching));

        await ocr.SendAndWaitAsync(ocr.Inside.Id, "1234,5");
        await ocr.SendAndWaitAsync(ocr.Inside.Id, "1234,5");

        var reply = OcrJson.Deserialize<ReadingReply>(await ocr.CallAsync(OcrMethods.GetReading, new ReaderRequest { ReaderId = reader.Id }));
        Assert.Equal("1234,5", Frame.TextOf(ocr.Plugin.Runtime!.Last(reader.Id)!.Jpeg)); // kept in memory, never sent
        Assert.Equal(ocr.Time.GetUtcNow(), reply.Reading!.Time);
        var area = Assert.Single(reply.Reading.Areas);
        Assert.Equal((OcrHarness.DefaultArea.Name, "1234,5", 90.0), (area.Area, area.Text, area.Confidence));
        Assert.Equal("1234.5", Assert.Single(reply.Details).Number);
        Assert.Equal("1234,5", Assert.Single((await ocr.ListAsync()).Readers).LastText);

        var live = OcrJson.Deserialize<ReadingEvent>(ocr.Context.PublishedEvents.Last(e => e.Topic == OcrMethods.ReadingTopic).PayloadJson);
        Assert.Equal(reader.Id, live.Reading.ReaderId);
        Assert.Equal("1234.5", Assert.Single(live.Details).Number);
        Assert.Equal("1234.5", ocr.TextRead[^1].Data[OcrInfo.NumberField]);
    }

    [Fact]
    public async Task ChangesRestartTheLoopAndStopEndsEveryLoop()
    {
        var ocr = await OcrHarness.StartAsync();
        var reader = await WatchAsync(ocr, ocr.NewReader("Gate", ReaderMode.Watching));
        await ocr.SaveAsync(ocr.NewReader("Second", ReaderMode.Watching, device: ocr.Outside.Id));
        await Eventually.TrueAsync(() => ocr.Frames.Open == 2, "two readers watch");

        await ocr.SaveAsync(reader with { Fps = 4 });
        await Eventually.TrueAsync(() => ocr.Frames.Rates.Contains(4) && ocr.Frames.Open == 2, "the changed reader watches again");
        await ocr.SaveAsync(reader with { Fps = 4, Mode = ReaderMode.OnAction });
        await Eventually.TrueAsync(() => ocr.Frames.Open == 1, "an On action reader does not watch");

        await ocr.Plugin.StopAsync(CancellationToken.None);
        Assert.Equal(0, ocr.Frames.Open);

        // A start watches again from the stored readers.
        await ocr.Plugin.StartAsync(ocr.Context, CancellationToken.None);
        await Eventually.TrueAsync(() => ocr.Frames.Open == 1, "the watching reader watches after a start");
        await ocr.DisposeAsync();
        Assert.Equal(0, ocr.Frames.Open);
    }

    [Fact]
    public async Task WithoutAnAreaNothingIsRead()
    {
        // Text is read only inside areas: a watching reader without one opens no stream and the action refuses.
        await using var ocr = await OcrHarness.StartAsync();
        var bare = await ocr.SaveAsync(ocr.NewReader("Gate", ReaderMode.Watching) with { Areas = [] });
        await WatchAsync(ocr, ocr.NewReader("Other", ReaderMode.Watching, device: ocr.Outside.Id));

        Assert.False(ocr.Frames.IsStreaming(ocr.Inside.Id));
        Assert.DoesNotContain(bare.Id, ocr.Plugin.Runtime!.Watching);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => ocr.ReadOnceAsync(bare.Id));
        Assert.Equal(ReaderRules.NoAreas, refused.Message);
        Assert.Equal(0, ocr.Frames.Singles);
        Assert.Empty(ocr.TextRead);

        // An area drawn and saved: it watches.
        await WatchAsync(ocr, bare with { Areas = [OcrHarness.DefaultArea] });
        Assert.True(ocr.Frames.IsStreaming(ocr.Inside.Id));
    }

    [Fact]
    public async Task MoreThan30TextsCarryTheNoteOfTheReading()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var reader = await ocr.SaveAsync(ocr.NewReader("Gate"));
        ocr.Recognizer.TooManyTexts = true;
        ocr.Frames.Single = "A";

        var reply = await ocr.ReadOnceAsync(reader.Id);

        Assert.Equal("More than 30 texts found. Only the first 30 are read. Draw smaller areas.", reply.Reading!.Note);
        Assert.Equal(ReaderRules.TooManyTexts, reply.Reading.Note);
        ocr.Recognizer.TooManyTexts = false;
        Assert.Null((await ocr.ReadOnceAsync(reader.Id)).Reading!.Note);
    }

    [Fact]
    public async Task WithoutTheRecognitionPartsNothingReads()
    {
        var ctx = new Oadm.Sdk.Testing.FakeCoreContext();
        var device = ctx.Add(new Oadm.Sdk.Testing.FakeDevice { Address = "10.0.0.1" });
        await using var plugin = new OcrPlugin();
        await plugin.StartAsync(ctx, CancellationToken.None);
        var saved = OcrJson.Deserialize<SaveReaderReply>(await plugin.InvokeAsync(OcrMethods.SaveReader, OcrJson.Serialize(new SaveReaderRequest
        {
            Reader = new Reader { Name = "Gate", DeviceId = device.Id, Mode = ReaderMode.Watching },
        }), CancellationToken.None)).Reader!;

        Assert.Empty(plugin.Runtime!.Watching);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => plugin.ReadOnceAsync(plugin.FindReader(saved.Id)!, CancellationToken.None));
        Assert.Equal(ReaderRules.NotInstalled, refused.Message);
    }
}
