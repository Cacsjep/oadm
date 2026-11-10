using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;

namespace Oadm.Plugins.Shared.Vision;

/// <summary>
/// Reads the JPEG pictures of a camera's MJPEG answer (<c>multipart/x-mixed-replace</c>, docs/spec/ocr.md "Readers"; shared by the vision add-ons). Each
/// part starts after a boundary line and its headers; its body is <c>Content-Length</c> bytes when the part says so, else
/// everything up to the next boundary. Bytes between parts are skipped, parts that are no JPEG are skipped, a part larger
/// than the limit is skipped without holding it in memory, an unfinished last part is dropped.
/// </summary>
internal static class MjpegParser
{
    /// <summary>A picture larger than this is skipped.</summary>
    public const int MaxFrameBytes = 10 * 1024 * 1024;

    /// <summary>Headers of one part longer than this are no headers: the parser looks for the next boundary.</summary>
    private const int MaxHeaderBytes = 16 * 1024;

    /// <summary>The boundary of a <c>multipart/x-mixed-replace</c> answer (quotes removed), or null for any other answer.</summary>
    public static string? BoundaryOf(MediaTypeHeaderValue? contentType)
    {
        if (contentType?.MediaType is not { } media || !media.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var value = contentType.Parameters
            .FirstOrDefault(p => string.Equals(p.Name, "boundary", StringComparison.OrdinalIgnoreCase))?.Value?.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>The boundary of the content type header <paramref name="contentType"/>, or null.</summary>
    public static string? BoundaryOf(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var parsed) ? BoundaryOf(parsed) : null;

    /// <summary>
    /// The pictures in <paramref name="stream"/> separated by <paramref name="boundary"/> (with or without its leading
    /// <c>--</c>), until the stream ends or the closing boundary.
    /// </summary>
    public static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadFramesAsync(
        Stream stream,
        string boundary,
        int maxFrameBytes = MaxFrameBytes,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(boundary);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFrameBytes, 1);
        var token = Encoding.ASCII.GetBytes(boundary);
        var buffer = new PartBuffer(stream, maxFrameBytes + token.Length + MaxHeaderBytes);
        while (await SkipPastBoundaryAsync(buffer, token, ct).ConfigureAwait(false))
        {
            var headers = await ReadHeadersAsync(buffer, ct).ConfigureAwait(false);
            if (headers is null)
            {
                continue; // no headers: garbage, look for the next boundary
            }

            var frame = headers.Value.Length is { } length
                ? await ReadCountedAsync(buffer, length, maxFrameBytes, ct).ConfigureAwait(false)
                : await ReadToBoundaryAsync(buffer, token, maxFrameBytes, ct).ConfigureAwait(false);
            if (frame is { Length: > 2 } jpeg && jpeg[0] == 0xFF && jpeg[1] == 0xD8)
            {
                yield return jpeg;
            }
        }
    }

    /// <summary>Consumes everything up to and including the next boundary line; false at the end or the closing boundary.</summary>
    private static async Task<bool> SkipPastBoundaryAsync(PartBuffer buffer, byte[] token, CancellationToken ct)
    {
        while (true)
        {
            var at = buffer.Data.IndexOf(token);
            if (at >= 0)
            {
                buffer.Consume(at + token.Length);
                break;
            }

            buffer.Consume(Math.Max(0, buffer.Count - token.Length + 1));
            if (!await buffer.FillAsync(ct).ConfigureAwait(false))
            {
                return false;
            }
        }

        // The rest of the boundary line: "--" closes the answer.
        while (buffer.Data.IndexOf((byte)'\n') is var newline && newline < 0)
        {
            if (buffer.Count > MaxHeaderBytes || !await buffer.FillAsync(ct).ConfigureAwait(false))
            {
                return false;
            }
        }

        var end = buffer.Data.IndexOf((byte)'\n');
        var closing = buffer.Data[..end].StartsWith("--"u8);
        buffer.Consume(end + 1);
        return !closing;
    }

    /// <summary>The part's headers up to the empty line; null when there are none within the limit.</summary>
    private static async Task<PartHeaders?> ReadHeadersAsync(PartBuffer buffer, CancellationToken ct)
    {
        long? length = null;
        var read = 0;
        while (true)
        {
            var newline = buffer.Data.IndexOf((byte)'\n');
            if (newline < 0)
            {
                if (buffer.Count > MaxHeaderBytes || !await buffer.FillAsync(ct).ConfigureAwait(false))
                {
                    return null;
                }

                continue;
            }

            var line = Encoding.ASCII.GetString(buffer.Data[..newline]).TrimEnd('\r');
            buffer.Consume(newline + 1);
            read += newline + 1;
            if (line.Length == 0)
            {
                return new PartHeaders(length);
            }

            if (read > MaxHeaderBytes)
            {
                return null;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0
                && line.AsSpan(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(line.AsSpan(colon + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                length = value;
            }
        }
    }

    /// <summary>A body of <paramref name="length"/> bytes; null when it is larger than the limit (skipped) or the stream ends first.</summary>
    private static async Task<byte[]?> ReadCountedAsync(PartBuffer buffer, long length, int maxFrameBytes, CancellationToken ct)
    {
        if (length > maxFrameBytes)
        {
            var left = length;
            while (left > 0)
            {
                if (buffer.Count == 0 && !await buffer.FillAsync(ct).ConfigureAwait(false))
                {
                    return null;
                }

                var skip = (int)Math.Min(left, buffer.Count);
                buffer.Consume(skip);
                left -= skip;
            }

            return null;
        }

        while (buffer.Count < length)
        {
            if (!await buffer.FillAsync(ct).ConfigureAwait(false))
            {
                return null;
            }
        }

        var body = buffer.Data[..(int)length].ToArray();
        buffer.Consume((int)length);
        return body;
    }

    /// <summary>
    /// A body without a length: everything up to the next boundary (which stays in the buffer), without the line break and
    /// <c>--</c> before it; null when it grows beyond the limit (skipped) or the stream ends first.
    /// </summary>
    private static async Task<byte[]?> ReadToBoundaryAsync(PartBuffer buffer, byte[] token, int maxFrameBytes, CancellationToken ct)
    {
        var tooLarge = false;
        var searched = 0;
        while (true)
        {
            var at = buffer.Data[searched..].IndexOf(token);
            if (at >= 0)
            {
                at += searched;
                if (tooLarge)
                {
                    buffer.Consume(at);
                    return null;
                }

                var body = buffer.Data[..at];
                body = body.EndsWith("--"u8) ? body[..^2] : body;
                body = body.EndsWith("\n"u8) ? body[..^1] : body;
                body = body.EndsWith("\r"u8) ? body[..^1] : body;
                var frame = body.ToArray();
                buffer.Consume(at);
                return frame;
            }

            if (buffer.Count > maxFrameBytes + token.Length)
            {
                // Too large: drop what was read, keep only what could start the boundary.
                tooLarge = true;
                buffer.Consume(buffer.Count - token.Length + 1);
            }

            searched = Math.Max(0, buffer.Count - token.Length + 1);
            if (!await buffer.FillAsync(ct).ConfigureAwait(false))
            {
                return null;
            }
        }
    }

    private readonly record struct PartHeaders(long? Length);

    /// <summary>The unread bytes of the stream, grown up to <c>capacity</c>.</summary>
    private sealed class PartBuffer(Stream stream, int capacity)
    {
        private const int ReadSize = 64 * 1024;
        private byte[] _bytes = new byte[ReadSize];
        private int _start;
        private int _end;

        public ReadOnlySpan<byte> Data => _bytes.AsSpan(_start, _end - _start);

        public int Count => _end - _start;

        public void Consume(int count)
        {
            _start += count;
            if (_start == _end)
            {
                _start = _end = 0;
            }
        }

        /// <summary>Reads more; false at the end of the stream.</summary>
        public async ValueTask<bool> FillAsync(CancellationToken ct)
        {
            if (_start > 0)
            {
                Array.Copy(_bytes, _start, _bytes, 0, Count);
                _end = Count;
                _start = 0;
            }

            if (_bytes.Length - _end < ReadSize / 4 && _bytes.Length < capacity + ReadSize)
            {
                Array.Resize(ref _bytes, Math.Min(_bytes.Length * 2, capacity + ReadSize));
            }

            if (_end == _bytes.Length)
            {
                throw new InvalidOperationException("The MJPEG buffer is full.");
            }

            var read = await stream.ReadAsync(_bytes.AsMemory(_end), ct).ConfigureAwait(false);
            _end += read;
            return read > 0;
        }
    }
}
