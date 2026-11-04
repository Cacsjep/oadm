using System.Collections.Concurrent;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Plugins.Ocr.Recognition;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Ocr.Tests.Readers;

/// <summary>
/// Unchanged areas while watching (docs/spec/ocr.md "Reading", unchanged areas): watching readers and previews ask the
/// recognizer to reuse unchanged areas, each with a source of its own; the action's single reading never does; a picture
/// whose areas were all unchanged counts apart from the pictures read.
/// </summary>
public sealed class UnchangedReuseTests
{
    /// <summary>The fakes' recognition with the detailed call: records every call's options; <see cref="Reused"/> areas reused.</summary>
    private sealed class DetailedFake : IDetailedTextRecognizer
    {
        private readonly FakeRecognizer _inner = new();

        public ConcurrentQueue<RecognitionOptions?> Calls { get; } = new();

        /// <summary>How many areas each detailed reading says it gave again (more than the areas: all).</summary>
        public int Reused { get; set; }

        public RecognitionDevice RunsOn { get; } = new(false, "CPU (8 cores)");

        public async Task<RecognizedPicture> ReadAsync(ReadOnlyMemory<byte> jpeg, IReadOnlyList<Area> areas, CancellationToken ct)
        {
            Calls.Enqueue(null);
            return await _inner.ReadAsync(jpeg, areas, ct);
        }

        public async Task<RecognitionResult> ReadDetailedAsync(ReadOnlyMemory<byte> jpeg, IReadOnlyList<Area> areas, RecognitionOptions options, CancellationToken ct)
        {
            Calls.Enqueue(options);
            var picture = await _inner.ReadAsync(jpeg, areas, ct);
            var perArea = picture.LinesPerArea.Select(lines => new AreaRecognition([.. lines.Select(l => new RecognizedLine(l, [], null))], 0)).ToList();
            return new RecognitionResult(picture.Resolution, perArea) { ReusedAreas = Math.Min(Reused, areas.Count) };
        }
    }

    private static async Task<OcrHarness> StartAsync(DetailedFake recognizer)
    {
        var ocr = new OcrHarness(recognizer: recognizer);
        await ocr.Plugin.StartAsync(ocr.Context, CancellationToken.None);
        return ocr;
    }

    [Fact]
    public async Task AWatchingReaderReusesUnchangedAreasUnderItsOwnId()
    {
        var recognizer = new DetailedFake();
        await using var ocr = await StartAsync(recognizer);
        var opened = ocr.Frames.Opened;
        var reader = await ocr.SaveAsync(ocr.NewReader("Gate", ReaderMode.Watching));
        await Eventually.TrueAsync(() => ocr.Frames.Opened > opened && ocr.Frames.Open > 0, "the reader watches");

        await ocr.SendAndWaitAsync(ocr.Inside.Id, "A");

        var options = Assert.Single(recognizer.Calls);
        Assert.NotNull(options);
        Assert.True(options.ReuseUnchanged);
        Assert.Equal(reader.Id, options.Source);
    }

    [Fact]
    public async Task APreviewReusesUnderItsOwnIdAndTheActionNever()
    {
        var recognizer = new DetailedFake();
        await using var ocr = await StartAsync(recognizer);
        var saved = await ocr.SaveAsync(ocr.NewReader("Gate"));

        // The action's single reading: every area read, no reuse.
        await ocr.ReadOnceAsync(saved.Id);
        Assert.Null(Assert.Single(recognizer.Calls));

        // A preview of the same reader: its own source, never the reader's.
        var reply = await ocr.PreviewAsync(saved);
        await Eventually.TrueAsync(() => ocr.Frames.Open > 0, "the preview watches");
        var before = ocr.PreviewReadings;
        ocr.Frames.Send(ocr.Inside.Id, "A");
        await Eventually.TrueAsync(() => ocr.PreviewReadings > before, "the preview read the frame");

        var options = recognizer.Calls.Last();
        Assert.NotNull(options);
        Assert.True(options.ReuseUnchanged);
        Assert.Equal("preview:" + reply.PreviewId, options.Source);
        Assert.NotEqual(saved.Id, options.Source);
    }

    [Fact]
    public async Task AnEditOnAHeldFrameIsNeverAnsweredWithTheOldReading()
    {
        var recognizer = new DetailedFake { Reused = int.MaxValue }; // every reuse call says: all areas unchanged
        await using var ocr = await StartAsync(recognizer);
        var draft = ocr.NewReader("Belt");
        var id = (await ocr.PreviewAsync(draft)).PreviewId;
        await Eventually.TrueAsync(() => ocr.Frames.Open > 0, "the preview watches");
        var before = ocr.PreviewReadings;
        ocr.Frames.Send(ocr.Inside.Id, "A");
        await Eventually.TrueAsync(() => ocr.PreviewReadings > before, "the preview read the frame");
        Assert.True(recognizer.Calls.Last()!.ReuseUnchanged); // live: reuse on
        await ocr.CallAsync(OcrMethods.PausePreview, new PreviewRequest { PreviewId = id });

        // The same pixels with another format, then another place: read anew each time, the new draft's result.
        foreach (var edited in new[]
        {
            draft with { Areas = [OcrHarness.DefaultArea with { Expected = ExpectedText.WithFormat("@") }] },
            draft with { Areas = [OcrHarness.DefaultArea with { Box = new(0.2, 0.2, 0.3, 0.3), Expected = ExpectedText.WithFormat("@") }] },
        })
        {
            var kept = ocr.PreviewReadings;
            var calls = recognizer.Calls.Count;
            await ocr.PreviewAsync(edited, id);
            await Eventually.TrueAsync(() => ocr.PreviewReadings > kept, "the held frame was read");
            Assert.Null(recognizer.Calls.Skip(calls).Single()); // the plain call: no options, no reuse
            var reply = OcrJson.Deserialize<ReadingReply>(await ocr.CallAsync(OcrMethods.GetPreview, new PreviewRequest { PreviewId = id }));
            Assert.True(Assert.Single(reply.Reading!.Areas).MatchesFormat);
        }
    }

    [Fact]
    public async Task APictureWithEveryAreaUnchangedCountsApartFromThePicturesRead()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero));
        var stats = new ReaderStats(time, () => TimeSpan.Zero, cores: 4);
        var recognizer = new DetailedFake();
        var maker = new ReadingMaker(recognizer, new FakeMatcher(), time, stats);
        var reader = new Reader
        {
            Id = "r1",
            Name = "Gate",
            Areas = [new Area { Name = "Left", Box = new(0, 0, 0.5, 1) }, new Area { Name = "Right", Box = new(0.5, 0, 0.5, 1) }],
        };
        stats.Sample(null);

        // 2 s: one picture read whole, one with one area unchanged (still read), two with both areas unchanged.
        await maker.ReadAsync(reader, Frame.Of("A"), "r1", CancellationToken.None);
        recognizer.Reused = 1;
        await maker.ReadAsync(reader, Frame.Of("A"), "r1", CancellationToken.None);
        recognizer.Reused = 2;
        var reused = await maker.ReadAsync(reader, Frame.Of("A"), "r1", CancellationToken.None);
        await maker.ReadAsync(reader, Frame.Of("A"), "r1", CancellationToken.None);
        time.Advance(ReaderStats.SampleInterval);
        stats.Sample(null);

        var figures = stats.Figures();
        Assert.Equal(1, figures.PicturesPerSecond, 6); // 2 pictures read in 2 s
        Assert.Equal(1, figures.ReusedPerSecond, 6); // 2 unchanged in 2 s
        Assert.All(reused.Reading.Areas, a => Assert.Equal("A", a.Text)); // the reused reading still gives the value
    }
}
