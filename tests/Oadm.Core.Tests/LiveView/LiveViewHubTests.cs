using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Oadm.Core.LiveView;
using Oadm.Core.LiveView.Rtsp;
using Oadm.Core.Vapix;

namespace Oadm.Core.Tests.LiveView;

public sealed class LiveViewHubTests
{
    private static readonly Guid Device = Guid.NewGuid();

    [Fact]
    public async Task TwoViewersShareOneUpstreamWhichClosesAfterTheLastLeaves()
    {
        var factory = new FakeSourceFactory();
        await using var hub = new LiveViewHub(factory);

        var a = await hub.SubscribeAsync(Device, 640, 360, 10, [VideoCodecKind.H264, VideoCodecKind.H265], CancellationToken.None);
        var b = await hub.SubscribeAsync(Device, 640, 360, 10, [VideoCodecKind.H264, VideoCodecKind.H265], CancellationToken.None);

        Assert.Equal(1, factory.Opened);
        Assert.Equal(1, hub.ActiveUpstreams);
        Assert.Equal(VideoCodecKind.H265, a.Key.Codec);
        Assert.Equal(new ImageSize(640, 360), a.Key.Size);

        var source = factory.OpenedSources.Single();
        source.Emit(Frame(keyframe: true));
        source.Emit(Frame(keyframe: false));
        Assert.Equal(2, await CountAsync(a, 2));
        Assert.Equal(2, await CountAsync(b, 2));

        await a.DisposeAsync();
        Assert.Equal(1, hub.ActiveUpstreams);
        Assert.False(source.Disposed);

        await b.DisposeAsync();
        Assert.Equal(0, hub.ActiveUpstreams);
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task LateViewerStartsWithTheCachedGop()
    {
        var factory = new FakeSourceFactory();
        await using var hub = new LiveViewHub(factory);
        await using var first = await hub.SubscribeAsync(Device, 0, 0, 0, [VideoCodecKind.H264], CancellationToken.None);
        var source = factory.OpenedSources.Single();
        source.Emit(Frame(keyframe: false)); // before any keyframe: never delivered
        source.Emit(Frame(keyframe: true));
        source.Emit(Frame(keyframe: false));
        source.Emit(Frame(keyframe: false));
        await TestWait.UntilAsync(() => source.Pending == 0);

        await using var late = await hub.SubscribeAsync(Device, 0, 0, 0, [VideoCodecKind.H264], CancellationToken.None);
        var frames = await ReadAsync(late, 3);
        Assert.True(frames[0].IsKeyframe);
        Assert.Equal(3, frames.Count);
    }

    [Fact]
    public async Task SlowViewerSkipsToTheNextKeyframe()
    {
        var factory = new FakeSourceFactory();
        await using var hub = new LiveViewHub(factory, options: new LiveViewHubOptions { QueueCapacity = 3 });
        await using var slow = await hub.SubscribeAsync(Device, 0, 0, 0, [VideoCodecKind.H264], CancellationToken.None);
        var source = factory.OpenedSources.Single();
        source.Emit(Frame(true, 1));
        for (var i = 2; i <= 6; i++)
        {
            source.Emit(Frame(false, i)); // 4th frame overflows the queue
        }

        source.Emit(Frame(true, 7));
        source.Emit(Frame(false, 8));
        await TestWait.UntilAsync(() => source.Pending == 0);

        var frames = await ReadAsync(slow, 2);
        Assert.Equal([7, 8], frames.Select(f => (int)f.RtpTimestamp));
        Assert.True(slow.DroppedFrames >= 4);
    }

    [Fact]
    public async Task FallsBackToH264WhenH265CannotBeOpened()
    {
        var factory = new FakeSourceFactory { Fail = VideoCodecKind.H265 };
        await using var hub = new LiveViewHub(factory);
        await using var sub = await hub.SubscribeAsync(Device, 0, 0, 0, [VideoCodecKind.H264, VideoCodecKind.H265], CancellationToken.None);
        Assert.Equal(VideoCodecKind.H264, sub.Key.Codec);
        Assert.Equal(1, hub.ActiveUpstreams);
    }

    [Fact]
    public async Task NoCommonCodecIsRefused()
    {
        var factory = new FakeSourceFactory { CameraCodecs = [VideoCodecKind.H264] };
        await using var hub = new LiveViewHub(factory);
        var ex = await Assert.ThrowsAsync<LiveViewException>(() => hub.SubscribeAsync(Device, 0, 0, 0, [VideoCodecKind.H265], CancellationToken.None));
        Assert.Equal(LiveViewError.NotSupported, ex.Error);
        Assert.Equal(0, factory.Opened);
    }

    [Fact]
    public async Task UpstreamFailureEndsEveryViewerWithTheErrorAndANewSubscribeReopens()
    {
        var factory = new FakeSourceFactory();
        await using var hub = new LiveViewHub(factory);
        await using var sub = await hub.SubscribeAsync(Device, 0, 0, 0, [VideoCodecKind.H264], CancellationToken.None);
        factory.OpenedSources.Single().Fail(new LiveViewException(LiveViewError.Unreachable, "gone"));

        var ex = await Assert.ThrowsAsync<LiveViewException>(async () => await ReadAsync(sub, 1));
        Assert.Equal("gone", ex.Message);
        await TestWait.UntilAsync(() => hub.ActiveUpstreams == 0);

        await using var again = await hub.SubscribeAsync(Device, 0, 0, 0, [VideoCodecKind.H264], CancellationToken.None);
        Assert.Equal(2, factory.Opened);
    }

    [Fact]
    public async Task CancelledReaderDoesNotCloseTheSharedStreamUntilDisposed()
    {
        var factory = new FakeSourceFactory();
        await using var hub = new LiveViewHub(factory);
        var sub = await hub.SubscribeAsync(Device, 0, 0, 0, [VideoCodecKind.H264], CancellationToken.None);
        using (var cts = new CancellationTokenSource(50))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (var _ in sub.ReadAllAsync(cts.Token))
                {
                }
            });
        }

        Assert.Equal(1, hub.ActiveUpstreams);
        await sub.DisposeAsync();
        Assert.True(factory.OpenedSources.Single().Disposed);
    }

    private static LiveVideoFrame Frame(bool keyframe, long ts = 0) =>
        new(VideoCodecKind.H264, [0, 0, 0, 1, keyframe ? (byte)0x65 : (byte)0x41], keyframe, 640, 360, DateTimeOffset.UtcNow, null, ts);

    private static async Task<int> CountAsync(LiveViewSubscription sub, int count) => (await ReadAsync(sub, count)).Count;

    private static async Task<List<LiveVideoFrame>> ReadAsync(LiveViewSubscription sub, int count)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var frames = new List<LiveVideoFrame>();
        await foreach (var frame in sub.ReadAllAsync(cts.Token))
        {
            frames.Add(frame);
            if (frames.Count == count)
            {
                break;
            }
        }

        return frames;
    }

    internal sealed class FakeSourceFactory : ILiveVideoSourceFactory
    {
        public List<FakeSource> OpenedSources { get; } = [];

        public int Opened => OpenedSources.Count;

        public VideoCodecKind? Fail { get; init; }

        public IReadOnlyList<VideoCodecKind> CameraCodecs { get; init; } = [VideoCodecKind.H264, VideoCodecKind.H265];

        public IReadOnlyList<VideoSourceInfo>? Sources { get; init; }

        public List<LiveStreamKey> OpenedKeys { get; } = [];

        public Task<LiveViewCapabilities> GetCapabilitiesAsync(Guid deviceId, CancellationToken ct) =>
            Task.FromResult(new LiveViewCapabilities(CameraCodecs, [new(1920, 1080), new(1280, 720), new(640, 360)], Sources));

        public Task<ILiveVideoSource> OpenAsync(LiveStreamKey key, CancellationToken ct)
        {
            if (key.Codec == Fail)
            {
                throw new LiveViewException(LiveViewError.NotSupported, "nope");
            }

            var source = new FakeSource();
            lock (OpenedSources)
            {
                OpenedSources.Add(source);
                OpenedKeys.Add(key);
            }

            return Task.FromResult<ILiveVideoSource>(source);
        }
    }

    internal sealed class FakeSource : ILiveVideoSource
    {
        private readonly Channel<LiveVideoFrame> _frames = Channel.CreateUnbounded<LiveVideoFrame>();

        public bool Disposed { get; private set; }

        public int Pending => _frames.Reader.Count;

        public void Emit(LiveVideoFrame frame) => _frames.Writer.TryWrite(frame);

        public void Fail(Exception error) => _frames.Writer.TryComplete(error);

        public async IAsyncEnumerable<LiveVideoFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var frame in _frames.Reader.ReadAllAsync(ct))
            {
                yield return frame;
            }
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

internal static class TestWait
{
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > end)
            {
                throw new TimeoutException("Condition not met in time");
            }

            await Task.Delay(10);
        }

        await Task.Delay(20); // let the consumer finish handling the last item
    }
}
