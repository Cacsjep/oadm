namespace Oadm.Core.Vapix;

/// <summary>
/// When the last request of one read was written to the device's connection (docs/spec/main-window.md "Clock"): the
/// polling's clock offset measures only the exchange whose answer carries the Date header. The handler answers a Digest
/// challenge by itself (a second request after the 401) and may open the connection first; both happen before the last
/// write, so neither is inside the measured round trip. Set for the read with <see cref="Use"/>; the connection stream of
/// <see cref="VapixClient.CreateHandler"/> (<see cref="Wrap"/>) marks every write of that read's flow.
/// </summary>
internal sealed class RequestSendClock(TimeProvider time)
{
    private static readonly AsyncLocal<RequestSendClock?> Current = new();

    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private long _lastWriteUtcTicks = long.MinValue;

    /// <summary>The time of the last write in the read's flow, null when nothing was written through a marked stream.</summary>
    public DateTimeOffset? LastWrite
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastWriteUtcTicks);
            return ticks == long.MinValue ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>Marks the writes of the current flow on this clock until the returned scope ends.</summary>
    public IDisposable Use()
    {
        var previous = Current.Value;
        Current.Value = this;
        return new Scope(previous);
    }

    /// <summary>A write of the current flow went to the device (the connection stream, or a test's handler per attempt).</summary>
    public static void MarkWrite()
    {
        if (Current.Value is { } clock)
        {
            Interlocked.Exchange(ref clock._lastWriteUtcTicks, clock._time.GetUtcNow().UtcTicks);
        }
    }

    /// <summary>The connection stream that marks its writes (<see cref="System.Net.Http.SocketsHttpHandler.PlaintextStreamFilter"/>).</summary>
    public static Stream Wrap(Stream inner) => new MarkingStream(inner);

    private sealed class Scope(RequestSendClock? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }

    /// <summary>Passes everything through; every write marks the clock of the writing flow, if any.</summary>
    private sealed class MarkingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanWrite => inner.CanWrite;

        public override bool CanSeek => false;

        public override bool CanTimeout => inner.CanTimeout;

        public override int ReadTimeout
        {
            get => inner.ReadTimeout;
            set => inner.ReadTimeout = value;
        }

        public override int WriteTimeout
        {
            get => inner.WriteTimeout;
            set => inner.WriteTimeout = value;
        }

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            MarkWrite();
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            MarkWrite();
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
            MarkWrite();
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            MarkWrite();
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
