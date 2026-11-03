using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Ocr.Tests.Readers;

/// <summary>
/// The runtime's leaks and races (docs/spec/ocr.md "Readers", "Page"): the newest stored list always wins, a stop waits for
/// the single readings, cut-outs only when a page asks, the pages hear of readings only on change or once a second, and a
/// preview's readings go only to its scope.
/// </summary>
public sealed class RuntimeSafetyTests
{
    [Fact]
    public async Task ADeletedReaderNeverStreamsAgainWhateverSyncRunsLate()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var reader = await ocr.SaveAsync(ocr.NewReader("Gate", ReaderMode.Watching));
        await Eventually.TrueAsync(() => ocr.Frames.Open == 1, "the reader watches");

        // A sync that started before the delete (the 1-minute check, a group change) reads the list under its lock: the
        // reader stays gone, also after the next checks.
        var late = ocr.Plugin.Runtime!.SyncAsync();
        await ocr.CallAsync(OcrMethods.DeleteReader, new ReaderRequest { ReaderId = reader.Id });
        await late;
        await ocr.Plugin.Runtime.SyncAsync();
        ocr.Time.Advance(ReaderRuntime.RecheckInterval);
        await ocr.Plugin.Runtime.SyncAsync();

        Assert.Equal(0, ocr.Frames.Open);
        Assert.Empty(ocr.Plugin.Runtime.Watching);

        // Saves and deletes racing each other end with exactly the stored watching readers streaming.
        var saves = Enumerable.Range(0, 6).Select(i => ocr.SaveAsync(ocr.NewReader($"Gate {i}", ReaderMode.Watching))).ToList();
        var saved = await Task.WhenAll(saves);
        await Task.WhenAll(saved.Where((_, i) => i % 2 == 0).Select(r => ocr.CallAsync(OcrMethods.DeleteReader, new ReaderRequest { ReaderId = r.Id })));
        await ocr.Plugin.Runtime.SyncAsync();
        Assert.Equal(saved.Where((_, i) => i % 2 == 1).Select(r => r.Id).Order(), ocr.Plugin.Runtime.Watching.Order());
    }

    [Fact]
    public async Task AStopCancelsAndAwaitsTheSingleReadingsFirst()
    {
        var ocr = await OcrHarness.StartAsync();
        var reader = await ocr.SaveAsync(ocr.NewReader("Gate"));
        ocr.Recognizer.Gate = new SemaphoreSlim(0);
        ocr.Frames.Single = "A";

        var reading = ocr.Plugin.ReadOnceAsync(ocr.Plugin.FindReader(reader.Id)!, CancellationToken.None);
        await Eventually.TrueAsync(() => ocr.Recognizer.Started == 1, "the reading is in the recognition");
        var runtime = ocr.Plugin.Runtime!;

        await ocr.DisposeAsync(); // the gate is never released: only the stop's cancellation ends the reading

        Assert.True(reading.IsCompleted, "the stop waited for the reading");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        Assert.Empty(ocr.TextRead);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ReadOnceAsync(reader, CancellationToken.None));
        Assert.Equal(ReaderRules.NotRunning, refused.Message);
    }

    [Fact]
    public async Task CutOutsAreCutOnlyWhenAPageAsksAndOncePerReading()
    {
        var cutter = new CountingCutter();
        await using var ocr = new OcrHarness(cutter: cutter);
        await ocr.Plugin.StartAsync(ocr.Context, CancellationToken.None);
        var reader = await ocr.SaveAsync(ocr.NewReader("Gate", ReaderMode.Watching));
        await Eventually.TrueAsync(() => ocr.Frames.Open == 1, "the reader watches");
        for (var i = 0; i < 4; i++)
        {
            await ocr.SendAndWaitAsync(ocr.Inside.Id, "A");
        }

        Assert.Equal(0, cutter.Calls); // never per frame

        var first = OcrJson.Deserialize<ReadingReply>(await ocr.CallAsync(OcrMethods.GetReading, new ReaderRequest { ReaderId = reader.Id }));
        var again = OcrJson.Deserialize<ReadingReply>(await ocr.CallAsync(OcrMethods.GetReading, new ReaderRequest { ReaderId = reader.Id }));

        Assert.Equal(1, cutter.Calls);
        Assert.Equal([7, 7], Assert.Single(first.Details).Cutout);
        Assert.Equal([7, 7], Assert.Single(again.Details).Cutout);
    }

    [Fact]
    public async Task ThePagesHearOfAReadingOnlyWhenItChangesOrOnceASecond()
    {
        await using var ocr = await OcrHarness.StartAsync();
        await ocr.SaveAsync(ocr.NewReader("Gate", ReaderMode.Watching) with { Fps = 4 });
        await Eventually.TrueAsync(() => ocr.Frames.Open == 1, "the reader watches");
        int Told() => ocr.Context.PublishedEvents.Count(e => e.Topic == OcrMethods.ReadingTopic && e.Audience.Kind == PluginEventAudienceKind.AllDevices);

        // Five frames with the same value in the same second: the vote changes the value once, the pages hear of it once more.
        for (var i = 0; i < 5; i++)
        {
            await ocr.SendAndWaitAsync(ocr.Inside.Id, "A");
        }

        var told = Told();
        Assert.InRange(told, 1, 2);
        Assert.Equal(5, ocr.Readings);

        // A second later the unchanged reading is told again; a change is told at once.
        ocr.Time.Advance(ReaderRuntime.UnchangedReadingInterval);
        await ocr.SendAndWaitAsync(ocr.Inside.Id, "A");
        Assert.Equal(told + 1, Told());
        for (var i = 0; i < 5; i++)
        {
            await ocr.SendAndWaitAsync(ocr.Inside.Id, "B"); // the vote turns to B within these frames
        }

        Assert.True(Told() > told + 1);
        Assert.Equal("B", OcrJson.Deserialize<ReadingEvent>(ocr.Context.PublishedEvents.Last(e => e.Topic == OcrMethods.ReadingTopic).PayloadJson).Reading.Areas[0].Text);
    }

    [Fact]
    public async Task APreviewIsToldOnlyToItsUser()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var key = ocr.Context.FakeScopes.Add("Building B", [ocr.Inside.Id]);
        var group = FakeCallContext.WithGroup(ocr.Context, [ocr.Inside.Id], "operator", key);
        var reply = await ocr.PreviewAsync(ocr.NewReader("Gate"), caller: group);
        await Eventually.TrueAsync(() => ocr.Frames.Open == 1, "the preview streams");

        var before = ocr.PreviewReadings;
        ocr.Frames.Send(ocr.Inside.Id, "A");
        await Eventually.TrueAsync(() => ocr.PreviewReadings > before, "the preview read the frame");

        var told = ocr.Context.PublishedEvents.Where(e => e.Topic == OcrMethods.PreviewTopic).ToList();
        Assert.NotEmpty(told);
        Assert.All(told, e => Assert.Equal((PluginEventAudienceKind.User, "operator"), (e.Audience.Kind, e.Audience.UserName))); // only its user
        Assert.Equal(reply.PreviewId, OcrJson.Deserialize<PreviewEvent>(told[^1].PayloadJson).PreviewId);
    }

    [Fact]
    public async Task ThePagesOwnPreviewIdIsKeptSoItCanAlwaysEndIt()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var id = Guid.NewGuid().ToString("N");

        // The stop overtook the start (the page left at once): the start still makes the preview under the page's id, and the
        // page's second stop with that id ends it.
        await ocr.CallAsync(OcrMethods.StopPreview, new PreviewRequest { PreviewId = id });
        Assert.Equal(id, (await ocr.PreviewAsync(ocr.NewReader("Gate"), id)).PreviewId);
        await ocr.CallAsync(OcrMethods.StopPreview, new PreviewRequest { PreviewId = id });
        Assert.Empty(ocr.Plugin.Runtime!.Previews);

        // Not a page's id: the server makes one.
        Assert.NotEqual("p1", (await ocr.PreviewAsync(ocr.NewReader("Gate"), "p1")).PreviewId);
    }

    private sealed class CountingCutter : IPictureCutter
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public byte[]? Cut(ReadOnlyMemory<byte> jpeg, RelativeBox box)
        {
            Interlocked.Increment(ref _calls);
            return [7, 7];
        }
    }
}
