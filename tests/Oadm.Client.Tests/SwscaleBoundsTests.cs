using System.Runtime.InteropServices;

using FFmpeg.AutoGen;

using Oadm.Client.LiveView;

using Xunit.Abstractions;

namespace Oadm.Client.Tests;

/// <summary>
/// The live video converts every decoded picture into the bitmap's pixel buffer (exactly width x height x 4 bytes,
/// <see cref="BgraConversion"/>). Checks that nothing is written past that buffer, for the sizes cameras stream and both
/// source formats (YUV 4:2:0 from the processor, NV12 from the graphics card): a guard zone after the buffer must stay
/// untouched. Plain swscale into the buffer overflowed it for widths that are not a multiple of 16.
/// </summary>
public sealed unsafe class SwscaleBoundsTests(ITestOutputHelper output)
{
    private const int Guard = 4096;
    private const byte GuardByte = 0xCD;

    public static TheoryData<int, int> Sizes => new()
    {
        { 160, 90 }, { 320, 180 }, { 480, 270 }, { 640, 360 }, { 800, 450 }, { 1024, 576 }, { 1280, 720 }, { 1920, 1080 },
        { 1024, 768 }, { 640, 480 }, { 704, 576 }, { 2688, 1512 }, { 3840, 2160 }, { 854, 480 }, { 162, 90 },
        // Widths that are not a multiple of 16: plain swscale wrote 8 to 32 bytes past the buffer.
        { 426, 240 }, { 1080, 1920 }, { 24, 90 }, { 30, 90 }, { 1366, 768 }, { 1918, 1080 },
    };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Swscale_stays_inside_the_bitmap_buffer(int width, int height)
    {
        if (!FfmpegRuntime.Status.IsAvailable)
        {
            return;
        }

        foreach (var format in new[] { AVPixelFormat.AV_PIX_FMT_YUV420P, AVPixelFormat.AV_PIX_FMT_NV12 })
        {
            var source = ffmpeg.av_frame_alloc();
            try
            {
                source->width = width;
                source->height = height;
                source->format = (int)format;
                Assert.True(ffmpeg.av_frame_get_buffer(source, 0) >= 0);
                Fill(source);

                // The same context as FfmpegVideoDecoder.Convert: same size, BGRA, bilinear; the bitmap's stride is width * 4.
                SwsContext* sws = ffmpeg.sws_getCachedContext(null, width, height, format, width, height,
                    AVPixelFormat.AV_PIX_FMT_BGRA, (int)SwsFlags.SWS_BILINEAR, null, null, null);
                Assert.True(sws != null);
                var size = width * height * 4;
                var buffer = Marshal.AllocHGlobal(size + Guard);
                var scratch = ffmpeg.av_frame_alloc();
                try
                {
                    new Span<byte>((byte*)buffer + size, Guard).Fill(GuardByte);
                    for (var i = 0; i < 3; i++)
                    {
                        Assert.True(BgraConversion.Convert(sws, source, scratch, (byte*)buffer, width * 4));
                    }

                    var guard = new ReadOnlySpan<byte>((byte*)buffer + size, Guard);
                    var overrun = guard.IndexOfAnyExcept(GuardByte) is var first && first >= 0 ? guard.LastIndexOfAnyExcept(GuardByte) + 1 : 0;
                    output.WriteLine($"{width}x{height} {format}: {(overrun == 0 ? "inside" : $"{overrun} bytes past the end")}");
                    Assert.True(overrun == 0, $"The conversion wrote {overrun} bytes past a {width}x{height} BGRA buffer from {format}");
                }
                finally
                {
                    ffmpeg.av_frame_free(&scratch);
                    Marshal.FreeHGlobal(buffer);
                    ffmpeg.sws_freeContext(sws);
                }
            }
            finally
            {
                ffmpeg.av_frame_free(&source);
            }
        }
    }

    /// <summary>A gradient, so the converter does real work.</summary>
    private static void Fill(AVFrame* frame)
    {
        for (uint plane = 0; plane < 4 && frame->data[plane] != null; plane++)
        {
            var rows = plane == 0 ? frame->height : (frame->height + 1) / 2;
            for (var y = 0; y < rows; y++)
            {
                var row = new Span<byte>(frame->data[plane] + (y * frame->linesize[plane]), frame->linesize[plane]);
                for (var x = 0; x < row.Length; x++)
                {
                    row[x] = (byte)(x + y);
                }
            }
        }
    }
}
