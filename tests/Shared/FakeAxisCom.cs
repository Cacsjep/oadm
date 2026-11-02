using System.Collections.Concurrent;
using System.Net;

namespace Oadm.Tests.Shared;

/// <summary>
/// www.axis.com for the MyAxis tests, without network: an HTTP handler that answers from <see cref="Respond"/> and records
/// every request (method, URL, the Cookie header). <see cref="Listings"/> builds the two listings of the sign-in check.
/// </summary>
internal sealed class FakeAxisCom : HttpMessageHandler
{
    public const string Session = "axis_connect_session_sid=s3cr3t-session-value";
    public static readonly Uri Product = new("https://www.axis.com/ftp/pub_soft/MPQT/AXIS_A1001/");
    public static readonly Uri ProbeFile = new("https://www.axis.com/ftp/pub_soft/MPQT/AXIS_A1001/latest/A1001_12_6_1.bin");

    private readonly ConcurrentQueue<(HttpMethod Method, Uri Uri, string? Cookie)> _requests = new();

    /// <summary>The answer to a request; default: the sign-in check's listings and 200 for the probe file.</summary>
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; }

    public FakeAxisCom() => Respond = (request, _) => Task.FromResult(Listings(request) ?? new HttpResponseMessage(HttpStatusCode.OK));

    public IReadOnlyList<(HttpMethod Method, Uri Uri, string? Cookie)> Requests => [.. _requests];

    /// <summary>The two listings of the sign-in check (MPQT and the first product's latest/), else null.</summary>
    public static HttpResponseMessage? Listings(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = request.RequestUri!.AbsolutePath;
        return path switch
        {
            "/ftp/pub_soft/MPQT/" => Html("""<a href="../">Parent</a><a href="?C=N;O=D">Name</a><a href="AXIS_A1001/">AXIS_A1001/</a><a href="AXIS_A1214/">AXIS_A1214/</a>"""),
            "/ftp/pub_soft/MPQT/AXIS_A1001/latest/" => Html("""<a href="../">Parent</a><a href="A1001_12_6_1.txt">notes</a><a href="A1001_12_6_1.bin">A1001_12_6_1.bin</a>"""),
            _ => null,
        };
    }

    public static HttpResponseMessage Html(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    public static HttpResponseMessage RedirectTo(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cookie = request.Headers.TryGetValues("Cookie", out var values) ? string.Join("|", values) : null;
        _requests.Enqueue((request.Method, request.RequestUri!, cookie));
        var response = await Respond(request, cancellationToken).ConfigureAwait(false);
        response.RequestMessage ??= request;
        return response;
    }
}

/// <summary>A response body the test feeds piece by piece (a download in flight).</summary>
internal sealed class GatedStream(byte[] content) : Stream
{
    private readonly SemaphoreSlim _chunks = new(0);
    private int _position;
    private volatile int _released;

    /// <summary>Lets <paramref name="bytes"/> more bytes through.</summary>
    public void Release(int bytes)
    {
        _released = Math.Min(content.Length, _released + bytes);
        _chunks.Release();
    }

    /// <summary>Lets the rest through.</summary>
    public void ReleaseAll() => Release(content.Length);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_position >= _released)
        {
            if (_position >= content.Length)
            {
                return 0;
            }

            await _chunks.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        var count = Math.Min(buffer.Length, _released - _position);
        content.AsMemory(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => content.Length;

    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _chunks.Dispose();
        }

        base.Dispose(disposing);
    }
}
