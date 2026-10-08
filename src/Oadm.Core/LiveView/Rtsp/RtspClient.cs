using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

using Oadm.Core.Vapix;

namespace Oadm.Core.LiveView.Rtsp;

/// <summary>An RTSP response (status line, headers, body).</summary>
public sealed class RtspResponse(int statusCode, string reason, IReadOnlyList<KeyValuePair<string, string>> headers, string body)
{
    public int StatusCode { get; } = statusCode;

    public string Reason { get; } = reason;

    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; } = headers;

    public string Body { get; } = body;

    public string? Header(string name) => Headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    public IEnumerable<string> HeaderValues(string name) =>
        Headers.Where(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value);
}

/// <summary>One RTP or RTCP packet received interleaved on the RTSP connection (RFC 2326 10.12).</summary>
public readonly record struct InterleavedPacket(byte Channel, byte[] Data);

/// <summary>Live view failures with a reason the UI can show.</summary>
public sealed class LiveViewException : Exception
{
    public LiveViewException()
    {
    }

    public LiveViewException(string message)
        : base(message)
    {
    }

    public LiveViewException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public LiveViewException(LiveViewError error, string message, Exception? inner = null)
        : base(message, inner)
    {
        Error = error;
    }

    public LiveViewError Error { get; } = LiveViewError.Protocol;
}

public enum LiveViewError
{
    Protocol,
    Unreachable,
    Unauthorized,
    NotSupported,
}

/// <summary>
/// Minimal RTSP 1.0 client for one TCP connection: requests with Digest authentication and
/// reception of RTP interleaved on the same connection (RTP/AVP/TCP), so the stream needs only
/// port 554 and works through NAT and firewalls that allow it. Not thread-safe for reads;
/// writes are serialized so keep-alives and TEARDOWN may come from another task.
/// </summary>
public sealed class RtspClient : IAsyncDisposable
{
    public const int DefaultPort = 554;
    private const int MaxLine = 8 * 1024;
    private const int MaxBody = 64 * 1024;

    private readonly Stream _stream;
    private readonly IDisposable? _owner;
    private readonly BufferedByteReader _reader;
    private readonly NetworkCredential? _credentials;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private DigestAuthenticator? _digest;
    private int _cseq;
    private long _lastReceive = Stopwatch.GetTimestamp();

    public RtspClient(Stream stream, NetworkCredential? credentials, IDisposable? owner = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _owner = owner;
        _credentials = credentials;
        _reader = new BufferedByteReader(stream, 128 * 1024, 2 * 1024 * 1024);
    }

    /// <summary>Session id from the SETUP response (without the timeout parameter).</summary>
    public string? Session { get; private set; }

    /// <summary>Time since the last interleaved packet or RTSP response arrived (liveness of quiet streams).</summary>
    public TimeSpan SinceLastReceive => Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastReceive));

    /// <summary>Session timeout from the SETUP response (default 60 s).</summary>
    public TimeSpan SessionTimeout { get; private set; } = TimeSpan.FromSeconds(60);

    public static async Task<RtspClient> ConnectAsync(string host, int port, NetworkCredential? credentials, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var tcp = new TcpClient { NoDelay = true, ReceiveBufferSize = 512 * 1024 };
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectTimeout.CancelAfter(timeout);
            await tcp.ConnectAsync(host, port, connectTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            tcp.Dispose();
            throw new LiveViewException(LiveViewError.Unreachable, $"RTSP port {port} on {host} is not reachable.", ex);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }

        return new RtspClient(tcp.GetStream(), credentials, tcp);
    }

    /// <summary>Sends a request and reads its response; answers a 401 once with Digest credentials.</summary>
    public async Task<RtspResponse> SendAsync(string method, Uri url, IEnumerable<KeyValuePair<string, string>>? headers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);
        var extra = headers?.ToList() ?? [];
        var response = await SendOnceAsync(method, url, extra, ct).ConfigureAwait(false);
        if (response.StatusCode == 401 && _credentials is not null)
        {
            _digest = DigestAuthenticator.FromChallenges(response.HeaderValues("WWW-Authenticate"), _credentials)
                ?? throw new LiveViewException(LiveViewError.Unauthorized, "The camera offers no Digest authentication for RTSP (Basic over plain RTSP is refused).");
            response = await SendOnceAsync(method, url, extra, ct).ConfigureAwait(false);
        }

        if (response.StatusCode is 401 or 403)
        {
            throw new LiveViewException(LiveViewError.Unauthorized, $"RTSP {method}: the camera rejected the stored credentials ({response.StatusCode}).");
        }

        if (method == "SETUP" && response.Header("Session") is { } session)
        {
            var parts = session.Split(';', StringSplitOptions.TrimEntries);
            Session = parts[0];
            foreach (var p in parts.Skip(1))
            {
                if (p.StartsWith("timeout=", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(p["timeout=".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                {
                    SessionTimeout = TimeSpan.FromSeconds(seconds);
                }
            }
        }

        return response;
    }

    /// <summary>Writes a request without waiting for the response (keep-alive while RTP flows; the read loop skips the answer).</summary>
    public Task SendWithoutResponseAsync(string method, Uri url, CancellationToken ct) => WriteRequestAsync(method, url, [], ct);

    /// <summary>Next interleaved packet; RTSP responses in between are skipped. Null at end of stream.</summary>
    public async Task<InterleavedPacket?> ReadInterleavedAsync(CancellationToken ct)
    {
        while (true)
        {
            var first = await _reader.PeekByteAsync(ct).ConfigureAwait(false);
            if (first < 0)
            {
                return null;
            }

            if (first == '$')
            {
                if (!await _reader.EnsureAsync(4, ct).ConfigureAwait(false))
                {
                    return null;
                }

                var header = _reader.Buffered;
                var channel = header[1];
                var length = (header[2] << 8) | header[3];
                _reader.Skip(4);
                var data = await _reader.ReadExactAsync(length, ct).ConfigureAwait(false);
                if (data is null)
                {
                    return null;
                }

                Interlocked.Exchange(ref _lastReceive, Stopwatch.GetTimestamp());
                return new InterleavedPacket(channel, data);
            }

            if (await ReadResponseAsync(ct).ConfigureAwait(false) is null)
            {
                return null;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        _owner?.Dispose();
        _writeLock.Dispose();
    }

    private async Task<RtspResponse> SendOnceAsync(string method, Uri url, List<KeyValuePair<string, string>> headers, CancellationToken ct)
    {
        var cseq = await WriteRequestAsync(method, url, headers, ct).ConfigureAwait(false);
        while (true)
        {
            // Interleaved data that arrives before the response (e.g. RTP right after PLAY) is not expected
            // before PLAY completes; skip it defensively.
            var first = await _reader.PeekByteAsync(ct).ConfigureAwait(false);
            if (first == '$')
            {
                if (await ReadInterleavedAsync(ct).ConfigureAwait(false) is null)
                {
                    throw new LiveViewException(LiveViewError.Protocol, $"RTSP {method}: connection closed.");
                }

                continue;
            }

            var response = await ReadResponseAsync(ct).ConfigureAwait(false)
                ?? throw new LiveViewException(LiveViewError.Protocol, $"RTSP {method}: connection closed.");
            if (response.Header("CSeq") is { } value && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var got) && got != cseq)
            {
                continue; // answer to an earlier keep-alive
            }

            return response;
        }
    }

    private async Task<int> WriteRequestAsync(string method, Uri url, IEnumerable<KeyValuePair<string, string>> headers, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cseq = ++_cseq;
            var uri = url.AbsoluteUri;
            var sb = new StringBuilder();
            sb.Append(CultureInfo.InvariantCulture, $"{method} {uri} RTSP/1.0\r\n");
            sb.Append(CultureInfo.InvariantCulture, $"CSeq: {cseq}\r\n");
            sb.Append("User-Agent: OADM\r\n");
            if (_digest is not null)
            {
                sb.Append(CultureInfo.InvariantCulture, $"Authorization: {_digest.Authorize(method, uri)}\r\n");
            }

            if (Session is not null)
            {
                sb.Append(CultureInfo.InvariantCulture, $"Session: {Session}\r\n");
            }

            foreach (var (name, value) in headers)
            {
                sb.Append(CultureInfo.InvariantCulture, $"{name}: {value}\r\n");
            }

            sb.Append("\r\n");
            var bytes = Encoding.ASCII.GetBytes(sb.ToString());
            await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
            return cseq;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<RtspResponse?> ReadResponseAsync(CancellationToken ct)
    {
        var status = await _reader.ReadLineAsync(MaxLine, ct).ConfigureAwait(false);
        while (status is not null && status.Length == 0)
        {
            status = await _reader.ReadLineAsync(MaxLine, ct).ConfigureAwait(false);
        }

        if (status is null)
        {
            return null;
        }

        // RTSP/1.0 200 OK
        var parts = status.Split(' ', 3);
        if (parts.Length < 2 || !parts[0].StartsWith("RTSP/", StringComparison.Ordinal)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var code))
        {
            throw new LiveViewException(LiveViewError.Protocol, "Not an RTSP response: " + (status.Length > 80 ? status[..80] : status));
        }

        var headers = new List<KeyValuePair<string, string>>();
        while (true)
        {
            var line = await _reader.ReadLineAsync(MaxLine, ct).ConfigureAwait(false);
            if (line is null)
            {
                return null;
            }

            if (line.Length == 0)
            {
                break;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                headers.Add(new(line[..colon].Trim(), line[(colon + 1)..].Trim()));
            }
        }

        var body = string.Empty;
        var lengthHeader = headers.FirstOrDefault(h => h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)).Value;
        if (lengthHeader is not null && int.TryParse(lengthHeader, NumberStyles.None, CultureInfo.InvariantCulture, out var length) && length > 0)
        {
            if (length > MaxBody)
            {
                throw new LiveViewException(LiveViewError.Protocol, $"RTSP response body of {length} bytes is too large.");
            }

            var bytes = await _reader.ReadExactAsync(length, ct).ConfigureAwait(false);
            if (bytes is null)
            {
                return null;
            }

            body = Encoding.UTF8.GetString(bytes);
        }

        Interlocked.Exchange(ref _lastReceive, Stopwatch.GetTimestamp());
        return new RtspResponse(code, parts.Length > 2 ? parts[2] : string.Empty, headers, body);
    }
}
