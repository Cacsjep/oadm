using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Matching;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Plugins.Ocr.Recognition;
using Oadm.Sdk.Testing;

using SkiaSharp;

namespace Oadm.Plugins.Ocr.Tests.Readers;

/// <summary>The last 20 reads (docs/spec/ocr.md "The last 20 reads") on the server.</summary>
public sealed class RecentReadsTests
{
    private static async Task<IReadOnlyList<RecentRead>> ReadsAsync(OcrHarness ocr, string readerId, FakeCallContext? caller = null) =>
        OcrJson.Deserialize<ReadingReply>(await ocr.CallAsync(OcrMethods.GetReading, new ReaderRequest { ReaderId = readerId }, caller)).Recent;

    private static async Task<IReadOnlyList<RecentRead>> PreviewReadsAsync(OcrHarness ocr, string previewId, FakeCallContext? caller = null) =>
        OcrJson.Deserialize<ReadingReply>(await ocr.CallAsync(OcrMethods.GetPreview, new PreviewRequest { PreviewId = previewId }, caller)).Recent;

    private static async Task ActionReadsAsync(OcrHarness ocr, string readerId, params string[] values)
    {
        foreach (var value in values)
        {
            ocr.Frames.Single = value;
            await ocr.ReadOnceAsync(readerId);
        }
    }

    private static async Task<OcrHarness> StartedAsync(IPictureCutter? cutter = null, ITextMatcher? matcher = null)
    {
        var ocr = new OcrHarness(cutter: cutter, matcher: matcher);
        await ocr.Plugin.StartAsync(ocr.Context, CancellationToken.None);
        return ocr;
    }

    private static async Task<string> PreviewingAsync(OcrHarness ocr, Reader draft, FakeCallContext? caller = null)
    {
        var opened = ocr.Frames.Opened;
        var reply = await ocr.PreviewAsync(draft, caller: caller);
        Assert.Null(reply.Problem);
        await Eventually.TrueAsync(() => ocr.Frames.Opened > opened && ocr.Frames.Open > 0, "the preview streams");
        return reply.PreviewId;
    }

    private static async Task SendToPreviewAsync(OcrHarness ocr, params string[] values)
    {
        foreach (var value in values)
        {
            var before = ocr.PreviewReadings;
            ocr.Frames.Send(ocr.Inside.Id, value);
            await Eventually.TrueAsync(() => ocr.PreviewReadings > before, "the preview read the frame");
        }
    }

    [Fact]
    public async Task TheListHoldsTwentyNewestFirstAndDropsTheOldestWithItsCutOut()
    {
        var cutter = new TextCutter();
        await using var ocr = await StartedAsync(cutter);
        var reader = await ocr.SaveAsync(ocr.NewReader("Gate"));
        var values = Enumerable.Range(1, OcrLimits.RecentReads + 5).Select(i => "V" + i).ToArray();

        await ActionReadsAsync(ocr, reader.Id, values);
        var rows = await ReadsAsync(ocr, reader.Id);

        Assert.Equal(20, OcrLimits.RecentReads);
        Assert.Equal(values.Reverse().Take(20), rows.Select(r => r.Text));
        Assert.Equal(rows.OrderByDescending(r => r.Id).Select(r => r.Id), rows.Select(r => r.Id));
        Assert.All(rows, r => Assert.Equal(r.Text, System.Text.Encoding.UTF8.GetString(r.Cutout!))); // each row its own picture
        Assert.Equal("Text", rows[0].Area);
        Assert.False(rows[0].HasFormat);
        Assert.Equal(90, rows[0].Confidence);
    }

    [Fact]
    public async Task ARowIsANewValueNotAFrame()
    {
        await using var ocr = await StartedAsync();
        var reader = await ocr.SaveAsync(ocr.NewReader("Gate"));

        await ActionReadsAsync(ocr, reader.Id, "A", "A", "A");
        Assert.Single(await ReadsAsync(ocr, reader.Id));

        await ActionReadsAsync(ocr, reader.Id, "B", "A");
        Assert.Equal(["A", "B", "A"], (await ReadsAsync(ocr, reader.Id)).Select(r => r.Text));
    }

    [Fact]
    public async Task AnEmptyValueOrANonMatchAddsNoRow()
    {
        await using var ocr = await StartedAsync(matcher: new TextMatcher());
        var reader = await ocr.SaveAsync(ocr.NewReader("Gate", areas: new Area { Name = "Number", Expected = ExpectedText.WithFormat("###"), Box = new RelativeBox(0, 0, 1, 1) }));

        await ActionReadsAsync(ocr, reader.Id, string.Empty, "ABC");
        Assert.Empty(await ReadsAsync(ocr, reader.Id));

        await ActionReadsAsync(ocr, reader.Id, "123");
        var row = Assert.Single(await ReadsAsync(ocr, reader.Id));
        Assert.Equal("123", row.Text);
        Assert.True(row.HasFormat);
        Assert.True(row.MatchesFormat);
    }

    [Fact]
    public async Task AWatchingReaderAddsARowWithEachEventAlsoWithThePageClosed()
    {
        await using var ocr = await StartedAsync();
        var reader = await ocr.SaveAsync(ocr.NewReader("Gate", ReaderMode.Watching));
        await Eventually.TrueAsync(() => ocr.Frames.Open == 1, "the reader watches");

        // The vote holds a value after 2 frames in a row; a frame alone is no row.
        await ocr.SendAndWaitAsync(ocr.Inside.Id, "A");
        Assert.Empty(ocr.TextRead);
        Assert.Empty(await ReadsAsync(ocr, reader.Id));
        foreach (var text in new[] { "A", "A", "A", "A", "A", "B", "B", "B", "B", "B", "A", "A", "A", "A", "A" })
        {
            await ocr.SendAndWaitAsync(ocr.Inside.Id, text);
        }

        var values = ocr.TextRead.Select(e => e.Data[OcrInfo.TextField]).ToList();
        Assert.Equal(["A", "B", "A"], values);
        Assert.Equal(["A", "B", "A"], (await ReadsAsync(ocr, reader.Id)).Select(r => r.Text));
    }

    [Fact]
    public async Task ThePreviewOfADraftHasItsOwnListAndASharedOneTheReaders()
    {
        await using var ocr = await StartedAsync();
        var saved = await ocr.SaveAsync(ocr.NewReader("Gate", ReaderMode.Watching));
        await Eventually.TrueAsync(() => ocr.Frames.Open == 1, "the reader watches");
        foreach (var text in new[] { "S", "S" })
        {
            await ocr.SendAndWaitAsync(ocr.Inside.Id, text);
        }

        // The unchanged draft shares the reader's stream: the reader's list.
        var shared = (await ocr.PreviewAsync(saved)).PreviewId;
        await Eventually.TrueAsync(() => ocr.Plugin.Runtime!.Previews.Single().SharedWith == saved.Id, "the stream is shared");
        Assert.Equal(["S"], (await PreviewReadsAsync(ocr, shared)).Select(r => r.Text));
        await ocr.CallAsync(OcrMethods.StopPreview, new PreviewRequest { PreviewId = shared });

        // A new draft: its own list from each frame, without a vote or an event; the saved reader's stays apart.
        var events = ocr.TextRead.Count;
        var draft = ocr.NewReader("Draft", ReaderMode.OnAction, device: ocr.Outside.Id);
        var id = (await ocr.PreviewAsync(draft)).PreviewId;
        await Eventually.TrueAsync(() => ocr.Frames.IsStreaming(ocr.Outside.Id), "the draft streams");
        foreach (var text in new[] { "D", "D", "E" })
        {
            var before = ocr.PreviewReadings;
            ocr.Frames.Send(ocr.Outside.Id, text);
            await Eventually.TrueAsync(() => ocr.PreviewReadings > before, "the preview read the frame");
        }

        Assert.Equal(["E", "D"], (await PreviewReadsAsync(ocr, id)).Select(r => r.Text));
        Assert.Equal(["S"], (await ReadsAsync(ocr, saved.Id)).Select(r => r.Text));
        Assert.Equal(events, ocr.TextRead.Count);

        // It goes with the preview.
        await ocr.CallAsync(OcrMethods.StopPreview, new PreviewRequest { PreviewId = id });
        Assert.Empty(await PreviewReadsAsync(ocr, id));
    }

    [Fact]
    public async Task APageOfASavedOnActionReaderSeesWhatTheActionRead()
    {
        await using var ocr = await StartedAsync();
        var reader = await ocr.SaveAsync(ocr.NewReader("Gate"));
        await ActionReadsAsync(ocr, reader.Id, "FROM RULE");

        var id = await PreviewingAsync(ocr, reader);
        await SendToPreviewAsync(ocr, "ON PAGE");

        Assert.Equal(["ON PAGE", "FROM RULE"], (await PreviewReadsAsync(ocr, id)).Select(r => r.Text));
    }

    [Fact]
    public async Task ADeletedReaderTakesItsListAlong()
    {
        await using var ocr = await StartedAsync(new TextCutter());
        var reader = await ocr.SaveAsync(ocr.NewReader("Gate"));
        await ActionReadsAsync(ocr, reader.Id, "A");
        Assert.NotNull(ocr.Plugin.Runtime!.RecentList(reader.Id));

        await ocr.CallAsync(OcrMethods.DeleteReader, new ReaderRequest { ReaderId = reader.Id });

        Assert.Null(ocr.Plugin.Runtime!.RecentList(reader.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => ReadsAsync(ocr, reader.Id));
    }

    [Fact]
    public async Task ReReadsOfAHeldFrameAddNothing()
    {
        await using var ocr = await StartedAsync();
        var draft = ocr.NewReader("Draft");
        var id = await PreviewingAsync(ocr, draft);
        await SendToPreviewAsync(ocr, "A");
        Assert.Single(await PreviewReadsAsync(ocr, id));
        await ocr.CallAsync(OcrMethods.PausePreview, new PreviewRequest { PreviewId = id });
        var kept = ocr.PreviewReadings;

        // A new area: the held frame is read again, with the same text under a new value in the new area.
        await ocr.PreviewAsync(draft with { Areas = [.. draft.Areas, new Area { Name = "Second", Box = new RelativeBox(0, 0, 0.5, 0.5) }] }, id);
        await Eventually.TrueAsync(() => ocr.PreviewReadings > kept, "the held frame was read again");

        Assert.Empty(await PreviewReadsAsync(ocr, id)); // while paused the page shows the held frame's result per area
        await ocr.CallAsync(OcrMethods.ResumePreview, new PreviewRequest { PreviewId = id });
        await Eventually.TrueAsync(() => ocr.Frames.Open > 0, "live again");
        Assert.Equal(["A"], (await PreviewReadsAsync(ocr, id)).Select(r => r.Text));
    }

    [Fact]
    public async Task TheListOfAnotherDeviceGroupIsNeverReached()
    {
        await using var ocr = await StartedAsync();
        var key = ocr.Context.FakeScopes.Add("Building B", [ocr.Inside.Id]);
        var group = FakeCallContext.WithGroup(ocr.Context, [ocr.Inside.Id], "operator", key);
        var other = FakeCallContext.WithGroup(ocr.Context, [ocr.Inside.Id], "other", ocr.Context.FakeScopes.Add("Building C", [ocr.Inside.Id]));
        var reader = await ocr.SaveAsync(ocr.NewReader("Gate"), group);
        await ActionReadsAsync(ocr, reader.Id, "SECRET");
        Assert.Single(await ReadsAsync(ocr, reader.Id, group));

        // Another group's reader answers like an unknown id, a user who no longer sees the camera is refused.
        await Assert.ThrowsAsync<ArgumentException>(() => ReadsAsync(ocr, reader.Id, other));
        var away = FakeCallContext.WithGroup(ocr.Context, [ocr.Outside.Id], "operator2", key);
        await Assert.ThrowsAnyAsync<Exception>(() => ReadsAsync(ocr, reader.Id, away));

        // Another user's preview answers like an ended one.
        var id = await PreviewingAsync(ocr, ocr.NewReader("Draft"), group);
        await SendToPreviewAsync(ocr, "MINE");
        Assert.Empty(await PreviewReadsAsync(ocr, id, other));
        Assert.Single(await PreviewReadsAsync(ocr, id, group));
    }

    [Fact]
    public async Task AUserWithoutLiveViewGetsTheRowsWithoutACutOut()
    {
        await using var ocr = await StartedAsync(new TextCutter());
        var reader = await ocr.SaveAsync(ocr.NewReader("Gate"));
        await ActionReadsAsync(ocr, reader.Id, "A");
        var payload = OcrJson.Serialize(new ReaderRequest { ReaderId = reader.Id });

        var without = new ReaderLifecycleTests.WithoutLiveView(ocr.Context.CallAsAllDevices("viewer"));
        var reply = OcrJson.Deserialize<ReadingReply>(await ocr.Plugin.InvokeAsync(without, OcrMethods.GetReading, payload, CancellationToken.None));

        Assert.Equal("A", Assert.Single(reply.Recent).Text);
        Assert.Null(reply.Recent[0].Cutout);
        Assert.NotNull(Assert.Single(await ReadsAsync(ocr, reader.Id)).Cutout);
    }

    [Fact]
    public async Task ACutOutComesOncePerRow()
    {
        await using var ocr = await StartedAsync(new TextCutter());
        var reader = await ocr.SaveAsync(ocr.NewReader("Gate"));
        await ActionReadsAsync(ocr, reader.Id, "A", "B");
        var rows = await ReadsAsync(ocr, reader.Id);

        var reply = OcrJson.Deserialize<ReadingReply>(await ocr.CallAsync(OcrMethods.GetReading, new ReaderRequest { ReaderId = reader.Id, KnownCutouts = [rows[0].Id] }));

        Assert.Null(reply.Recent[0].Cutout);
        Assert.NotNull(reply.Recent[1].Cutout);
    }

    [Fact]
    public async Task EachRowKeepsOnlyItsSmallCutOutNeverItsFrame()
    {
        var cutter = new TextCutter();
        await using var ocr = await StartedAsync(cutter);
        var reader = await ocr.SaveAsync(ocr.NewReader("Gate"));
        await ActionReadsAsync(ocr, reader.Id, "A", "B");
        var list = ocr.Plugin.Runtime!.RecentList(reader.Id)!;

        await Eventually.TrueAsync(() => cutter.Calls >= 2 && list.NextToCut() is null, "every row was cut");

        Assert.Equal(2, list.PictureBytes); // the cut-outs "A" and "B"; the frames went
    }

    [Fact]
    public void AListStaysBoundedWhateverTheCutOutsWeigh()
    {
        var list = new RecentReadList();
        for (var i = 0; i < 100; i++)
        {
            list.Add(new RecentRead { Id = i }, new byte[1024], new RelativeBox(0, 0, 1, 1));
            if (list.NextToCut() is { } row)
            {
                list.Finish(row, new byte[i % 2 == 0 ? OcrLimits.MaxCutoutBytes : OcrLimits.MaxCutoutBytes + 1]);
            }
        }

        Assert.Equal(OcrLimits.RecentReads, list.Count);
        Assert.InRange(list.PictureBytes, 0, OcrLimits.RecentReads * OcrLimits.MaxCutoutBytes);
        Assert.All(list.Rows(), r => Assert.True(r.Id % 2 == 0 ? r.Cutout is not null : r.Cutout is null)); // a longer one is not kept
    }

    [Fact]
    public void AnUnchangedValueAllocatesNothing()
    {
        var list = new RecentReadList();
        const string Value = "MSKU 123456 7";
        Assert.True(list.IsNewValue(0, Value));

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            Assert.False(list.IsNewValue(0, Value));
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void TheRealCutterKeepsARowWithinItsLimits()
    {
        // The worst picture for a JPEG: noise over the whole of full HD.
        using var noisy = new SKBitmap(new SKImageInfo(1920, 1080, SKColorType.Rgba8888, SKAlphaType.Premul));
        var random = new Random(7);
        var pixels = new byte[noisy.ByteCount];
        random.NextBytes(pixels);
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, noisy.GetPixels(), pixels.Length);
        using var image = SKImage.FromBitmap(noisy);
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 95);

        var cut = new PictureCutter().Cut(jpeg.ToArray(), new RelativeBox(0, 0, 1, 1));

        Assert.NotNull(cut);
        Assert.InRange(cut.Length, 1, OcrLimits.MaxCutoutBytes);
        using var back = SKBitmap.Decode(cut);
        Assert.InRange(Math.Max(back.Width, back.Height), 1, OcrLimits.MaxCutoutSide);
    }

    /// <summary>Cuts each value as its own text, so a test can tell the pictures apart.</summary>
    private sealed class TextCutter : IPictureCutter
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public byte[]? Cut(ReadOnlyMemory<byte> jpeg, RelativeBox box)
        {
            Interlocked.Increment(ref _calls);
            return jpeg.ToArray();
        }
    }
}
