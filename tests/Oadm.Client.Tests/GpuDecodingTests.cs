using Avalonia.Headless;
using Avalonia.Media.Imaging;

using Google.Protobuf;

using Oadm.Client.Api;
using Oadm.Client.LiveView;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tests;

/// <summary>
/// GPU decoding: the test's result, the warning texts, and that a stream always decodes: on the graphics card when it can,
/// else on the processor with a reason. Runs on any machine (with or without a graphics card).
/// </summary>
public sealed class GpuDecodingTests
{
    [Theory]
    [InlineData("device=d3d11va\nh264=ok\nh265=ok\n", 0, GpuDecodingState.Available, "d3d11va", true, true)]
    [InlineData("device=vaapi\r\nh264=ok\r\nh265=failed: no picture\r\n", 0, GpuDecodingState.Available, "vaapi", true, false)]
    [InlineData("device=vaapi\nh264=failed: x\nh265=failed: y\nproblem=Install the driver", 0, GpuDecodingState.Unavailable, null, false, false)]
    [InlineData("errors=vaapi: no device\nproblem=Install the driver\n", 0, GpuDecodingState.Unavailable, null, false, false)]
    [InlineData("", 139, GpuDecodingState.Unavailable, null, false, false)]
    public void The_tests_output_decides_what_the_graphics_card_decodes(string output, int exit, GpuDecodingState state, string? device, bool h264, bool h265)
    {
        var status = GpuDecodingService.Parse(output, exit);
        Assert.Equal(state, status.State);
        Assert.Equal(device, status.Device);
        Assert.Equal(h264, status.Decodes(VideoCodec.H264));
        Assert.Equal(h265, status.Decodes(VideoCodec.H265));
        if (state == GpuDecodingState.Unavailable)
        {
            Assert.False(string.IsNullOrEmpty(status.Problem));
            Assert.Equal(status.Problem, status.WarningFor(VideoCodec.H264));
        }
    }

    [Fact]
    public void A_crash_says_the_driver_stopped_the_test_and_off_or_not_tested_warns_nothing()
    {
        Assert.Equal(GpuDecodingTexts.TestStopped, GpuDecodingService.Parse("", 134).Problem);
        Assert.Equal("Install the driver", GpuDecodingService.Parse("problem=Install the driver", 0).Problem);
        Assert.Null(GpuDecodingStatus.Off.WarningFor(VideoCodec.H264));
        Assert.Null(GpuDecodingStatus.NotChecked.WarningFor(VideoCodec.H265));
        var h264Only = GpuDecodingService.Parse("device=vaapi\nh264=ok\nh265=failed: x", 0);
        Assert.Null(h264Only.WarningFor(VideoCodec.H264));
        Assert.StartsWith("The graphics card cannot decode H.265", h264Only.WarningFor(VideoCodec.H265), StringComparison.Ordinal);
    }

    [Fact]
    public void The_test_runs_only_in_the_client_itself()
    {
        // The test host is not the client: no test process is started, the processor decodes without a warning.
        Assert.Null(GpuDecodingService.TestCommand());
    }

    [Fact]
    public async Task Turning_the_setting_off_and_on_publishes_the_state()
    {
        var service = new GpuDecodingService(Microsoft.Extensions.Logging.Abstractions.NullLogger<GpuDecodingService>.Instance);
        var changes = 0;
        service.Changed += (_, _) => changes++;
        service.SetEnabled(false);
        Assert.Equal(GpuDecodingState.Off, service.Status.State);
        service.SetEnabled(true);
        await service.StartAsync();
        Assert.Equal(GpuDecodingState.NotChecked, service.Status.State); // not the client: no test
        Assert.True(changes >= 2);
    }

    [FfmpegFact]
    [Trait("Category", "Timing")]
    public async Task The_client_runs_the_test_in_its_own_process()
    {
        // The real command: "dotnet Oadm.Client.dll --check-gpu-decoding" next to the tests.
        var dotnet = Environment.ProcessPath!.EndsWith("testhost.exe", StringComparison.OrdinalIgnoreCase) || Environment.ProcessPath.EndsWith("testhost", StringComparison.Ordinal)
            ? "dotnet" : Environment.ProcessPath;
        var dll = Path.Combine(AppContext.BaseDirectory, "Oadm.Client.dll");
        var service = new GpuDecodingService(Microsoft.Extensions.Logging.Abstractions.NullLogger<GpuDecodingService>.Instance, () => (dotnet, [dll, GpuDecodingCheck.Argument]));
        service.SetEnabled(true);
        await TestSupport.WaitUntilAsync(() => service.Status.State is GpuDecodingState.Available or GpuDecodingState.Unavailable, 30_000);
        Assert.True(service.Status.State == GpuDecodingState.Available || !string.IsNullOrEmpty(service.Status.Problem));

        // A test that crashes (here: a process that ends with an error and says nothing) leaves the processor with a warning.
        var crashing = new GpuDecodingService(Microsoft.Extensions.Logging.Abstractions.NullLogger<GpuDecodingService>.Instance, () => (dotnet, ["no-such-file.dll"]));
        crashing.SetEnabled(true);
        await TestSupport.WaitUntilAsync(() => crashing.Status.State == GpuDecodingState.Unavailable, 30_000);
        Assert.Equal(GpuDecodingTexts.TestStopped, crashing.Status.Problem);
    }

    [FfmpegFact]
    public void The_gpu_test_command_never_fails_and_says_what_works()
    {
        using var output = new StringWriter();
        Assert.Equal(0, GpuDecodingCheck.Run(output));
        var status = GpuDecodingService.Parse(output.ToString(), 0);
        Assert.True(status.State == GpuDecodingState.Available || !string.IsNullOrEmpty(status.Problem), output.ToString());
    }

    public static TheoryData<string> Devices()
    {
        var data = new TheoryData<string>();
        foreach (var device in HwDevice.Candidates())
        {
            data.Add(device);
        }

        data.Add("no-such-device");
        return data;
    }

    [Theory]
    [MemberData(nameof(Devices))]
    public async Task Every_picture_is_decoded_on_the_graphics_card_or_on_the_processor_with_a_reason(string device)
    {
        if (!FfmpegRuntime.Status.IsAvailable)
        {
            return;
        }

        foreach (var codec in new[] { VideoCodec.H264, VideoCodec.H265 })
        {
            var units = FakeLiveVideo.Load(codec);
            var result = await HeadlessSession.Shared.Dispatch(() =>
            {
                using var decoder = new FfmpegVideoDecoder(codec, device);
                var pictures = 0;
                foreach (var (keyframe, data) in units)
                {
                    using var image = decoder.Decode(new LiveViewFrame { Codec = codec, Keyframe = keyframe, Data = ByteString.CopyFrom(data) });
                    if (image is not null)
                    {
                        pictures++;
                        Assert.Equal((640, 360), (image.Width, image.Height));
                        Assert.IsType<WriteableBitmap>(image.Image);
                    }
                }

                return (pictures, decoder.GpuFrames, decoder.GpuProblem);
            }, CancellationToken.None);

            Assert.True(result.pictures >= units.Count - 2, $"{device} {codec}: {result.pictures} of {units.Count} pictures");
            Assert.True(result.GpuFrames > 0 || !string.IsNullOrEmpty(result.GpuProblem), $"{device} {codec}: neither the graphics card nor a reason");
        }
    }
}
