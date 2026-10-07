using Avalonia.Media.Imaging;
using Avalonia.Platform;

using Google.Protobuf;

using Oadm.Client.Api;
using Oadm.Client.LiveView;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tests;

/// <summary>A fact that is skipped, with the loader's reason, when FFmpeg cannot be loaded on this machine.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (!FfmpegRuntime.Status.IsAvailable)
        {
            Skip = "FFmpeg not available: " + FfmpegRuntime.Status.Error;
        }
    }
}

/// <summary>
/// Decodes the access units recorded from 10.0.0.48 (the fake server's replay, depacketized from the
/// RTP fixtures) with the bundled FFmpeg. Bitmaps need the Avalonia platform, so it runs in the
/// headless session.
/// </summary>
public sealed class FfmpegDecoderTests
{
    [Fact]
    public void Runtime_loads_the_bundled_libraries_and_reports_both_decoders()
    {
        FfmpegStatus status = FfmpegRuntime.Status;
        Assert.True(status.IsAvailable, status.Error);
        Assert.Contains("h264", status.Decoders);
        Assert.Contains("hevc", status.Decoders);
        Assert.StartsWith("9.", status.Version, StringComparison.Ordinal);
        Assert.Equal([VideoCodec.H265, VideoCodec.H264], new FfmpegVideoDecoderFactory().SupportedCodecs);
    }

    [FfmpegFact]
    public async Task Recorded_h264_decodes_to_640x360_pictures() => await DecodeAsync(VideoCodec.H264);

    [FfmpegFact]
    public async Task Recorded_h265_decodes_to_640x360_pictures() => await DecodeAsync(VideoCodec.H265);

    [FfmpegFact]
    public void Check_decoder_command_decodes_both_codecs_without_a_window()
    {
        using var output = new StringWriter();
        Assert.Equal(0, DecoderSelfCheck.Run(output));
        Assert.Contains("H264: 15/15 pictures, 640x360, 0 errors: OK", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("H265: 15/15 pictures, 640x360, 0 errors: OK", output.ToString(), StringComparison.Ordinal);
    }

    [FfmpegFact]
    public async Task Garbage_does_not_throw()
    {
        await HeadlessSession.Shared.Dispatch(() =>
        {
            using var decoder = new FfmpegVideoDecoder(VideoCodec.H264);
            Assert.Null(decoder.Decode(new LiveViewFrame { Codec = VideoCodec.H264, Data = ByteString.CopyFrom(0, 0, 0, 1, 0x65, 1, 2, 3) }));
            Assert.Null(decoder.Decode(new LiveViewFrame { Codec = VideoCodec.H264 }));
            return 0;
        }, CancellationToken.None);
    }

    private static async Task DecodeAsync(VideoCodec codec)
    {
        var units = FakeLiveVideo.Load(codec);
        (int Pictures, int Width, int Height, int Distinct) result = await HeadlessSession.Shared.Dispatch(() =>
        {
            using var decoder = new FfmpegVideoDecoder(codec);
            int pictures = 0, width = 0, height = 0, distinct = 0;
            foreach (var (keyframe, data) in units)
            {
                using LiveImage? image = decoder.Decode(new LiveViewFrame { Codec = codec, Keyframe = keyframe, Data = ByteString.CopyFrom(data) });
                if (image is null)
                {
                    continue;
                }

                pictures++;
                width = image.Width;
                height = image.Height;
                distinct = Math.Max(distinct, DistinctSamples((WriteableBitmap)image.Image));
            }

            return (pictures, width, height, distinct);
        }, CancellationToken.None);

        Assert.True(result.Pictures >= units.Count - 1, $"{result.Pictures} of {units.Count} access units produced a picture");
        Assert.Equal((640, 360), (result.Width, result.Height));
        Assert.True(result.Distinct > 8, "picture looks uniform; conversion produced no image content");
    }

    /// <summary>Number of different colors on a coarse grid; a real camera picture has many.</summary>
    private static int DistinctSamples(WriteableBitmap bitmap)
    {
        using ILockedFramebuffer fb = bitmap.Lock();
        var colors = new HashSet<int>();
        for (int y = 10; y < fb.Size.Height; y += 40)
        {
            for (int x = 10; x < fb.Size.Width; x += 40)
            {
                colors.Add(System.Runtime.InteropServices.Marshal.ReadInt32(fb.Address, (y * fb.RowBytes) + (x * 4)));
            }
        }

        return colors.Count;
    }
}
