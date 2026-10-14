using Avalonia.Controls;
using Avalonia.Threading;

using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Client.Api;
using Oadm.Client.Infrastructure;
using Oadm.Client.LiveView;
using Oadm.Contracts.V1;

using Xunit.Abstractions;

namespace Oadm.Client.Tests;

/// <summary>
/// Many live videos at once, like a Live View page: real FFmpeg decoding of the recorded sample video (on the graphics
/// card when OADM_LIVE_STRESS_GPU names a device type, e.g. d3d11va), sizes changing every few hundred milliseconds
/// (silent swaps), streams stopped, restarted and closed. Opt-in: OADM_LIVE_STRESS=&lt;seconds&gt;. A native crash ends the test
/// process; run it with DOTNET_DbgEnableMiniDump=1 to keep a dump.
/// </summary>
[Trait("Category", "Perf")]
public sealed class LiveVideoStressTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Many_videos_swap_and_close_without_crashing()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("OADM_LIVE_STRESS"), out var seconds) || seconds <= 0
            || !FfmpegRuntime.Status.IsAvailable)
        {
            return;
        }

        var gpu = Environment.GetEnvironmentVariable("OADM_LIVE_STRESS_GPU");
        var factory = new StressDecoderFactory(string.IsNullOrEmpty(gpu) ? null : gpu);
        var (pictures, swaps) = await HeadlessSession.Shared.Dispatch(async () =>
        {
            using var api = new FakeOadmApi(TimeSpan.FromMilliseconds(5));
            var devices = (await api.ListDevicesAsync(CancellationToken.None))
                .Where(d => d.Category is DeviceCategory.Camera or DeviceCategory.Encoder or DeviceCategory.Intercom).Select(d => d.Id).ToList();
            var random = new Random(7);
            var videos = new List<(LiveVideoViewModel Video, Window Window)>();
            var pictureCount = 0;
            var swapCount = 0;

            void Open()
            {
                var video = new LiveVideoViewModel(api, factory, new AvaloniaUiDispatcher(), NullLogger.Instance, new LiveVideoSettings());
                video.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(LiveVideoViewModel.Image))
                    {
                        pictureCount++;
                    }
                };
                var window = new Window { Width = 480, Height = 270, Content = new LiveVideoView { DataContext = video } };
                window.Show();
                video.Start(devices[random.Next(devices.Count)]);
                videos.Add((video, window));
            }

            for (var i = 0; i < 8; i++)
            {
                Open();
            }

            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < end)
            {
                var (video, window) = videos[random.Next(videos.Count)];
                switch (random.Next(10))
                {
                    case < 6: // a new size: a silent swap once it rests
                        int[] widths = [160, 320, 480, 640, 800, 1280];
                        var width = widths[random.Next(widths.Length)];
                        video.SetViewport(width, width * 9 / 16);
                        swapCount++;
                        break;
                    case 6: // another camera in the same tile
                        video.Start(devices[random.Next(devices.Count)]);
                        break;
                    case 7: // hidden and shown again
                        video.SetVisible(false);
                        video.SetVisible(true);
                        break;
                    default: // the tile is closed (a removed camera, a closed tab) and another one opens
                        window.Close();
                        video.Dispose();
                        videos.Remove((video, window));
                        Open();
                        break;
                }

                Dispatcher.UIThread.RunJobs();
                await Task.Delay(random.Next(20, 250));
            }

            foreach (var (video, window) in videos)
            {
                window.Close();
                video.Dispose();
            }

            return (pictureCount, swapCount);
        }, CancellationToken.None);

        output.WriteLine($"{seconds} s, GPU {gpu ?? "off"}: {pictures} pictures, {swaps} size changes, decoders {factory.Created} created");
        Assert.True(pictures > 0);
    }

    private sealed class StressDecoderFactory(string? gpu) : IVideoDecoderFactory
    {
        private int _created;

        public int Created => _created;

        public IReadOnlyList<VideoCodec> SupportedCodecs { get; } = new FfmpegVideoDecoderFactory().SupportedCodecs;

        public string? UnavailableReason => null;

        public IVideoDecoder Create(VideoCodec codec)
        {
            Interlocked.Increment(ref _created);
            return new FfmpegVideoDecoder(codec, gpu);
        }
    }
}
