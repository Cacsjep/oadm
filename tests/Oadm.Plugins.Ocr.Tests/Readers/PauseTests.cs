using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Ocr.Tests.Readers;

/// <summary>
/// Pause (docs/spec/ocr.md "Pause"): the server holds the frame of a preview's latest reading in memory, the draft re-reads
/// that frame, nothing reaches the rules, a saved reader goes on, and the frame goes with Resume, the lease or another picture.
/// </summary>
public sealed class PauseTests
{
    private static async Task<string> PreviewOnAsync(OcrHarness ocr, Reader draft, string frame, FakeCallContext? caller = null)
    {
        var reply = await ocr.PreviewAsync(draft, caller: caller);
        await Eventually.TrueAsync(() => ocr.Frames.Open > 0, "the preview streams");
        await SendAsync(ocr, draft.DeviceId, frame);
        return reply.PreviewId;
    }

    private static async Task SendAsync(OcrHarness ocr, Guid device, string frame)
    {
        var before = ocr.PreviewReadings;
        ocr.Frames.Send(device, frame);
        await Eventually.TrueAsync(() => ocr.PreviewReadings > before, "the preview read the frame");
    }

    private static async Task<PauseReply> PauseAsync(OcrHarness ocr, string id, FakeCallContext? caller = null) =>
        OcrJson.Deserialize<PauseReply>(await ocr.CallAsync(OcrMethods.PausePreview, new PreviewRequest { PreviewId = id }, caller));

    private static async Task<PreviewReply> ResumeAsync(OcrHarness ocr, string id, FakeCallContext? caller = null) =>
        OcrJson.Deserialize<PreviewReply>(await ocr.CallAsync(OcrMethods.ResumePreview, new PreviewRequest { PreviewId = id }, caller));

    private static async Task<ReadingReply> PreviewReadingAsync(OcrHarness ocr, string id) =>
        OcrJson.Deserialize<ReadingReply>(await ocr.CallAsync(OcrMethods.GetPreview, new PreviewRequest { PreviewId = id }));

    private static Reader WithFormat(Reader reader, string format) =>
        reader with { Areas = [.. reader.Areas.Select(a => a with { Expected = ExpectedText.WithFormat(format) })] };

    [Fact]
    public async Task PauseSaysWhenTheHeldFrameIsReadAgainAndEachReadingCarriesItsNumber()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var draft = ocr.NewReader("Belt", ReaderMode.Watching) with { MovingObjects = true };
        var id = await PreviewOnAsync(ocr, draft, "MSKU");

        // With Moving objects the frame's reading is the 2 frames rule's: the draft reads it once more. The reading is held.
        using var gate = new SemaphoreSlim(0);
        ocr.Recognizer.Gate = gate;
        var paused = await PauseAsync(ocr, id);

        Assert.True(paused.Paused);
        Assert.True(paused.Rereading);
        Assert.Equal(0, paused.HeldRead);

        var kept = ocr.PreviewReadings;
        ocr.Recognizer.Gate = null;
        gate.Release();
        await Eventually.TrueAsync(() => ocr.PreviewReadings > kept, "the frame was read");
        var told = OcrJson.Deserialize<PreviewEvent>(ocr.Context.PublishedEvents.Last(e => e.Topic == OcrMethods.PreviewTopic).PayloadJson);
        Assert.Equal(1, told.HeldRead);
        var after = await ocr.PreviewAsync(draft, id);
        Assert.False(after.Rereading);
        Assert.Equal(1, after.HeldRead);
    }

    [Fact]
    public async Task PauseAnswersTheFrameThatWasReadAndAnEditReadsThatSameFrame()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var draft = ocr.NewReader("Belt");
        var id = await PreviewOnAsync(ocr, draft, "MSKU");
        await SendAsync(ocr, ocr.Inside.Id, "TCLU");
        var frameTime = (await PreviewReadingAsync(ocr, id)).Reading!.Time;

        var paused = await PauseAsync(ocr, id);

        // The very picture of the latest reading, with its results; its own stream stops.
        Assert.True(paused.Paused);
        Assert.Equal("TCLU", Frame.TextOf(paused.Picture));
        Assert.Equal("TCLU", Assert.Single(paused.Reading!.Areas).Text);
        Assert.False(paused.Reading.Areas[0].MatchesFormat);
        await Eventually.TrueAsync(() => ocr.Frames.Open == 0, "the stream stopped");

        // Another format: the same frame again, its time kept, every area read.
        ocr.Time.Advance(TimeSpan.FromSeconds(3));
        var before = ocr.PreviewReadings;
        var edit = await ocr.PreviewAsync(WithFormat(draft, "@@@@"), id);
        Assert.True(edit.Paused);
        await Eventually.TrueAsync(() => ocr.PreviewReadings > before, "the held frame was read");
        var reading = (await PreviewReadingAsync(ocr, id)).Reading!;
        Assert.Equal("TCLU", Assert.Single(reading.Areas).Text);
        Assert.True(reading.Areas[0].MatchesFormat);
        Assert.Equal(frameTime, reading.Time);
        Assert.Equal(["MSKU", "TCLU", "TCLU"], ocr.Recognizer.Read);

        // A moved area and a new one too; a keep-alive with the same draft reads nothing.
        before = ocr.PreviewReadings;
        var moved = draft with { Areas = [new Area { Name = "Left", Box = new(0, 0, 0.5, 1) }, new Area { Name = "Right", Box = new(0.5, 0, 0.5, 1) }] };
        await ocr.PreviewAsync(moved, id);
        await Eventually.TrueAsync(() => ocr.PreviewReadings > before, "the new areas read the held frame");
        Assert.Equal(["Left", "Right"], (await PreviewReadingAsync(ocr, id)).Reading!.Areas.Select(a => a.Area));
        var read = ocr.Recognizer.Started;
        Assert.False((await ocr.PreviewAsync(moved, id)).Rereading);
        Assert.Equal(read, ocr.Recognizer.Started);

        // A preview publishes no rule events, paused or not.
        Assert.Empty(ocr.TextRead);
        Assert.Empty(ocr.Plugin.AllReaders);
    }

    [Fact]
    public async Task ResumeDropsTheFrameAndReadsLiveAgain()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var draft = ocr.NewReader("Belt");
        var id = await PreviewOnAsync(ocr, draft, "A");
        await PauseAsync(ocr, id);
        await Eventually.TrueAsync(() => ocr.Frames.Open == 0, "the stream stopped");

        var resumed = await ResumeAsync(ocr, id);

        Assert.False(resumed.Paused);
        Assert.Null(resumed.Problem);
        Assert.Null((await PreviewReadingAsync(ocr, id)).Reading); // the reading keeps the frame's bytes: it goes too
        Assert.False((await PauseAsync(ocr, id)).Paused); // nothing to pause on before the next picture
        await Eventually.TrueAsync(() => ocr.Frames.Open == 1, "the stream again");
        await SendAsync(ocr, ocr.Inside.Id, "B");
        Assert.Equal("B", Assert.Single((await PreviewReadingAsync(ocr, id)).Reading!.Areas).Text);
        Assert.False((await ocr.PreviewAsync(draft, id)).Paused);
    }

    [Fact]
    public async Task ASavedWatchingReaderKeepsRunningAndFiringWhileItsPreviewIsPaused()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var reader = await ocr.SaveAsync(ocr.NewReader("Gate", ReaderMode.Watching));
        await Eventually.TrueAsync(() => ocr.Frames.Open == 1, "the reader watches");
        var id = (await ocr.PreviewAsync(reader)).PreviewId;
        await ocr.SendAndWaitAsync(ocr.Inside.Id, "A");
        await ocr.SendAndWaitAsync(ocr.Inside.Id, "A");
        Assert.Equal(["A"], ocr.TextRead.Select(e => e.Data[OcrInfo.TextField]));

        var paused = await PauseAsync(ocr, id);
        Assert.Equal("A", Frame.TextOf(paused.Picture));
        Assert.Null(Assert.Single(ocr.Plugin.Runtime!.Previews).SharedWith);

        // The vote's value is no frame's own: the held frame is read once with the draft.
        await Eventually.TrueAsync(() => ocr.Recognizer.Started >= 3, "the frame's own reading");

        // The reader's stream stays open, its next value fires; the preview stays on its frame.
        Assert.Equal(1, ocr.Frames.Open);
        for (var i = 0; i < 4; i++)
        {
            await ocr.SendAndWaitAsync(ocr.Inside.Id, "B"); // the vote of the last 5 frames turns
        }

        Assert.Equal(["A", "B"], ocr.TextRead.Select(e => e.Data[OcrInfo.TextField]));
        Assert.Equal("B", ocr.Plugin.Runtime.Last(reader.Id)!.Reading.Areas[0].Text);
        Assert.Equal("A", Assert.Single((await PreviewReadingAsync(ocr, id)).Reading!.Areas).Text);

        // A format tried on the frame reaches neither the rules nor the reader.
        var events = ocr.TextRead.Count;
        var before = ocr.PreviewReadings;
        await ocr.PreviewAsync(WithFormat(reader, "@"), id);
        await Eventually.TrueAsync(() => ocr.PreviewReadings > before, "the frame was read");
        Assert.Equal(events, ocr.TextRead.Count);
        Assert.Equal(ExpectedTextKind.Any, ocr.Plugin.FindReader(reader.Id)!.Areas[0].Expected.Kind);

        // Resume with the reader as saved: shares its stream again.
        await ocr.PreviewAsync(reader, id);
        await ResumeAsync(ocr, id);
        Assert.Equal(reader.Id, Assert.Single(ocr.Plugin.Runtime.Previews).SharedWith);
    }

    [Fact]
    public async Task ABurstOfEditsReadsTheNewestDraftOnceAndNeverShowsAnOlderOne()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var draft = ocr.NewReader("Belt");
        var id = await PreviewOnAsync(ocr, draft, "A");
        await PauseAsync(ocr, id);

        // One reading in work, held at the gate; ten drafts arrive meanwhile.
        using var gate = new SemaphoreSlim(0);
        ocr.Recognizer.Gate = gate;
        var started = ocr.Recognizer.Started;
        var kept = ocr.PreviewReadings;
        Assert.True((await ocr.PreviewAsync(WithFormat(draft, "@"), id)).Rereading);
        await Eventually.TrueAsync(() => ocr.Recognizer.Started == started + 1, "the first reading started");
        for (var i = 1; i <= 10; i++)
        {
            await ocr.PreviewAsync(WithFormat(draft, new string('@', i + 1)), id);
        }

        Assert.Equal(started + 1, ocr.Recognizer.Started); // nothing queued beside it

        gate.Release(2);
        await Eventually.TrueAsync(() => ocr.PreviewReadings == kept + 1, "the newest draft read");
        Assert.Equal(started + 2, ocr.Recognizer.Started); // the first and the newest, no other

        // Its reading is the only one the page heard of.
        Assert.False((await ocr.PreviewAsync(WithFormat(draft, new string('@', 11)), id)).Rereading);
        ocr.Recognizer.Gate = null;
    }

    [Fact]
    public async Task ThePausedPreviewCountsAndEndsWithItsLease()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var draft = ocr.NewReader("Belt");
        var users = Enumerable.Range(0, 4).Select(i => ocr.Context.CallAsAllDevices($"user{i}")).ToArray();
        var id = await PreviewOnAsync(ocr, draft, "A", users[0]);
        await PauseAsync(ocr, id, users[0]);
        await Eventually.TrueAsync(() => ocr.Frames.Open == 0, "the stream stopped");

        // Still one of the 8 previews though it streams nothing.
        for (var i = 1; i < OcrLimits.MaxPreviews; i++)
        {
            await ocr.PreviewAsync(draft, caller: users[i / OcrLimits.MaxPreviewsPerUser]);
        }

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => ocr.PreviewAsync(draft, caller: ocr.Context.CallAsAllDevices("late")));
        Assert.Equal(ReaderRules.TooManyPreviews, refused.Message);

        // No call for 30 s: it ends with its frame like any preview.
        ocr.Time.Advance(OcrLimits.PreviewLease + ReaderRuntime.PreviewSweepInterval);
        await Eventually.TrueAsync(() => ocr.Plugin.Runtime!.Previews.Count == 0, "the leases ended");
        Assert.False((await PauseAsync(ocr, id, users[0])).Paused);

        // A keep-alive with the ended id: a new preview, live.
        Assert.False((await ocr.PreviewAsync(draft, id, users[0])).Paused);
    }

    [Fact]
    public async Task AnotherPictureOrStopEndsThePause()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var draft = ocr.NewReader("Belt");
        var id = await PreviewOnAsync(ocr, draft, "A");
        await PauseAsync(ocr, id);

        // Name, mode and rate change no picture: still paused.
        Assert.True((await ocr.PreviewAsync(draft with { Name = "Line 2", Mode = ReaderMode.Watching, Fps = 4 }, id)).Paused);

        // Another size: that frame is no picture of this reader; it reads live.
        var other = await ocr.PreviewAsync(draft with { Resolution = new(1280, 720) }, id);
        Assert.False(other.Paused);
        Assert.Null((await PreviewReadingAsync(ocr, id)).Reading);
        await Eventually.TrueAsync(() => ocr.Frames.Open == 1, "the stream again");

        await SendAsync(ocr, ocr.Inside.Id, "B");
        await PauseAsync(ocr, id);
        await ocr.CallAsync(OcrMethods.StopPreview, new PreviewRequest { PreviewId = id });
        Assert.Empty(ocr.Plugin.Runtime!.Previews);
        Assert.False((await PauseAsync(ocr, id)).Paused);
    }

    [Fact]
    public async Task AnotherCameraUnderTheSameIdKeepsNothingOfTheOldOne()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var draft = ocr.NewReader("Belt");
        var id = await PreviewOnAsync(ocr, draft, "A");
        Assert.NotNull((await PreviewReadingAsync(ocr, id)).Reading);

        var other = await ocr.PreviewAsync(draft with { DeviceId = ocr.Outside.Id }, id);

        // Live too: the old camera's reading, rows and picture never answer for the new one, whatever its first frame takes.
        Assert.Equal(id, other.PreviewId);
        var reply = await PreviewReadingAsync(ocr, id);
        Assert.Null(reply.Reading);
        Assert.Empty(reply.Recent);
        Assert.Equal(ReaderRules.NothingToPause, (await PauseAsync(ocr, id)).Problem);
        Assert.Single(ocr.Plugin.Runtime!.Previews);
    }

    [Fact]
    public async Task ASavedReaderDeletedWhilePausedEndsThePause()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var reader = await ocr.SaveAsync(ocr.NewReader("Belt"));
        var id = await PreviewOnAsync(ocr, reader, "A");
        await PauseAsync(ocr, id);

        await ocr.CallAsync(OcrMethods.DeleteReader, new ReaderRequest { ReaderId = reader.Id });

        Assert.Null((await PreviewReadingAsync(ocr, id)).Reading);
    }

    [Fact]
    public async Task ThePictureGoesOnlyToUsersWithLiveViewAndTheirDevices()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var draft = ocr.NewReader("Belt");
        var id = await PreviewOnAsync(ocr, draft, "A");
        var payload = OcrJson.Serialize(new PreviewRequest { PreviewId = id });

        // Without Live view: refused, nothing held.
        var viewer = new ReaderLifecycleTests.WithoutLiveView(ocr.Context.CallAsAllDevices());
        var refused = await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => ocr.Plugin.InvokeAsync(viewer, OcrMethods.PausePreview, payload, CancellationToken.None));
        Assert.Equal(ReaderRules.PauseNeedsLiveView, refused.Message);
        Assert.Equal(1, ocr.Frames.Open);

        // Another user's preview answers like an ended one.
        Assert.False((await PauseAsync(ocr, id, ocr.Context.CallAsAllDevices("other"))).Paused);

        // Live view taken away while paused: the next keep-alive drops the frame.
        Assert.True((await PauseAsync(ocr, id)).Paused);
        var keep = OcrJson.Serialize(new PreviewRequest { PreviewId = id, Reader = draft });
        var after = OcrJson.Deserialize<PreviewReply>(await ocr.Plugin.InvokeAsync(viewer, OcrMethods.StartPreview, keep, CancellationToken.None));
        Assert.False(after.Paused);
        Assert.Null((await PreviewReadingAsync(ocr, id)).Reading);
    }

    [Fact]
    public async Task ACameraThatLeavesTheGroupEndsThePause()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var key = ocr.Context.FakeScopes.Add("Building B", [ocr.Inside.Id]);
        var group = FakeCallContext.WithGroup(ocr.Context, [ocr.Inside.Id], "operator", key);
        var id = await PreviewOnAsync(ocr, ocr.NewReader("Belt"), "A", group);
        Assert.True((await PauseAsync(ocr, id, group)).Paused);

        ocr.Context.FakeScopes.Delete(key);

        await Eventually.TrueAsync(
            () => ocr.Plugin.Runtime!.PreviewOf(id, key, group.UserName) is { Last: null },
            "the frame went with the group");
    }

    [Fact]
    public async Task ThereIsNothingToPauseWithoutAReadingOrBeyondThePictureLimits()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var id = (await ocr.PreviewAsync(ocr.NewReader("Belt"))).PreviewId;
        var none = await PauseAsync(ocr, id);
        Assert.False(none.Paused);
        Assert.Equal(ReaderRules.NothingToPause, none.Problem);

        await using var limited = new OcrHarness(pictureFits: _ => false);
        await limited.Plugin.StartAsync(limited.Context, CancellationToken.None);
        var big = await PreviewOnAsync(limited, limited.NewReader("Belt"), "A");
        var refused = await PauseAsync(limited, big);
        Assert.False(refused.Paused);
        Assert.Equal(ReaderRules.PictureTooLarge, refused.Problem);
        Assert.Null(refused.Picture);
        Assert.Equal(1, limited.Frames.Open); // it goes on live
    }

    [Fact]
    public async Task AFailedReadingOfTheHeldFrameLeavesThePageAsItWas()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var draft = ocr.NewReader("Belt");
        var id = await PreviewOnAsync(ocr, draft, "A");
        await PauseAsync(ocr, id);

        // The failure is logged, the page keeps its results, and the next edit reads again.
        var before = ocr.PreviewReadings;
        ocr.Recognizer.FailNext = new InvalidOperationException("no");
        await ocr.PreviewAsync(WithFormat(draft, "@"), id);
        await Eventually.TrueAsync(async () => !(await ocr.PreviewAsync(WithFormat(draft, "@"), id)).Rereading, "the failed reading ended");
        Assert.Equal(before, ocr.PreviewReadings);
        await ocr.PreviewAsync(WithFormat(draft, "@@"), id);
        await Eventually.TrueAsync(() => ocr.PreviewReadings > before, "the next edit reads");
    }
}
