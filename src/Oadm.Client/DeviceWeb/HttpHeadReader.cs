using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Oadm.Client.DeviceWeb;

/// <summary>One HTTP/1.1 request head of the embedded browser.</summary>
public sealed class HttpRequestHead(string method, string target, IReadOnlyList<KeyValuePair<string, string>> headers)
{
    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "Proxy-Connection", "TE", "Trailer",
        "Transfer-Encoding", "Upgrade",
    };

    public string Method { get; } = method;

    /// <summary>The origin-form target, "/camera/index.html?x=1".</summary>
    public string Target { get; } = target;

    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; } = headers;

    public static bool IsHopByHop(string name) => HopByHop.Contains(name);

    /// <summary>The first value of a header, null when missing.</summary>
    public string? Header(string name)
    {
        foreach (var (key, value) in Headers)
        {
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>Content-Length, or -1 without one.</summary>
    public long ContentLength =>
        long.TryParse(Header("Content-Length"), NumberStyles.None, CultureInfo.InvariantCulture, out var length) ? length : -1;

    public bool IsChunked => Header("Transfer-Encoding")?.Contains("chunked", StringComparison.OrdinalIgnoreCase) == true;

    public bool HasBody => IsChunked || ContentLength > 0;

    public bool WantsClose => Header("Connection")?.Contains("close", StringComparison.OrdinalIgnoreCase) == true;

    public bool IsWebSocket =>
        Header("Upgrade")?.Equals("websocket", StringComparison.OrdinalIgnoreCase) == true
        && Header("Connection")?.Contains("upgrade", StringComparison.OrdinalIgnoreCase) == true
        && Header("Sec-WebSocket-Key") is not null;
}

/// <summary>
/// Reads request heads and bodies from a connection with its own buffer (bytes after a head stay for the body or the
/// next request). Heads are at most <see cref="DeviceWebRelay.MaxHeadBytes"/>; a malformed one throws
/// <see cref="InvalidDataException"/>.
/// </summary>
public sealed class HttpHeadReader(Stream stream)
{
    private const int MaxHeaders = 200;
    private readonly byte[] _buffer = new byte[DeviceWebRelay.MaxHeadBytes];
    private int _start;
    private int _end;

    /// <summary>The next request head, null when the connection closed between requests.</summary>
    public async Task<HttpRequestHead?> ReadHeadAsync(CancellationToken ct)
    {
        while (true)
        {
            var headEnd = IndexOfHeadEnd();
            if (headEnd >= 0)
            {
                var text = Encoding.Latin1.GetString(_buffer, _start, headEnd - _start);
                _start = headEnd + 4;
                return Parse(text);
            }

            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }

            if (_end == _buffer.Length)
            {
                throw new InvalidDataException("The request head is too large.");
            }

            var read = await stream.ReadAsync(_buffer.AsMemory(_end), ct).ConfigureAwait(false);
            if (read == 0)
            {
                if (_end > _start)
                {
                    throw new InvalidDataException("The connection closed inside a request head.");
                }

                return null;
            }

            _end += read;
        }
    }

    /// <summary>The body of <paramref name="request"/> in chunks (Content-Length or chunked).</summary>
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadBodyAsync(HttpRequestHead request, [EnumeratorCancellation] CancellationToken ct)
    {
        if (request.IsChunked)
        {
            while (true)
            {
                var line = await ReadLineAsync(ct).ConfigureAwait(false);
                var size = line.Split(';')[0].Trim();
                if (!long.TryParse(size, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var length) || length < 0)
                {
                    throw new InvalidDataException("Bad chunk size.");
                }

                if (length == 0)
                {
                    // Trailers until the empty line.
                    while ((await ReadLineAsync(ct).ConfigureAwait(false)).Length > 0)
                    {
                    }

                    yield break;
                }

                await foreach (var part in ReadExactAsync(length, ct).ConfigureAwait(false))
                {
                    yield return part;
                }

                if ((await ReadLineAsync(ct).ConfigureAwait(false)).Length != 0)
                {
                    throw new InvalidDataException("Bad chunk end.");
                }
            }
        }

        await foreach (var part in ReadExactAsync(Math.Max(0, request.ContentLength), ct).ConfigureAwait(false))
        {
            yield return part;
        }
    }

    /// <summary>The connection as a stream for a WebSocket: buffered bytes first, then the network; writes go straight through.</summary>
    public Stream AsStream() => new BufferedPrefixStream(stream, _buffer.AsMemory(_start, _end - _start).ToArray());

    private async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadExactAsync(long length, [EnumeratorCancellation] CancellationToken ct)
    {
        var remaining = length;
        while (remaining > 0)
        {
            if (_end == _start)
            {
                _start = 0;
                _end = await stream.ReadAsync(_buffer, ct).ConfigureAwait(false);
                if (_end == 0)
                {
                    throw new IOException("The connection closed inside a request body.");
                }
            }

            var take = (int)Math.Min(remaining, _end - _start);
            var part = _buffer.AsMemory(_start, take).ToArray();
            _start += take;
            remaining -= take;
            yield return part;
        }
    }

    private async Task<string> ReadLineAsync(CancellationToken ct)
    {
        while (true)
        {
            var index = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (index >= 0)
            {
                var line = Encoding.Latin1.GetString(_buffer, _start, index - _start).TrimEnd('\r');
                _start = index + 1;
                return line;
            }

            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }

            if (_end == _buffer.Length)
            {
                throw new InvalidDataException("A line is too long.");
            }

            var read = await stream.ReadAsync(_buffer.AsMemory(_end), ct).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("The connection closed inside a request body.");
            }

            _end += read;
        }
    }

    private int IndexOfHeadEnd()
    {
        for (var i = _start; i + 3 < _end; i++)
        {
            if (_buffer[i] == '\r' && _buffer[i + 1] == '\n' && _buffer[i + 2] == '\r' && _buffer[i + 3] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static HttpRequestHead Parse(string text)
    {
        var lines = text.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal) || parts[0].Length == 0 || !parts[1].StartsWith('/'))
        {
            throw new InvalidDataException("Bad request line.");
        }

        if (lines.Length - 1 > MaxHeaders)
        {
            throw new InvalidDataException("Too many headers.");
        }

        var headers = new List<KeyValuePair<string, string>>(lines.Length - 1);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                throw new InvalidDataException("Bad header line.");
            }

            headers.Add(KeyValuePair.Create(line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }

        return new HttpRequestHead(parts[0], parts[1], headers);
    }

    /// <summary>Reads <c>prefix</c> first, then the inner stream; writes go to the inner stream.</summary>
    private sealed class BufferedPrefixStream(Stream inner, byte[] prefix) : Stream
    {
        private int _offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset < prefix.Length)
            {
                var take = Math.Min(buffer.Length, prefix.Length - _offset);
                prefix.AsMemory(_offset, take).CopyTo(buffer);
                _offset += take;
                return take;
            }

            return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_offset < prefix.Length)
            {
                var take = Math.Min(count, prefix.Length - _offset);
                Buffer.BlockCopy(prefix, _offset, buffer, offset, take);
                _offset += take;
                return take;
            }

            return inner.Read(buffer, offset, count);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => inner.WriteAsync(buffer, offset, count, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override void Flush() => inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
