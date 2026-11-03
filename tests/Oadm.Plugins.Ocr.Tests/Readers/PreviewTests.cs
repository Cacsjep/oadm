using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Ocr.Tests.Readers;

/// <summary>
/// The page's preview (docs/spec/ocr.md "Page"): the shown reader is read live, saved or not, without rule events; a lease
/// ends it 30 s after the last call; at most 8 per server, counted against the watching limits.
/// </summary>
public sealed class PreviewTests
{
    private static async Task<string> PreviewingAsync(OcrHarness ocr, Reader draft, FakeCallContext? caller = null)
    {
        var opened = ocr.Frames.Opened;
        var reply = await ocr.PreviewAsync(draft, caller: caller);
        Assert.Null(reply.Problem);
        await Eventually.TrueAsync(() => ocr.Frames.Opened > opened && ocr.Frames.Open > 0, "the preview streams");
        return reply.PreviewId;
    }

    private static async Task SendAndWaitPreviewAsync(OcrHarness ocr, Guid deviceId, string text)
    {
        var before = ocr.PreviewReadings;
        ocr.Frames.Send(deviceId, text);
        await Eventually.TrueAsync(() => ocr.PreviewReadings > before, "the preview read the frame");
    }

    [Fact]
    public async Task APreviewReadsTheDraftLiveAndPublishesNoRuleEvents()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var draft = ocr.NewReader("Gate", ReaderMode.OnAction, areas: new Area { Name = "Number", Box = new(0.1, 0.1, 0.5, 0.5) });

        var id = await PreviewingAsync(ocr, draft); // never saved
        Assert.Equal([OcrLimits.PreviewFpsOnAction], ocr.Frames.Rates); // On action: 1 per second
        foreach (var text in new[] { "A", "A", "B" })
        {
            await SendAndWaitPreviewAsync(ocr, ocr.Inside.Id, text);
        }

        // Every frame's own result (no vote), no rule event, no reading of a saved reader.
        Assert.Empty(ocr.TextRead);
        Assert.Equal(0, ocr.Readings);
        Assert.Empty(ocr.Plugin.AllReaders);
        var live = OcrJson.Deserialize<PreviewEvent>(ocr.Context.PublishedEvents.Last(e => e.Topic == OcrMethods.PreviewTopic).PayloadJson);
        Assert.Equal(id, live.PreviewId);
        Assert.Equal("B", Assert.Single(live.Reading.Areas).Text);
        var reply = OcrJson.Deserialize<ReadingReply>(await ocr.CallAsync(OcrMethods.GetPreview, new PreviewRequest { PreviewId = id }));
        Assert.Equal("Number", Assert.Single(reply.Reading!.Areas).Area);
        Assert.DoesNotContain("jpeg", OcrJson.Serialize(reply), StringComparison.OrdinalIgnoreCase); // the live video shows the picture

        // A keep-alive with other areas takes them without opening the stream again.
        var opened = ocr.Frames.Opened;
        var again = await ocr.PreviewAsync(draft with { Areas = [new Area { Name = "Left", Box = new(0, 0, 0.5, 1) }, new Area { Name = "Right", Box = new(0.5, 0, 0.5, 1) }] }, id);
        Assert.Equal(id, again.PreviewId);
        await SendAndWaitPreviewAsync(ocr, ocr.Inside.Id, "C");
        reply = OcrJson.Deserialize<ReadingReply>(await ocr.CallAsync(OcrMethods.GetPreview, new PreviewRequest { PreviewId = id }));
        Assert.Equal(["Left", "Right"], reply.Reading!.Areas.Select(a => a.Area));
        Assert.Equal(opened, ocr.Frames.Opened);

        // Another rate opens it again; Stop ends it.
        await ocr.PreviewAsync(draft with { Mode = ReaderMode.Watching, Fps = 4 }, id);
        await Eventually.TrueAsync(() => ocr.Frames.Rates.Contains(4) && ocr.Frames.Open == 1, "the stream at the new rate");
        await ocr.CallAsync(OcrMethods.StopPreview, new PreviewRequest { PreviewId = id });
        Assert.Equal(0, ocr.Frames.Open);
        Assert.Empty(ocr.Plugin.Runtime!.Previews);
        Assert.Null(OcrJson.Deserialize<ReadingReply>(await ocr.CallAsync(OcrMethods.GetPreview, new PreviewRequest { PreviewId = id })).Reading);
        Assert.Empty(ocr.TextRead);
    }

    [Fact]
    public async Task APreviewWithoutAnAreaReadsNothingUntilOneIsDrawn()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var draft = ocr.NewReader("Gate", ReaderMode.Watching) with { Areas = [] };

        var reply = await ocr.PreviewAsync(draft);

        // Kept alive, no problem, but no stream and no reading: the page shows "Start by drawing an area".
        Assert.Null(reply.Problem);
        var preview = Assert.Single(ocr.Plugin.Runtime!.Previews);
        Assert.False(preview.OwnStream);
        Assert.Equal(0, ocr.Frames.Opened);
        Assert.Equal(0, ocr.PreviewReadings);

        // The first area: the stream opens and the preview reads.
        await ocr.PreviewAsync(draft with { Areas = [OcrHarness.DefaultArea] }, reply.PreviewId);
        await Eventually.TrueAsync(() => ocr.Frames.Open == 1, "the preview streams once an area exists");
        await SendAndWaitPreviewAsync(ocr, ocr.Inside.Id, "A");

        // The area removed again: the stream stops.
        await ocr.PreviewAsync(draft, reply.PreviewId);
        Assert.Equal(0, ocr.Frames.Open);
        Assert.Empty(ocr.TextRead);
    }

    [Fact]
    public async Task APreviewEndsThirtySecondsAfterTheLastKeepAlive()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var draft = ocr.NewReader("Gate");
        var id = await PreviewingAsync(ocr, draft);

        // Kept alive after 25 s: still there 25 s later.
        ocr.Time.Advance(TimeSpan.FromSeconds(25));
        Assert.Equal(id, (await ocr.PreviewAsync(draft, id)).PreviewId);
        ocr.Time.Advance(TimeSpan.FromSeconds(25));
        Assert.Single(ocr.Plugin.Runtime!.Previews);
        Assert.Equal(1, ocr.Frames.Open);

        // No call for 30 s: the next sweep ends it and stops its stream.
        ocr.Time.Advance(TimeSpan.FromSeconds(10));
        await Eventually.TrueAsync(() => ocr.Plugin.Runtime.Previews.Count == 0 && ocr.Frames.Open == 0, "the lease ended");

        // A keep-alive with the ended id starts a new preview under that id (the page's own).
        var reply = await ocr.PreviewAsync(draft, id);
        Assert.Equal(id, reply.PreviewId);
        Assert.Single(ocr.Plugin.Runtime.Previews);
    }

    [Fact]
    public async Task AtMostEightPreviewsAndTheyCountAgainstTheWatchingLimit()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var draft = ocr.NewReader("Gate");
        var ids = new List<string>();
        for (var i = 0; i < OcrLimits.MaxPreviews; i++)
        {
            ids.Add((await ocr.PreviewAsync(draft)).PreviewId);
        }

        await Eventually.TrueAsync(() => ocr.Frames.Opened == OcrLimits.MaxPreviews, "every preview streams");
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => ocr.PreviewAsync(draft));
        Assert.Equal(ReaderRules.TooManyPreviews, refused.Message);
        Assert.Equal("At most 8 readers can be shown live at once. Try again when another page closes.", refused.Message);

        // One ends: room for another.
        await ocr.CallAsync(OcrMethods.StopPreview, new PreviewRequest { PreviewId = ids[0] });
        Assert.NotEmpty((await ocr.PreviewAsync(draft)).PreviewId);

        // The watching readers and the own streams of the previews share the limit (20 on the CPU).
        for (var i = 0; i < OcrLimits.MaxWatching - OcrLimits.MaxPreviews; i++)
        {
            await ocr.SaveAsync(ocr.NewReader($"Watch {i:00}", ReaderMode.Watching));
        }

        await Eventually.TrueAsync(() => ocr.Plugin.Runtime!.Watching.Count == OcrLimits.MaxWatching - OcrLimits.MaxPreviews, "the readers watch");
        await ocr.CallAsync(OcrMethods.StopPreview, new PreviewRequest { PreviewId = ids[1] });
        await ocr.SaveAsync(ocr.NewReader("Watch 99", ReaderMode.Watching));
        await Eventually.TrueAsync(() => ocr.Plugin.Runtime!.Watching.Count == OcrLimits.MaxWatching - OcrLimits.MaxPreviews + 1, "the new reader watches");
        var busy = await ocr.PreviewAsync(draft);
        Assert.Equal(ReaderRules.PreviewBusy, busy.Problem);
        Assert.DoesNotContain(ocr.Plugin.Runtime!.Previews, p => p.Id == busy.PreviewId && p.OwnStream);
        Assert.Empty(ocr.TextRead);
    }

    [Fact]
    public async Task AnUnchangedWatchingReaderSharesItsStreamAndAChangedDraftGetsItsOwn()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var reader = await ocr.SaveAsync(ocr.NewReader("Gate", ReaderMode.Watching));
        await Eventually.TrueAsync(() => ocr.Frames.Open == 1, "the reader watches");

        var reply = await ocr.PreviewAsync(reader);
        var preview = Assert.Single(ocr.Plugin.Runtime!.Previews);
        Assert.Equal((false, reader.Id), (preview.OwnStream, preview.SharedWith));
        Assert.Equal(1, ocr.Frames.Opened);

        // The reader's readings are the preview's; the reader's own events go on.
        await SendAndWaitPreviewAsync(ocr, ocr.Inside.Id, "A");
        await SendAndWaitPreviewAsync(ocr, ocr.Inside.Id, "A");
        Assert.Equal(["A"], ocr.TextRead.Select(e => e.Data[OcrInfo.TextField]));
        var live = OcrJson.Deserialize<PreviewEvent>(ocr.Context.PublishedEvents.Last(e => e.Topic == OcrMethods.PreviewTopic).PayloadJson);
        Assert.Equal(reply.PreviewId, live.PreviewId);

        // An area drawn on the page: the draft reads on a stream of its own, without events.
        await ocr.PreviewAsync(reader with { Areas = [new Area { Name = "Number", Box = new(0, 0, 0.5, 0.5) }] }, reply.PreviewId);
        await Eventually.TrueAsync(() => ocr.Frames.Opened == 2, "the draft's own stream");
        Assert.True(Assert.Single(ocr.Plugin.Runtime.Previews).OwnStream);
    }

    [Fact]
    public async Task APreviewBelongsToItsDeviceScope()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var key = ocr.Context.FakeScopes.Add("Building B", [ocr.Inside.Id]);
        var group = FakeCallContext.WithGroup(ocr.Context, [ocr.Inside.Id], "operator", key);
        var other = FakeCallContext.WithGroup(ocr.Context, [ocr.Inside.Id], "other", ocr.Context.FakeScopes.Add("Building C", [ocr.Inside.Id]));
        var id = await PreviewingAsync(ocr, ocr.NewReader("Gate"), group);

        // Only its group hears of it and reaches it.
        await SendAndWaitPreviewAsync(ocr, ocr.Inside.Id, "A");
        Assert.Contains(ocr.Context.EventsSeenBy(group), e => e.Topic == OcrMethods.PreviewTopic);
        Assert.DoesNotContain(ocr.Context.EventsSeenBy(other), e => e.Topic == OcrMethods.PreviewTopic);
        Assert.Null(OcrJson.Deserialize<ReadingReply>(await ocr.CallAsync(OcrMethods.GetPreview, new PreviewRequest { PreviewId = id }, other)).Reading);
        Assert.NotNull(OcrJson.Deserialize<ReadingReply>(await ocr.CallAsync(OcrMethods.GetPreview, new PreviewRequest { PreviewId = id }, group)).Reading);
        await ocr.CallAsync(OcrMethods.StopPreview, new PreviewRequest { PreviewId = id }, other);
        Assert.Single(ocr.Plugin.Runtime!.Previews);

        // A camera outside the group and another group's reader are refused.
        await Assert.ThrowsAnyAsync<Exception>(() => ocr.PreviewAsync(ocr.NewReader("Lobby", device: ocr.Outside.Id), caller: group));
        var admins = await ocr.SaveAsync(ocr.NewReader("Lobby"));
        var foreign = await Assert.ThrowsAsync<ArgumentException>(() => ocr.PreviewAsync(admins, caller: group));
        Assert.Equal(ReaderRules.Gone, foreign.Message);

        await ocr.CallAsync(OcrMethods.StopPreview, new PreviewRequest { PreviewId = id }, group);
        Assert.Empty(ocr.Plugin.Runtime.Previews);
    }

    [Fact]
    public void ThePreviewReadsWhatTheDraftAllows()
    {
        var device = Guid.NewGuid();
        var draft = new Reader
        {
            Name = " ",
            DeviceId = device,
            Mode = ReaderMode.Watching,
            Fps = 15,
            Areas =
            [
                new Area { Name = "", Box = new(0.1, 0.1, 0.2, 0.2), Expected = ExpectedText.WithFormat("12") },
                new Area { Name = "Out", Box = new(0.9, 0.9, 0.5, 0.5) },
                new Area { Name = "Code", Box = new(0.5, 0.5, 0.2, 0.2), Expected = ExpectedText.WithFormat("A#") },
            ],
        };

        var cpu = ReaderRules.PreviewOf(draft, onGpu: false);
        Assert.Equal(OcrLimits.DefaultFps, cpu.Fps); // 15 only on a graphics card
        Assert.Equal(15, ReaderRules.PreviewOf(draft, onGpu: true).Fps);
        Assert.Equal(12, ReaderRules.PreviewOf(draft with { Fps = 12 }, onGpu: true).Fps);
        Assert.Equal(["Area 1", "Code"], cpu.Areas.Select(a => a.Name)); // outside the picture: left out
        Assert.Equal(ExpectedTextKind.Any, cpu.Areas[0].Expected.Kind); // a format with a problem reads any text
        Assert.Equal("A#", cpu.Areas[1].Expected.Format);
        Assert.Equal(OcrLimits.PreviewFpsOnAction, ReaderRules.PreviewOf(draft with { Mode = ReaderMode.OnAction }, onGpu: true).Fps);
        Assert.Equal(ReaderRules.CameraMissing, Assert.Throws<ArgumentException>(() => ReaderRules.PreviewOf(draft with { DeviceId = Guid.Empty }, false)).Message);
        Assert.Equal(ReaderRules.ResolutionMissing, Assert.Throws<ArgumentException>(() => ReaderRules.PreviewOf(draft with { Resolution = new(10, 10) }, false)).Message);
    }
}
