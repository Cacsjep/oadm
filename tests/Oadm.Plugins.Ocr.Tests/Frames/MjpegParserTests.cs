using System.Text;

using Oadm.Plugins.Ocr.Frames;

namespace Oadm.Plugins.Ocr.Tests.Frames;

/// <summary>The MJPEG answer of a camera (docs/spec/ocr.md "Tests": boundary, Content-Length, a part without it).</summary>
public sealed class MjpegParserTests
{
    /// <summary>3 s at 2 frames per second, 640x360, recorded read-only from the AXIS P3265-V (10.0.0.48).</summary>
    internal static readonly string RecordedPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Mjpeg", "p3265-640x360-2fps.mjpeg");

    /// <summary>The Content-Type header of the recorded answer.</summary>
    internal const string RecordedContentType = "multipart/x-mixed-replace; boundary=myboundary";

    /// <summary>The Content-Length of each part of the recorded answer.</summary>
    internal static readonly int[] RecordedLengths = [13930, 13914, 13911, 13898, 13899, 13896];

    internal static byte[] Jpeg(int size, byte fill)
    {
        var bytes = new byte[size];
        Array.Fill(bytes, fill);
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[^2] = 0xFF;
        bytes[^1] = 0xD9;
        return bytes;
    }

    /// <summary>A part as an Axis camera writes it, with or without Content-Length.</summary>
    internal static byte[] Part(byte[] jpeg, string boundary = "myboundary", bool withLength = true)
    {
        var head = $"--{boundary}\r\nContent-Type: image/jpeg\r\n" + (withLength ? $"Content-Length: {jpeg.Length}\r\n" : "") + "\r\n";
        return [.. Encoding.ASCII.GetBytes(head), .. jpeg, .. "\r\n"u8];
    }

    private static async Task<List<byte[]>> ReadAllAsync(byte[] answer, string boundary, int maxFrameBytes = MjpegParser.MaxFrameBytes, int chunk = 4096)
    {
        using var stream = new ChunkedStream(answer, chunk);
        var frames = new List<byte[]>();
        await foreach (var frame in MjpegParser.ReadFramesAsync(stream, boundary, maxFrameBytes, CancellationToken.None))
        {
            frames.Add(frame.ToArray());
        }

        return frames;
    }

    [Theory]
    [InlineData(7)]
    [InlineData(64 * 1024)]
    public async Task TheRecordedAnswerGivesEveryPicture(int chunk)
    {
        var answer = await File.ReadAllBytesAsync(RecordedPath, CancellationToken.None);
        Assert.True(answer.Length < 2 * 1024 * 1024);

        var frames = await ReadAllAsync(answer, MjpegParser.BoundaryOf(RecordedContentType)!, chunk: chunk);

        Assert.Equal(RecordedLengths, frames.Select(f => f.Length));
        Assert.All(frames, f => Assert.Equal([0xFF, 0xD8], f[..2]));
        Assert.All(frames, f => Assert.Equal([0xFF, 0xD9], f[^2..]));
    }

    [Theory]
    [InlineData("multipart/x-mixed-replace; boundary=myboundary", "myboundary")]
    [InlineData("multipart/x-mixed-replace;boundary=\"my boundary\"", "my boundary")]
    [InlineData("Multipart/X-Mixed-Replace; BOUNDARY=--abc", "--abc")]
    [InlineData("image/jpeg", null)]
    [InlineData("multipart/x-mixed-replace", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TheBoundaryComesFromTheHeader(string? contentType, string? boundary) =>
        Assert.Equal(boundary, MjpegParser.BoundaryOf(contentType));

    [Fact]
    public async Task PartsWithoutALengthEndAtTheNextBoundary()
    {
        byte[] answer = [.. Part(Jpeg(500, 1), withLength: false), .. Part(Jpeg(300, 2), withLength: false), .. Part(Jpeg(200, 3))];

        var frames = await ReadAllAsync(answer, "myboundary", chunk: 33);

        Assert.Equal([Jpeg(500, 1), Jpeg(300, 2), Jpeg(200, 3)], frames);
    }

    [Fact]
    public async Task AQuotedBoundaryWithASpaceWorks()
    {
        byte[] answer = [.. Part(Jpeg(100, 1), "my boundary"), .. Part(Jpeg(100, 2), "my boundary", withLength: false), .. "--my boundary--\r\n"u8];

        var frames = await ReadAllAsync(answer, MjpegParser.BoundaryOf("multipart/x-mixed-replace; boundary=\"my boundary\"")!);

        Assert.Equal([Jpeg(100, 1), Jpeg(100, 2)], frames);
    }

    [Fact]
    public async Task ABoundaryDeclaredWithItsDashesWorks()
    {
        // Some cameras declare "--myboundary" and write it as is.
        byte[] answer = [.. Encoding.ASCII.GetBytes($"--myboundary\r\nContent-Length: 100\r\n\r\n"), .. Jpeg(100, 1), .. "\r\n"u8, .. Part(Jpeg(100, 2), withLength: false), .. "--myboundary\r\n"u8];

        var frames = await ReadAllAsync(answer, "--myboundary");

        Assert.Equal([Jpeg(100, 1), Jpeg(100, 2)], frames);
    }

    [Fact]
    public async Task BytesBeforeAndBetweenPartsAreSkipped()
    {
        byte[] answer =
        [
            .. "HTTP noise\r\n\r\n"u8, .. Part(Jpeg(100, 1)),
            .. "garbage between parts\r\n"u8, .. Part(Jpeg(100, 2)),
            .. new byte[5000], .. Part(Jpeg(100, 3)),
        ];

        var frames = await ReadAllAsync(answer, "myboundary", chunk: 50);

        Assert.Equal([Jpeg(100, 1), Jpeg(100, 2), Jpeg(100, 3)], frames);
    }

    [Fact]
    public async Task APictureAboveTheLimitIsSkipped()
    {
        byte[] answer =
        [
            .. Part(Jpeg(5000, 1)), .. Part(Jpeg(100, 2)),
            .. Part(Jpeg(5000, 3), withLength: false), .. Part(Jpeg(100, 4), withLength: false), .. "--myboundary"u8,
        ];

        var frames = await ReadAllAsync(answer, "myboundary", maxFrameBytes: 1000, chunk: 64);

        Assert.Equal([Jpeg(100, 2), Jpeg(100, 4)], frames);
    }

    [Fact]
    public async Task PartsThatAreNoPictureAreSkipped()
    {
        byte[] answer = [.. "--myboundary\r\nContent-Type: text/plain\r\nContent-Length: 5\r\n\r\nhello\r\n"u8, .. Part(Jpeg(100, 2))];

        Assert.Equal([Jpeg(100, 2)], await ReadAllAsync(answer, "myboundary"));
    }

    [Fact]
    public async Task TheClosingBoundaryEndsTheAnswer()
    {
        byte[] answer = [.. Part(Jpeg(100, 1)), .. "--myboundary--\r\n"u8, .. Part(Jpeg(100, 2))];

        Assert.Equal([Jpeg(100, 1)], await ReadAllAsync(answer, "myboundary"));
    }

    [Fact]
    public async Task AnUnfinishedLastPartIsDropped()
    {
        var last = Part(Jpeg(1000, 2));
        byte[] counted = [.. Part(Jpeg(100, 1)), .. last[..500]];
        var uncounted = Part(Jpeg(1000, 2), withLength: false);
        byte[] open = [.. Part(Jpeg(100, 1)), .. uncounted[..^2]];

        Assert.Equal([Jpeg(100, 1)], await ReadAllAsync(counted, "myboundary"));
        Assert.Equal([Jpeg(100, 1)], await ReadAllAsync(open, "myboundary"));
    }

    [Fact]
    public async Task AnEmptyAnswerHasNoPicture() =>
        Assert.Empty(await ReadAllAsync([], "myboundary"));

    /// <summary>A stream that hands out at most <c>chunk</c> bytes per read.</summary>
    private sealed class ChunkedStream(byte[] data, int chunk) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, chunk));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], cancellationToken);
    }
}
