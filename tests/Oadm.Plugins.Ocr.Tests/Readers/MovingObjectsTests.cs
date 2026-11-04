using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Ocr.Tests.Readers;

/// <summary>Moving objects on a running reader and a preview, and the frame a row's cut-out comes from (docs/spec/ocr.md "Moving objects", "The last 20 reads").</summary>
public sealed class MovingObjectsTests
{
    /// <summary>Cuts each value as its frame's text, so a test can tell the frames apart.</summary>
    private sealed class FrameCutter : IPictureCutter
    {
        public byte[]? Cut(ReadOnlyMemory<byte> jpeg, RelativeBox box) => jpeg.ToArray();
    }

    private static async Task<OcrHarness> StartedAsync()
    {
        var ocr = new OcrHarness(cutter: new FrameCutter());
        await ocr.Plugin.StartAsync(ocr.Context, CancellationToken.None);
        return ocr;
    }

    private static async Task<Reader> WatchingAsync(OcrHarness ocr, bool moving)
    {
        var reader = await ocr.SaveAsync(ocr.NewReader("Belt", ReaderMode.Watching) with { MovingObjects = moving });
        await Eventually.TrueAsync(() => ocr.Frames.Open == 1, "the reader watches");
        return reader;
    }

    private static async Task SendAsync(OcrHarness ocr, params string[] texts)
    {
        foreach (var text in texts)
        {
            await ocr.SendAndWaitAsync(ocr.Inside.Id, text);
        }
    }

    private static async Task<ReadingReply> ReplyAsync(OcrHarness ocr, string readerId) =>
        OcrJson.Deserialize<ReadingReply>(await ocr.CallAsync(OcrMethods.GetReading, new ReaderRequest { ReaderId = readerId }));

    private static List<string> Events(OcrHarness ocr) => [.. ocr.TextRead.Select(e => e.Data[OcrInfo.TextField])];

    private static async Task<string> PreviewingAsync(OcrHarness ocr, Reader draft)
    {
        var opened = ocr.Frames.Opened;
        var reply = await ocr.PreviewAsync(draft);
        Assert.Null(reply.Problem);
        await Eventually.TrueAsync(() => ocr.Frames.Opened > opened, "the preview streams");
        return reply.PreviewId;
    }

    private static async Task SendToPreviewAsync(OcrHarness ocr, params string[] texts)
    {
        foreach (var text in texts)
        {
            var before = ocr.PreviewReadings;
            ocr.Frames.Send(ocr.Inside.Id, text);
            await Eventually.TrueAsync(() => ocr.PreviewReadings > before, "the preview read the frame");
        }
    }

    private static async Task<ReadingReply> PreviewReplyAsync(OcrHarness ocr, string previewId) =>
        OcrJson.Deserialize<ReadingReply>(await ocr.CallAsync(OcrMethods.GetPreview, new PreviewRequest { PreviewId = previewId }));

    [Fact]
    public async Task A_saved_reader_keeps_the_setting_and_an_older_record_reads_as_off()
    {
        await using var ocr = await OcrHarness.StartAsync();

        var saved = await ocr.SaveAsync(ocr.NewReader("Belt", ReaderMode.Watching) with { MovingObjects = true });

        Assert.True(saved.MovingObjects);
        Assert.True(OcrJson.Deserialize<Reader>(await ocr.CallAsync(OcrMethods.GetReader, new ReaderRequest { ReaderId = saved.Id })).MovingObjects);
        await using var again = new OcrPlugin(ocr.Parts);
        await again.StartAsync(ocr.Context, CancellationToken.None);
        Assert.True(again.FindReader(saved.Id)!.MovingObjects); // the stored record, which a backup carries too

        Assert.False(OcrJson.Deserialize<Reader>("""{"id":"r1","name":"Old","mode":"Watching"}""").MovingObjects);
        Assert.False((await ocr.SaveAsync(ocr.NewReader("Other"))).MovingObjects);
    }

    [Fact]
    public void Only_while_watching_the_setting_counts()
    {
        var reader = new Reader { Mode = ReaderMode.OnAction, MovingObjects = true };

        Assert.False(ReaderRules.IsMoving(reader));
        Assert.True(ReaderRules.IsMoving(reader with { Mode = ReaderMode.Watching }));
    }

    [Fact]
    public async Task On_a_value_counts_at_its_second_frame_in_a_row()
    {
        await using var ocr = await StartedAsync();
        var reader = await WatchingAsync(ocr, moving: true);

        await SendAsync(ocr, "AAAA");
        Assert.Empty(Events(ocr));
        Assert.Empty((await ReplyAsync(ocr, reader.Id)).Recent);

        await SendAsync(ocr, "AAAA");
        Assert.Equal(["AAAA"], Events(ocr));
        var row = Assert.Single((await ReplyAsync(ocr, reader.Id)).Recent);
        Assert.Equal("AAAA", row.Text);
        Assert.Equal("AAAA", System.Text.Encoding.UTF8.GetString(row.Cutout!));
    }

    [Fact]
    public async Task On_a_misread_in_one_frame_gives_no_row_and_no_event()
    {
        await using var ocr = await StartedAsync();
        var reader = await WatchingAsync(ocr, moving: true);

        await SendAsync(ocr, "AAAA", "AAAA", "XXXX", "AAAA", "AAAA", "YYYY");

        Assert.Equal(["AAAA"], Events(ocr));
        Assert.Equal(["AAAA"], (await ReplyAsync(ocr, reader.Id)).Recent.Select(r => r.Text));
    }

    [Fact]
    public async Task On_a_b_a_gives_three_rows_and_three_events_and_a_value_that_stays_one()
    {
        await using var ocr = await StartedAsync();
        var reader = await WatchingAsync(ocr, moving: true);

        await SendAsync(ocr, "AAAA", "AAAA", "AAAA", "BBBB", "BBBB", "BBBB", "AAAA", "AAAA", "AAAA");

        Assert.Equal(["AAAA", "BBBB", "AAAA"], Events(ocr));
        Assert.Equal(["AAAA", "BBBB", "AAAA"], (await ReplyAsync(ocr, reader.Id)).Recent.Select(r => r.Text));
    }

    [Fact]
    public async Task On_the_vote_is_gone_and_off_it_stays()
    {
        string[] frames = ["AAAA", "BBBB", "AAAA", "CCCC"];
        await using (var moving = await StartedAsync())
        {
            await WatchingAsync(moving, moving: true);
            await SendAsync(moving, frames);
            Assert.Empty(Events(moving));
        }

        await using var voting = await StartedAsync();
        await WatchingAsync(voting, moving: false);
        await SendAsync(voting, frames);
        Assert.Equal(["AAAA"], Events(voting));
    }

    [Fact]
    public async Task A_row_of_the_vote_shows_the_frame_the_value_was_read_in()
    {
        await using var ocr = await StartedAsync();
        var reader = await WatchingAsync(ocr, moving: false);

        // The vote settles on AAAA in a frame that reads CCCC.
        await SendAsync(ocr, "AAAA", "BBBB", "AAAA", "CCCC");

        var reply = await ReplyAsync(ocr, reader.Id);
        var row = Assert.Single(reply.Recent);
        Assert.Equal("AAAA", row.Text);
        Assert.Equal("AAAA", System.Text.Encoding.UTF8.GetString(row.Cutout!));

        // On the video the value has no box: this frame does not hold it.
        Assert.Equal("AAAA", reply.Reading!.Areas[0].Text);
        Assert.Null(Assert.Single(reply.Details).MatchBox);
    }

    [Fact]
    public async Task The_action_reads_a_voted_value_from_its_own_frame_too()
    {
        await using var ocr = await StartedAsync();
        var reader = await WatchingAsync(ocr, moving: false);

        // The vote puts ABC together: no frame holds it, it is not published yet, the frame of its reading is XBC.
        await SendAsync(ocr, "ABX", "AXC", "XBC");
        Assert.Empty(Events(ocr));
        var reading = await ocr.ReadOnceAsync(reader.Id);

        Assert.Equal("ABC", reading.Reading.Areas[0].Text);
        var row = Assert.Single((await ReplyAsync(ocr, reader.Id)).Recent);
        Assert.Equal("ABC", row.Text);
        Assert.Equal("XBC", System.Text.Encoding.UTF8.GetString(row.Cutout!));
    }

    [Fact]
    public async Task A_change_of_the_setting_starts_the_reader_again_and_a_rename_does_not()
    {
        await using var ocr = await StartedAsync();
        var reader = await WatchingAsync(ocr, moving: false);

        await ocr.SaveAsync(reader with { Name = "Belt 2" });
        Assert.Equal(1, ocr.Frames.Opened);

        await ocr.SaveAsync(reader with { MovingObjects = true });
        await Eventually.TrueAsync(() => ocr.Frames.Opened == 2 && ocr.Frames.Open == 1, "the reader starts again");
        await SendAsync(ocr, "AAAA");
        Assert.Empty(Events(ocr));
        await SendAsync(ocr, "AAAA");
        Assert.Equal(["AAAA"], Events(ocr));
    }

    [Fact]
    public async Task A_preview_with_the_setting_follows_the_two_frames_rule_without_an_event()
    {
        await using var ocr = await StartedAsync();
        var id = await PreviewingAsync(ocr, ocr.NewReader("Belt", ReaderMode.Watching) with { MovingObjects = true });

        await SendToPreviewAsync(ocr, "AAAA");
        Assert.Empty((await PreviewReplyAsync(ocr, id)).Recent);

        await SendToPreviewAsync(ocr, "AAAA", "XXXX");
        var reply = await PreviewReplyAsync(ocr, id);
        Assert.Equal("AAAA", Assert.Single(reply.Recent).Text);
        Assert.Equal("AAAA", reply.Reading!.Areas[0].Text); // the misread does not flicker in
        Assert.Null(Assert.Single(reply.Details).MatchBox);
        Assert.Empty(Events(ocr));
    }

    [Fact]
    public async Task A_preview_without_the_setting_shows_each_frame_as_before()
    {
        await using var ocr = await StartedAsync();
        var id = await PreviewingAsync(ocr, ocr.NewReader("Belt", ReaderMode.Watching));

        await SendToPreviewAsync(ocr, "AAAA", "XXXX");

        var reply = await PreviewReplyAsync(ocr, id);
        Assert.Equal(["XXXX", "AAAA"], reply.Recent.Select(r => r.Text));
        Assert.Equal("XXXX", reply.Reading!.Areas[0].Text);
    }

    [Fact]
    public async Task The_draft_turns_the_rule_on_and_off_on_the_running_stream()
    {
        await using var ocr = await StartedAsync();
        var draft = ocr.NewReader("Belt", ReaderMode.Watching);
        var id = await PreviewingAsync(ocr, draft);
        await SendToPreviewAsync(ocr, "AAAA");

        await ocr.PreviewAsync(draft with { MovingObjects = true }, id);
        await SendToPreviewAsync(ocr, "BBBB");
        Assert.Equal(["AAAA"], (await PreviewReplyAsync(ocr, id)).Recent.Select(r => r.Text));
        await SendToPreviewAsync(ocr, "BBBB");
        Assert.Equal(["BBBB", "AAAA"], (await PreviewReplyAsync(ocr, id)).Recent.Select(r => r.Text));

        await ocr.PreviewAsync(draft, id);
        await SendToPreviewAsync(ocr, "CCCC");
        Assert.Equal(["CCCC", "BBBB", "AAAA"], (await PreviewReplyAsync(ocr, id)).Recent.Select(r => r.Text));
        Assert.Equal(1, ocr.Frames.Opened); // the same stream all along
    }

    [Fact]
    public async Task A_draft_with_another_setting_never_shares_the_readers_stream()
    {
        await using var ocr = await StartedAsync();
        var reader = await WatchingAsync(ocr, moving: false);

        await PreviewingAsync(ocr, reader with { MovingObjects = true });
        Assert.Contains(ocr.Plugin.Runtime!.Previews, p => p.OwnStream && p.SharedWith is null);

        var shared = await ocr.PreviewAsync(reader);
        Assert.Contains(ocr.Plugin.Runtime!.Previews, p => p.Id == shared.PreviewId && p.SharedWith == reader.Id);
    }

    [Fact]
    public async Task Pause_on_a_preview_with_the_setting_reads_the_frame_itself()
    {
        await using var ocr = await StartedAsync();
        var id = await PreviewingAsync(ocr, ocr.NewReader("Belt", ReaderMode.Watching) with { MovingObjects = true });
        await SendToPreviewAsync(ocr, "AAAA", "AAAA", "XXXX");

        var kept = ocr.PreviewReadings;
        var pause = OcrJson.Deserialize<PauseReply>(await ocr.CallAsync(OcrMethods.PausePreview, new PreviewRequest { PreviewId = id }));

        Assert.True(pause.Paused);
        await Eventually.TrueAsync(() => ocr.PreviewReadings > kept, "the held frame was read");
        Assert.Equal("XXXX", (await PreviewReplyAsync(ocr, id)).Reading!.Areas[0].Text);
    }
}
