using Avalonia.Media.Imaging;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Client;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Ocr.Tests.Client;

/// <summary>
/// The page's preview against leaks and races (docs/spec/ocr.md "Page"): a preview whose start answer comes after the page
/// left is still ended, a re-shown page starts at once, and an older reading never replaces a newer one.
/// </summary>
public sealed class PagePreviewSafetyTests
{
    private static async Task<OcrViewModel> LoadedAsync(FakePageContext ctx, Func<byte[], Task<Bitmap?>>? decode = null)
    {
        var vm = new OcrViewModel(ctx) { DecodePicture = decode ?? (_ => Task.FromResult<Bitmap?>(null)), ReconnectDelay = TimeSpan.FromMilliseconds(10) };
        await vm.LoadAsync(CancellationToken.None);
        return vm;
    }

    [Fact]
    public async Task A_preview_started_while_the_page_left_is_ended_and_a_new_one_starts_at_once()
    {
        var ctx = Samples.Context();
        using var vm = await LoadedAsync(ctx);
        var editor = vm.Editor!;
        var held = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.AsyncAnswers[OcrMethods.StartPreview] = _ => held.Task;

        editor.StartPreview();
        await Eventually.TrueAsync(() => ctx.CallsOf(OcrMethods.StartPreview) == 1, "the start is on its way");
        var first = OcrJson.Deserialize<PreviewRequest>(ctx.LastPayloadOf(OcrMethods.StartPreview)).PreviewId;
        Assert.Matches("^[0-9a-f]{32}$", first);

        // The page leaves before the answer: the stop names the page's own id at once.
        editor.StopPreview();
        await Eventually.TrueAsync(() => ctx.CallsOf(OcrMethods.StopPreview) == 1, "the stop sent");
        Assert.Contains(first, ctx.LastPayloadOf(OcrMethods.StopPreview), StringComparison.Ordinal);

        // Shown again at once: a new preview starts without waiting for the old call.
        ctx.AsyncAnswers.Remove(OcrMethods.StartPreview);
        editor.StartPreview();
        await Eventually.TrueAsync(() => ctx.CallsOf(OcrMethods.StartPreview) == 2, "the new start");
        var second = OcrJson.Deserialize<PreviewRequest>(ctx.LastPayloadOf(OcrMethods.StartPreview)).PreviewId;
        Assert.NotEqual(first, second);

        // The old answer arrives late: that preview is ended once more (the start may have overtaken the stop), the new one stays.
        held.SetResult(OcrJson.Serialize(new PreviewReply { PreviewId = first }));
        await Eventually.TrueAsync(() => ctx.CallsOf(OcrMethods.StopPreview) == 2, "the late preview ended");
        Assert.Contains(first, ctx.LastPayloadOf(OcrMethods.StopPreview), StringComparison.Ordinal);
        Assert.Equal(second, editor.PreviewId);
        editor.StopPreview();
    }

    [Fact]
    public async Task The_saved_readers_last_reading_never_replaces_a_newer_preview_reading()
    {
        var ctx = Samples.Context();
        var decodeHeld = new TaskCompletionSource<Bitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var decoding = 0;
        using var vm = await LoadedAsync(ctx, bytes =>
        {
            Interlocked.Increment(ref decoding);
            return bytes is [9] ? decodeHeld.Task : Task.FromResult<Bitmap?>(null);
        });
        var editor = vm.Editor!;
        editor.Results.Clear();
        await editor.SendPreviewAsync(CancellationToken.None);

        // The last reading's answer is decoding (its cut-out) while the preview's newer reading arrives and shows.
        ctx.Answers[OcrMethods.GetReading] = _ => Samples.ReplyJson(Samples.GateReading("OLD 000000 0"), Samples.WithCutout(Samples.GateExtras, [9]), null);
        ctx.Answers[OcrMethods.GetPreview] = _ => Samples.ReplyJson(Samples.GateReading("NEW 111111 1"), Samples.GateExtras, null);
        var last = editor.LoadLastReadingAsync(CancellationToken.None);
        await Eventually.TrueAsync(() => Volatile.Read(ref decoding) == 1, "the last reading decodes");
        await editor.RefreshPreviewAsync(CancellationToken.None);
        Assert.Equal("NEW 111111 1", editor.Results[0].Text);

        decodeHeld.SetResult(null);
        await last;

        Assert.Equal("NEW 111111 1", editor.Results[0].Text);
    }
}
