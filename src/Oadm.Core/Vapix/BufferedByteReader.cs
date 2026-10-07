namespace Oadm.Core.Vapix;

/// <summary>
/// Small buffered reader over a network stream for line based protocols with binary bodies
/// (multipart MJPEG, RTSP with interleaved RTP). Handles partial reads; every limit is explicit so
/// a misbehaving peer cannot make it buffer without bound.
/// </summary>
internal sealed class BufferedByteReader
{
    private readonly Stream _stream;
    private readonly int _maxBuffer;
    private byte[] _buffer;
    private int _start;
    private int _end;
    private bool _eof;

    public BufferedByteReader(Stream stream, int initialSize = 64 * 1024, int maxBuffer = 16 * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _maxBuffer = maxBuffer;
        _buffer = new byte[Math.Min(initialSize, maxBuffer)];
    }

    /// <summary>Bytes buffered but not consumed yet.</summary>
    public ReadOnlySpan<byte> Buffered => _buffer.AsSpan(_start, _end - _start);

    public int BufferedCount => _end - _start;

    /// <summary>True once the stream has ended and every buffered byte was consumed.</summary>
    public bool IsAtEnd => _eof && _start == _end;

    /// <summary>Reads more data. Returns false at end of stream.</summary>
    public async ValueTask<bool> FillAsync(CancellationToken ct)
    {
        if (_eof)
        {
            return false;
        }

        if (_start > 0 && (_end == _buffer.Length || _start == _end))
        {
            Compact();
        }

        if (_end == _buffer.Length)
        {
            if (_buffer.Length >= _maxBuffer)
            {
                throw new InvalidDataException($"Stream element exceeds {_maxBuffer} bytes.");
            }

            Array.Resize(ref _buffer, Math.Min(_buffer.Length * 2, _maxBuffer));
        }

        var read = await _stream.ReadAsync(_buffer.AsMemory(_end), ct).ConfigureAwait(false);
        if (read == 0)
        {
            _eof = true;
            return false;
        }

        _end += read;
        return true;
    }

    /// <summary>Ensures at least <paramref name="count"/> bytes are buffered. False at end of stream.</summary>
    public async ValueTask<bool> EnsureAsync(int count, CancellationToken ct)
    {
        if (count > _maxBuffer)
        {
            throw new InvalidDataException($"Stream element of {count} bytes exceeds {_maxBuffer} bytes.");
        }

        while (BufferedCount < count)
        {
            if (_start + count > _buffer.Length)
            {
                Compact();
                if (count > _buffer.Length)
                {
                    Array.Resize(ref _buffer, Math.Min(Math.Max(count, _buffer.Length * 2), _maxBuffer));
                }
            }

            if (!await FillAsync(ct).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Reads one line without the trailing CR/LF, or null at end of stream.</summary>
    public async ValueTask<string?> ReadLineAsync(int maxLength, CancellationToken ct)
    {
        var searchFrom = 0;
        while (true)
        {
            var index = Buffered[searchFrom..].IndexOf((byte)'\n');
            if (index >= 0)
            {
                var length = searchFrom + index;
                var line = Buffered[..length];
                if (line.Length > 0 && line[^1] == '\r')
                {
                    line = line[..^1];
                }

                var text = System.Text.Encoding.Latin1.GetString(line);
                _start += length + 1;
                return text;
            }

            searchFrom = BufferedCount;
            if (searchFrom > maxLength)
            {
                throw new InvalidDataException($"Line longer than {maxLength} bytes.");
            }

            if (!await FillAsync(ct).ConfigureAwait(false))
            {
                if (BufferedCount == 0)
                {
                    return null;
                }

                var rest = System.Text.Encoding.Latin1.GetString(Buffered);
                _start = _end;
                return rest;
            }
        }
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes into a new array, or null at end of stream.</summary>
    public async ValueTask<byte[]?> ReadExactAsync(int count, CancellationToken ct)
    {
        if (!await EnsureAsync(count, ct).ConfigureAwait(false))
        {
            return null;
        }

        var result = Buffered[..count].ToArray();
        _start += count;
        return result;
    }

    /// <summary>
    /// Reads up to (not including) the next occurrence of <paramref name="delimiter"/>, consuming the
    /// delimiter too. Returns null at end of stream before the delimiter was seen.
    /// </summary>
    public async ValueTask<byte[]?> ReadUntilAsync(ReadOnlyMemory<byte> delimiter, CancellationToken ct)
    {
        var searchFrom = 0;
        while (true)
        {
            var index = Buffered[searchFrom..].IndexOf(delimiter.Span);
            if (index >= 0)
            {
                var length = searchFrom + index;
                var result = Buffered[..length].ToArray();
                _start += length + delimiter.Length;
                return result;
            }

            searchFrom = Math.Max(0, BufferedCount - delimiter.Length + 1);
            if (!await FillAsync(ct).ConfigureAwait(false))
            {
                return null;
            }
        }
    }

    /// <summary>Peeks the next byte, or -1 at end of stream.</summary>
    public async ValueTask<int> PeekByteAsync(CancellationToken ct)
    {
        if (!await EnsureAsync(1, ct).ConfigureAwait(false))
        {
            return -1;
        }

        return _buffer[_start];
    }

    public void Skip(int count)
    {
        if (count < 0 || count > BufferedCount)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        _start += count;
    }

    private void Compact()
    {
        var count = _end - _start;
        if (count > 0 && _start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, count);
        }

        _start = 0;
        _end = count;
    }
}
