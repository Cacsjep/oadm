using System.Globalization;
using System.Text;

namespace Oadm.Plugins.Est.Protocol;

/// <summary>One HTTP/1.1 request: method, path (without query), headers (case-insensitive) and body.</summary>
public sealed record HttpRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    /// <summary>The client wants the connection closed after the answer (HTTP/1.0 or "Connection: close").</summary>
    public bool CloseAfter { get; init; }

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>An answer: status, content type, body (text or base64 DER), extra headers.</summary>
public sealed record HttpResponse(int Status, string ContentType, byte[] Body)
{
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = [];

    /// <summary>A plain-text answer (errors).</summary>
    public static HttpResponse Text(int status, string text) => new(status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text + "\r\n"));
}

/// <summary>The request was not HTTP/1.1 the server can read; answer 400 and close.</summary>
public sealed class HttpFormatException(string message) : Exception(message);

/// <summary>
/// A small HTTP/1.1 server side for EST (the SDK has no HTTPS helper on a raw stream yet; docs/spec/est-server.md): header
/// block at most 16 KB, body at most 64 KB (Content-Length or chunked), <c>Expect: 100-continue</c> answered, keep-alive.
/// </summary>
public static class EstHttp
{
    public const int MaxHeaderBytes = 16 * 1024;
    public const int MaxBodyBytes = 64 * 1024;

    /// <summary>A reader for the requests of one connection (keeps what it read ahead).</summary>
    public static HttpRequestReader Reader(Stream stream) => new(stream);

    internal static async Task<HttpRequest?> ReadAsync(Stream stream, LineReader reader, CancellationToken ct)
    {
        var requestLine = await reader.ReadLineAsync(MaxHeaderBytes, ct).ConfigureAwait(false);
        if (requestLine is null)
        {
            return null;
        }

        var parts = requestLine.Split(' ');
        if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
        {
            throw new HttpFormatException("Not an HTTP/1.1 request.");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var total = requestLine.Length;
        while (true)
        {
            var line = await reader.ReadLineAsync(MaxHeaderBytes - total, ct).ConfigureAwait(false)
                ?? throw new HttpFormatException("The request ended in its headers.");
            total += line.Length + 2;
            if (line.Length == 0)
            {
                break;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                throw new HttpFormatException("A header line is not valid.");
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            headers[name] = headers.TryGetValue(name, out var earlier) ? earlier + ", " + value : value;
        }

        var target = parts[1];
        var query = target.IndexOf('?', StringComparison.Ordinal);
        var path = query >= 0 ? target[..query] : target;
        var chunked = headers.TryGetValue("Transfer-Encoding", out var encoding) && encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase);
        var length = 0;
        if (!chunked && headers.TryGetValue("Content-Length", out var lengthText)
            && (!int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out length) || length > MaxBodyBytes))
        {
            throw new HttpFormatException("The request body is too large.");
        }

        // 100 Continue only for a body the server will read.
        if (headers.TryGetValue("Expect", out var expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
        {
            await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        byte[] body;
        body = chunked ? await ReadChunkedAsync(reader, ct).ConfigureAwait(false)
            : length > 0 ? await reader.ReadExactlyAsync(length, ct).ConfigureAwait(false)
            : [];

        var close = parts[2] == "HTTP/1.0" || (headers.TryGetValue("Connection", out var connection) && connection.Contains("close", StringComparison.OrdinalIgnoreCase));
        return new HttpRequest(parts[0], path, headers, body) { CloseAfter = close };
    }

    /// <summary>Writes the answer; "Connection: close" when <paramref name="close"/>.</summary>
    public static async Task WriteAsync(Stream stream, HttpResponse response, bool close, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(response);
        var head = new StringBuilder();
        head.Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {response.Status} {Reason(response.Status)}\r\n");
        head.Append(CultureInfo.InvariantCulture, $"Content-Type: {response.ContentType}\r\n");
        head.Append(CultureInfo.InvariantCulture, $"Content-Length: {response.Body.Length}\r\n");
        foreach (var (name, value) in response.Headers)
        {
            head.Append(CultureInfo.InvariantCulture, $"{name}: {value}\r\n");
        }

        head.Append(close ? "Connection: close\r\n\r\n" : "\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct).ConfigureAwait(false);
        await stream.WriteAsync(response.Body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        503 => "Service Unavailable",
        _ => "Error",
    };

    private static async Task<byte[]> ReadChunkedAsync(LineReader reader, CancellationToken ct)
    {
        using var body = new MemoryStream();
        while (true)
        {
            var sizeLine = await reader.ReadLineAsync(256, ct).ConfigureAwait(false) ?? throw new HttpFormatException("The request body ended early.");
            var semicolon = sizeLine.IndexOf(';', StringComparison.Ordinal);
            if (!int.TryParse(semicolon >= 0 ? sizeLine[..semicolon] : sizeLine, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) || size < 0)
            {
                throw new HttpFormatException("A chunk size is not valid.");
            }

            if (size == 0)
            {
                // trailers until the empty line
                while (await reader.ReadLineAsync(MaxHeaderBytes, ct).ConfigureAwait(false) is { Length: > 0 })
                {
                }

                return body.ToArray();
            }

            if (body.Length + size > MaxBodyBytes)
            {
                throw new HttpFormatException("The request body is too large.");
            }

            body.Write(await reader.ReadExactlyAsync(size, ct).ConfigureAwait(false));
            if (await reader.ReadLineAsync(2, ct).ConfigureAwait(false) is not { Length: 0 })
            {
                throw new HttpFormatException("A chunk does not end with a line break.");
            }
        }
    }

    /// <summary>Buffered reads of lines (CRLF or LF) and of exact byte counts.</summary>
    internal sealed class LineReader(Stream stream)
    {
        private readonly byte[] _buffer = new byte[4096];
        private int _start;
        private int _end;

        /// <summary>The next line without its line break (ASCII); null at the end before any byte.</summary>
        public async Task<string?> ReadLineAsync(int max, CancellationToken ct)
        {
            var line = new List<byte>();
            while (true)
            {
                if (_start == _end && !await FillAsync(ct).ConfigureAwait(false))
                {
                    return line.Count == 0 ? null : throw new HttpFormatException("The request ended in a line.");
                }

                var b = _buffer[_start++];
                if (b == '\n')
                {
                    if (line.Count > 0 && line[^1] == '\r')
                    {
                        line.RemoveAt(line.Count - 1);
                    }

                    return Encoding.ASCII.GetString(line.ToArray());
                }

                line.Add(b);
                if (line.Count > max)
                {
                    throw new HttpFormatException("The request headers are too long.");
                }
            }
        }

        public async Task<byte[]> ReadExactlyAsync(int count, CancellationToken ct)
        {
            var result = new byte[count];
            var done = 0;
            while (done < count)
            {
                if (_start == _end && !await FillAsync(ct).ConfigureAwait(false))
                {
                    throw new HttpFormatException("The request body ended early.");
                }

                var take = Math.Min(count - done, _end - _start);
                Array.Copy(_buffer, _start, result, done, take);
                _start += take;
                done += take;
            }

            return result;
        }

        private async Task<bool> FillAsync(CancellationToken ct)
        {
            _start = 0;
            _end = await stream.ReadAsync(_buffer, ct).ConfigureAwait(false);
            return _end > 0;
        }
    }
}

/// <summary>Reads the requests of one connection, one after the other.</summary>
public sealed class HttpRequestReader
{
    private readonly Stream _stream;
    private readonly EstHttp.LineReader _reader;

    internal HttpRequestReader(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _reader = new EstHttp.LineReader(stream);
    }

    /// <summary>The next request; null when the client closed the connection before a new one.</summary>
    public Task<HttpRequest?> ReadAsync(CancellationToken ct) => EstHttp.ReadAsync(_stream, _reader, ct);
}
