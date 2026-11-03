using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Ocr.Tests.Readers;

/// <summary>A picture of the fakes: its bytes are the text every area reads ("" = no text).</summary>
internal static class Frame
{
    public static ReadOnlyMemory<byte> Of(string text) => Encoding.UTF8.GetBytes(text);

    public static string TextOf(ReadOnlyMemory<byte> jpeg) => Encoding.UTF8.GetString(jpeg.Span);
}

/// <summary>
/// Recognition of the fakes: each area has one line with the picture's text (none for an empty picture). A test can hold
/// readings at a gate and make the next reading fail.
/// </summary>
internal sealed class FakeRecognizer : ITextRecognizer, IRecognizerCapabilities
{
    private readonly ConcurrentQueue<string> _read = new();
    private int _started;

    public bool OnGpu { get; set; }

    /// <summary>When set, every reading waits for it (one release per reading).</summary>
    public SemaphoreSlim? Gate { get; set; }

    /// <summary>The next reading throws this once.</summary>
    public Exception? FailNext { get; set; }

    /// <summary>The texts of the pictures read, in order of their end.</summary>
    public IReadOnlyList<string> Read => [.. _read];

    /// <summary>Readings started (also those waiting at the gate).</summary>
    public int Started => Volatile.Read(ref _started);

    public async Task<RecognizedPicture> ReadAsync(ReadOnlyMemory<byte> jpeg, IReadOnlyList<Area> areas, CancellationToken ct)
    {
        Interlocked.Increment(ref _started);
        if (Gate is { } gate)
        {
            await gate.WaitAsync(ct);
        }

        if (FailNext is { } fail)
        {
            FailNext = null;
            throw fail;
        }

        var text = Frame.TextOf(jpeg);
        _read.Enqueue(text);
        var count = Math.Max(1, areas.Count);
        var lines = text.Length == 0 ? [] : new[] { new TextLine(text, new RelativeBox(0.1, 0.1, 0.5, 0.1), 90) };
        return new RecognizedPicture(new Resolution(1920, 1080), [.. Enumerable.Range(0, count).Select(_ => (IReadOnlyList<TextLine>)lines)]);
    }
}

/// <summary>Matching of the fakes: the value is all lines; with a format it matches when there is text.</summary>
internal sealed class FakeMatcher : ITextMatcher
{
    public AreaMatch Match(IReadOnlyList<TextLine> lines, ExpectedText expected)
    {
        var text = string.Join('\n', lines.Select(l => l.Text));
        return new AreaMatch(text, text.Length > 0, text);
    }
}

/// <summary>
/// The cameras of the fakes: <see cref="SingleAsync"/> answers <see cref="Single"/>; each watching reader gets the frames a
/// test sends with <see cref="Send"/>.
/// </summary>
internal sealed class FakeFrames : IFrameSource
{
    private readonly ConcurrentDictionary<Guid, Channel<ReadOnlyMemory<byte>>> _streams = new();
    private int _singles;
    private int _open;
    private int _opened;
    private int _taken;

    public string Single { get; set; } = string.Empty;

    public int Singles => Volatile.Read(ref _singles);

    /// <summary>Streams open now.</summary>
    public int Open => Volatile.Read(ref _open);

    /// <summary>Streams opened so far.</summary>
    public int Opened => Volatile.Read(ref _opened);

    /// <summary>Frames the readers took from the streams so far.</summary>
    public int Taken => Volatile.Read(ref _taken);

    /// <summary>The frame rates the streams were opened with.</summary>
    public ConcurrentQueue<int> Rates { get; } = new();

    public Task<ReadOnlyMemory<byte>> SingleAsync(FrameRequest request, CancellationToken ct)
    {
        Interlocked.Increment(ref _singles);
        return Task.FromResult(Frame.Of(Single));
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> WatchAsync(FrameRequest request, int fps, [EnumeratorCancellation] CancellationToken ct)
    {
        var stream = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        _streams[request.DeviceId] = stream;
        Rates.Enqueue(fps);
        Interlocked.Increment(ref _opened);
        Interlocked.Increment(ref _open);
        try
        {
            await foreach (var frame in stream.Reader.ReadAllAsync(ct))
            {
                yield return frame;
                Interlocked.Increment(ref _taken); // the reader asked for the next one: this frame was handed on
            }
        }
        finally
        {
            Interlocked.Decrement(ref _open);
        }
    }

    public bool IsStreaming(Guid deviceId) => _streams.ContainsKey(deviceId);

    /// <summary>Sends a frame to the stream of <paramref name="deviceId"/>.</summary>
    public void Send(Guid deviceId, string text)
    {
        Assert.True(_streams.TryGetValue(deviceId, out var stream), "No stream of the camera is open.");
        stream.Writer.TryWrite(Frame.Of(text));
    }
}

/// <summary>A vote of the tests: the value read most often in the last 5 frames (the newest on a tie).</summary>
internal sealed class MajorityVote : IFrameVote
{
    private readonly Queue<AreaResult> _last = new();

    public AreaResult Add(AreaResult frame)
    {
        _last.Enqueue(frame);
        if (_last.Count > 5)
        {
            _last.Dequeue();
        }

        var best = _last.GroupBy(r => r.Text, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Contains(frame) ? 1 : 0).First();
        return best.Last();
    }
}

/// <summary>A started Text recognition plugin over the fakes.</summary>
internal sealed class OcrHarness : IAsyncDisposable
{
    public OcrHarness(Func<Area, IFrameVote>? vote = null, bool onGpu = false)
    {
        Context = new FakeCoreContext { EventPublisher = Publisher };
        Inside = Context.Add(new FakeDevice { Address = "10.0.0.1" });
        Outside = Context.Add(new FakeDevice { Address = "10.0.0.2" });
        Recognizer.OnGpu = onGpu;
        Parts = new ReaderParts
        {
            Frames = _ => Frames,
            Recognizer = _ => Recognizer,
            Matcher = new FakeMatcher(),
            Vote = vote,
            Time = Time,
        };
        Plugin = new OcrPlugin(Parts);
    }

    public FakeCoreContext Context { get; }

    public FakeEventPublisher Publisher { get; } = new();

    public FakeDevice Inside { get; }

    public FakeDevice Outside { get; }

    public FakeRecognizer Recognizer { get; } = new();

    public FakeFrames Frames { get; } = new();

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.Zero));

    public ReaderParts Parts { get; }

    public OcrPlugin Plugin { get; }

    public static async Task<OcrHarness> StartAsync(Func<Area, IFrameVote>? vote = null, bool onGpu = false)
    {
        var harness = new OcrHarness(vote, onGpu);
        await harness.Plugin.StartAsync(harness.Context, CancellationToken.None);
        return harness;
    }

    /// <summary>Calls a method as <paramref name="caller"/> (default: a user with all devices).</summary>
    public Task<string?> CallAsync(string method, object? payload = null, FakeCallContext? caller = null) =>
        Context.InvokeAsync(Plugin, caller ?? Context.CallAsAllDevices(), method, payload is null ? "{}" : OcrJson.Serialize(payload));

    public async Task<Reader> SaveAsync(Reader reader, FakeCallContext? caller = null) =>
        OcrJson.Deserialize<Reader>(await CallAsync(OcrMethods.SaveReader, new SaveReaderRequest { Reader = reader }, caller));

    public Reader NewReader(string name = "Gate 2", ReaderMode mode = ReaderMode.OnDemand, Guid? device = null, params Area[] areas) => new()
    {
        Name = name,
        DeviceId = device ?? Inside.Id,
        Mode = mode,
        Areas = areas,
    };

    public async Task<ReaderList> ListAsync(FakeCallContext? caller = null) =>
        OcrJson.Deserialize<ReaderList>(await CallAsync(OcrMethods.ListReaders, caller: caller));

    /// <summary>The "Text read" events published so far.</summary>
    public IReadOnlyList<FakePublishedRuleEvent> TextRead => [.. Publisher.Published.Where(e => e.TypeId.StartsWith(OcrInfo.TextReadTypePrefix, StringComparison.Ordinal))];

    /// <summary>Sends a frame to a watching reader's camera and waits until it was read.</summary>
    public async Task SendAndWaitAsync(Guid deviceId, string text)
    {
        var before = Readings;
        Frames.Send(deviceId, text);
        await Eventually.TrueAsync(() => Readings > before, "the frame was read");
    }

    /// <summary>The readings told to the pages so far (each one after its rule events).</summary>
    public int Readings => Context.PublishedEvents.Count(e => e.Topic == OcrMethods.ReadingTopic && e.Audience.Kind == Oadm.Sdk.Plugins.PluginEventAudienceKind.AllDevices);

    public async ValueTask DisposeAsync() => await Plugin.DisposeAsync();
}
