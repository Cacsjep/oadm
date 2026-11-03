using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Ocr.Frames;
using Oadm.Plugins.Ocr.Model;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Testing;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Ocr.Tests.Frames;

/// <summary>Single pictures and the watched MJPEG stream through the camera's VAPIX client (docs/spec/ocr.md "Readers", "Tests").</summary>
public sealed class VapixFrameSourceTests
{
    private static readonly FrameRequest Request = new(Guid.NewGuid(), 2, new Resolution(640, 360));

    private readonly FakeTimeProvider _time = new();
    private readonly StreamCamera _camera = new();
    private readonly VapixFrameSource _source;
    private readonly List<TimeSpan> _pauses = [];
    private int _frames;

    public VapixFrameSourceTests()
    {
        _source = new VapixFrameSource(new Factory(_camera), _time)
        {
            PauseStarted = p =>
            {
                lock (_pauses)
                {
                    _pauses.Add(p);
                }
            },
            FrameReceived = () => Interlocked.Increment(ref _frames),
        };
    }

    private int Pauses
    {
        get
        {
            lock (_pauses)
            {
                return _pauses.Count;
            }
        }
    }

    private TimeSpan LastPause
    {
        get
        {
            lock (_pauses)
            {
                return _pauses[^1];
            }
        }
    }

    private static HttpResponseMessage Mjpeg(Stream body, string contentType = MjpegParserTests.RecordedContentType)
    {
        var content = new StreamContent(body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    [Fact]
    public async Task ASinglePictureIsAskedForWithResolutionAndSource()
    {
        var jpeg = MjpegParserTests.Jpeg(1000, 7);
        _camera.Answer = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(jpeg) });

        var picture = await _source.SingleAsync(Request, CancellationToken.None);

        Assert.Equal(jpeg, picture.ToArray());
        var sent = Assert.Single(_camera.Sent);
        Assert.Equal("GET /axis-cgi/jpg/image.cgi?resolution=640x360&camera=2", sent.Line);
        Assert.False(sent.Streamed);
    }

    [Fact]
    public async Task ASingleAnswerWithoutAPictureIsAnError()
    {
        _camera.Answer = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("Error: no picture") });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _source.SingleAsync(Request, CancellationToken.None));

        Assert.Equal(FrameTexts.NoPicture, error.Message);
    }

    [Fact]
    public async Task ARefusedSinglePictureSaysWhy()
    {
        _camera.Answer = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _source.SingleAsync(Request, CancellationToken.None));

        Assert.Equal(DeviceMessages.ForHttpStatus(401), error.Message);
    }

    [Fact]
    public async Task WatchingStreamsTheRecordedAnswer()
    {
        var answer = await File.ReadAllBytesAsync(MjpegParserTests.RecordedPath);
        _camera.Answer = (_, _) => Task.FromResult(Mjpeg(new MemoryStream(answer)));
        using var stop = new CancellationTokenSource();
        var received = new List<int>();

        // The consumer may miss pictures (the newest wins) but gets them in order and always the last one.
        await foreach (var frame in _source.WatchAsync(Request, 2, stop.Token))
        {
            received.Add(frame.Length);
            if (frame.Length == MjpegParserTests.RecordedLengths[^1])
            {
                await stop.CancelAsync();
            }
        }

        Assert.Equal(received.Select(l => Array.IndexOf(MjpegParserTests.RecordedLengths, l)).Order(), received.Select(l => Array.IndexOf(MjpegParserTests.RecordedLengths, l)));
        Assert.DoesNotContain(-1, received.Select(l => Array.IndexOf(MjpegParserTests.RecordedLengths, l)));
        var sent = _camera.Sent[0];
        Assert.Equal("GET /axis-cgi/mjpg/video.cgi?fps=2&resolution=640x360&camera=2", sent.Line);
        Assert.True(sent.Streamed);
    }

    [Fact]
    public async Task TheNewestPictureWinsWhileTheConsumerIsBusy()
    {
        var pipe = new Pipe();
        _camera.Answer = (_, _) => Task.FromResult(Mjpeg(pipe.Reader.AsStream()));
        using var stop = new CancellationTokenSource();
        var frames = _source.WatchAsync(Request, 1, stop.Token).GetAsyncEnumerator();

        await pipe.Writer.WriteAsync(MjpegParserTests.Part(MjpegParserTests.Jpeg(100, 1)));
        Assert.True(await frames.MoveNextAsync());
        Assert.Equal(MjpegParserTests.Jpeg(100, 1), frames.Current.ToArray());

        // Busy: three pictures arrive before the consumer asks again.
        for (byte i = 2; i <= 4; i++)
        {
            await pipe.Writer.WriteAsync(MjpegParserTests.Part(MjpegParserTests.Jpeg(100, i)));
        }

        await Eventually.TrueAsync(() => Volatile.Read(ref _frames) == 4, "four pictures received");
        Assert.True(await frames.MoveNextAsync());
        Assert.Equal(MjpegParserTests.Jpeg(100, 4), frames.Current.ToArray());

        await stop.CancelAsync();
        Assert.False(await frames.MoveNextAsync());
        await frames.DisposeAsync();
    }

    [Fact]
    public async Task AStreamThatCannotBeOpenedIsTriedAgainWithAGrowingPause()
    {
        _camera.Answer = (_, _) => throw new HttpRequestException("No route to host");
        using var stop = new CancellationTokenSource();
        var watch = ConsumeAsync(_source.WatchAsync(Request, 1, stop.Token));

        double[] expected = [1, 2, 4, 8, 16, 32, 60, 60];
        for (var i = 0; i < expected.Length; i++)
        {
            var n = i + 1;
            await Eventually.TrueAsync(() => Pauses == n, $"pause {n}");
            Assert.Equal(TimeSpan.FromSeconds(expected[i]), LastPause);
            Assert.Equal(n, _camera.Sent.Count);
            _time.Advance(LastPause);
            await Eventually.TrueAsync(() => _camera.Sent.Count == n + 1, $"attempt {n + 1}");
        }

        await stop.CancelAsync();
        await watch;
    }

    [Fact]
    public async Task AStreamThatDeliveredStartsThePausesAnew()
    {
        var attempt = 0;
        var good = MjpegParserTests.Part(MjpegParserTests.Jpeg(100, 1));
        _camera.Answer = (_, _) => Interlocked.Increment(ref attempt) switch
        {
            1 or 2 => throw new HttpRequestException("No route to host"),
            3 => Task.FromResult(Mjpeg(new MemoryStream(good))),
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
        };
        using var stop = new CancellationTokenSource();
        var watch = ConsumeAsync(_source.WatchAsync(Request, 1, stop.Token));

        double[] expected = [1, 2, 1, 2];
        for (var i = 0; i < expected.Length; i++)
        {
            var n = i + 1;
            await Eventually.TrueAsync(() => Pauses == n, $"pause {n}");
            Assert.Equal(TimeSpan.FromSeconds(expected[i]), LastPause);
            _time.Advance(LastPause);
        }

        await stop.CancelAsync();
        await watch;
        Assert.Equal(1, Volatile.Read(ref _frames));
    }

    [Fact]
    public async Task AStalledStreamIsOpenedAgain()
    {
        var pipe = new Pipe();
        _camera.Answer = (_, _) => Task.FromResult(Mjpeg(pipe.Reader.AsStream()));
        using var stop = new CancellationTokenSource();
        var watch = ConsumeAsync(_source.WatchAsync(Request, 1, stop.Token));
        await pipe.Writer.WriteAsync(MjpegParserTests.Part(MjpegParserTests.Jpeg(100, 1)));
        await Eventually.TrueAsync(() => Volatile.Read(ref _frames) == 1, "first picture");

        // Nothing for almost the stall time: still the same stream.
        _time.Advance(VapixFrameSource.StallTimeout - TimeSpan.FromSeconds(1));
        await pipe.Writer.WriteAsync(MjpegParserTests.Part(MjpegParserTests.Jpeg(100, 2)));
        await Eventually.TrueAsync(() => Volatile.Read(ref _frames) == 2, "second picture");
        Assert.Equal(0, Pauses);

        _time.Advance(VapixFrameSource.StallTimeout);
        await Eventually.TrueAsync(() => Pauses == 1, "the stalled stream dropped");
        Assert.Equal(VapixFrameSource.FirstReopenPause, LastPause);
        _time.Advance(LastPause);
        await Eventually.TrueAsync(() => _camera.Sent.Count == 2, "opened again");

        await stop.CancelAsync();
        await watch;
    }

    [Fact]
    public async Task TheFirstPictureMayTakeLongerThanAGap()
    {
        var pipe = new Pipe();
        _camera.Answer = (_, _) => Task.FromResult(Mjpeg(pipe.Reader.AsStream()));
        using var stop = new CancellationTokenSource();
        var watch = ConsumeAsync(_source.WatchAsync(Request, 1, stop.Token));
        await Eventually.TrueAsync(() => _camera.Sent.Count == 1, "stream opened");

        // Slower than a gap between pictures, faster than opening may take: still the same stream.
        _time.Advance(VapixFrameSource.StallTimeout + TimeSpan.FromSeconds(1));
        await pipe.Writer.WriteAsync(MjpegParserTests.Part(MjpegParserTests.Jpeg(100, 1)));
        await Eventually.TrueAsync(() => Volatile.Read(ref _frames) == 1, "first picture");
        Assert.Equal(0, Pauses);
        Assert.Single(_camera.Sent);

        await stop.CancelAsync();
        await watch;
    }

    [Fact]
    public async Task AnAnswerThatIsNoStreamIsTriedAgain()
    {
        _camera.Answer = (_, _) => Task.FromResult(Mjpeg(new MemoryStream([1, 2, 3]), "text/html"));
        using var stop = new CancellationTokenSource();
        var watch = ConsumeAsync(_source.WatchAsync(Request, 4, stop.Token));

        await Eventually.TrueAsync(() => Pauses == 1, "first pause");
        _time.Advance(LastPause);
        await Eventually.TrueAsync(() => Pauses == 2, "second pause");

        await stop.CancelAsync();
        Assert.Equal(0, await watch);
        Assert.Equal("GET /axis-cgi/mjpg/video.cgi?fps=4&resolution=640x360&camera=2", _camera.Sent[0].Line);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 4)]
    [InlineData(6, 32)]
    [InlineData(7, 60)]
    [InlineData(1000, 60)]
    public void ThePauseDoublesUpToAMinute(int failures, double seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), VapixFrameSource.ReopenPause(failures));
        Assert.Equal(OcrLimits.MaxReopenPause, VapixFrameSource.ReopenPause(int.MaxValue));
    }

    /// <summary>Reads the sequence until it ends; the number of pictures.</summary>
    private static async Task<int> ConsumeAsync(IAsyncEnumerable<ReadOnlyMemory<byte>> frames)
    {
        var count = 0;
        await foreach (var _ in frames)
        {
            count++;
        }

        return count;
    }

    private sealed record SentRequest(string Line, bool Streamed);

    /// <summary>A camera whose answers the test gives; records each request and whether it asked for a streamed answer.</summary>
    private sealed class StreamCamera : IVapixClient
    {
        private readonly List<SentRequest> _sent = [];

        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Answer { get; set; } =
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        public IReadOnlyList<SentRequest> Sent
        {
            get
            {
                lock (_sent)
                {
                    return [.. _sent];
                }
            }
        }

        public Uri BaseAddress { get; } = new("http://192.0.2.10/");

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (_sent)
            {
                var streamed = request.Options.TryGetValue(VapixRequestOptions.StreamResponse, out var s) && s;
                _sent.Add(new SentRequest($"{request.Method} {request.RequestUri!.OriginalString}", streamed));
            }

            return Answer(request, ct);
        }

        public Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct) => throw new NotSupportedException();

        public Task RestartAsync(CancellationToken ct) => throw new NotSupportedException("Read only.");

        public Task<IReadOnlyList<DeviceApi>> GetApiListAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Factory(IVapixClient camera) : IVapixClientFactory
    {
        public Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct) => Task.FromResult(camera);
    }
}
