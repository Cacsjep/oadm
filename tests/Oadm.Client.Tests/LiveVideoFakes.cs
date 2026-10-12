using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Avalonia;
using Avalonia.Media;

using NSubstitute;

using Oadm.Client.Api;
using Oadm.Client.LiveView;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tests;

/// <summary>
/// A fake server for live video tests: every Watch call is a <see cref="FakeStream"/> the test feeds; device "a" has two
/// view areas (1920x1080 down to 320x180), every other device one channel.
/// </summary>
internal sealed class LiveVideoFakes
{
    public LiveVideoFakes()
    {
        Api.WatchLiveViewAsync(Arg.Any<LiveViewRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var stream = new FakeStream(call.Arg<LiveViewRequest>());
            lock (Streams)
            {
                Streams.Add(stream);
            }

            return stream.ReadAsync(call.Arg<CancellationToken>());
        });
        Api.ListLiveViewSourcesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
            call.Arg<string>() == "a"
                ? [Source(1, "View Area 1"), Source(2, "View Area 2")]
                : (IReadOnlyList<LiveViewSource>)[Source(1, "Camera")]);
        Api.GetOpticsAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new OpticsState());
        Api.GetPtzAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new PtzState());
    }

    public IOadmApi Api { get; } = Substitute.For<IOadmApi>();

    public FakeDecoderFactory Decoders { get; } = new();

    public List<FakeStream> Streams { get; } = [];

    public int StreamCount
    {
        get
        {
            lock (Streams)
            {
                return Streams.Count;
            }
        }
    }

    public static LiveViewSource Source(int camera, string name)
    {
        var source = new LiveViewSource { Camera = camera, Name = name, MaxWidth = 1920, MaxHeight = 1080 };
        foreach (var width in new[] { 1920, 1280, 800, 640, 480, 320 })
        {
            source.Resolutions.Add(new LiveViewResolution { Width = width, Height = width * 9 / 16 });
        }

        return source;
    }

    public async Task<FakeStream> StreamAsync(int index)
    {
        await TestSupport.WaitUntilAsync(() => StreamCount > index);
        lock (Streams)
        {
            return Streams[index];
        }
    }

    internal sealed class FakeStream(LiveViewRequest request)
    {
        private readonly Channel<LiveViewFrame> _frames = Channel.CreateUnbounded<LiveViewFrame>();

        public LiveViewRequest Request { get; } = request;

        public bool Cancelled { get; private set; }

        public void Send(VideoCodec codec = VideoCodec.H264, int camera = 1, int width = 640, int height = 360) =>
            _frames.Writer.TryWrite(new LiveViewFrame { Codec = codec, Keyframe = true, Width = width, Height = height, Camera = camera, Data = Google.Protobuf.ByteString.CopyFrom(0, 0, 0, 1) });

        public void Fail(Exception error) => _frames.Writer.TryComplete(error);

        public async IAsyncEnumerable<LiveViewFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            // Not disposed: cancelling can run this iterator to its end inline, before a disposed registration had its turn.
            ct.Register(() => Cancelled = true);
            await foreach (var frame in _frames.Reader.ReadAllAsync(ct))
            {
                yield return frame;
            }
        }
    }

    internal sealed class FakeDecoderFactory : IVideoDecoderFactory
    {
        public bool Available { get; set; } = true;

        public List<FakeDecoder> Created { get; } = [];

        public IReadOnlyList<VideoCodec> SupportedCodecs => Available ? [VideoCodec.H265, VideoCodec.H264] : [];

        public string? UnavailableReason => Available ? null : "avcodec-63.dll not found";

        /// <summary>Decoders on the graphics card, or with this reason why not.</summary>
        public bool UsesGpu { get; set; }

        public string? GpuProblem { get; set; }

        public IVideoDecoder Create(VideoCodec codec)
        {
            var decoder = new FakeDecoder { UsesGpu = UsesGpu, GpuProblem = GpuProblem };
            lock (Created)
            {
                Created.Add(decoder);
            }

            return decoder;
        }
    }

    internal sealed class FakeDecoder : IVideoDecoder
    {
        public bool Disposed { get; private set; }

        public bool UsesGpu { get; init; }

        public string? GpuProblem { get; init; }

        public LiveImage? Decode(LiveViewFrame frame) => new(new FakeImage(frame.Width, frame.Height), frame.Width, frame.Height);

        public void Dispose() => Disposed = true;
    }

    internal sealed class FakeImage(int width, int height) : IImage, IDisposable
    {
        public bool Disposed { get; private set; }

        public Size Size => new(width, height);

        public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
        {
        }

        public void Dispose() => Disposed = true;
    }
}
