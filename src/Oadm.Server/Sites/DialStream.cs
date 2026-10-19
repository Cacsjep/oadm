using Google.Protobuf;

using Grpc.Core;

using Oadm.Contracts.SiteNode;
using Oadm.Core.Sites;

namespace Oadm.Server.Sites;

/// <summary>
/// One TCP connection to a device through a site node (a <c>Dial</c> call) as a stream: data both ways, the node's end
/// message is the end of the input, disposing ends the call (the node closes the socket). Writes are sent one at a time
/// in chunks of at most 64 KB.
/// <para>
/// The node ends the call normally when the device closes or resets its socket, so a call that ends with an error, or is
/// cancelled because the node's link or session ended, is the site's problem: an <see cref="IOException"/> (what readers
/// of a stream expect) whose inner exception is the <see cref="SiteException"/> "Site offline: ...". The status rules
/// find it there (<see cref="SiteErrors.Find"/>) and never count it as a device error. A cancellation by the caller's
/// token is an <see cref="OperationCanceledException"/>.
/// </para>
/// </summary>
internal sealed class DialStream : Stream
{
    private const int ChunkSize = 64 * 1024;

    private readonly AsyncDuplexStreamingCall<DialMessage, DialMessage> _call;
    private readonly CancellationTokenSource _life;
    private readonly SemaphoreSlim _writing = new(1, 1); // never disposed: a write may still release it after Dispose
    private readonly Action? _closed;
    private readonly Func<SiteException> _lost;
    private ReadOnlyMemory<byte> _pending;
    private bool _ended;
    private int _disposed;

    /// <param name="lost">The site error of a link that ended (the session's "Site offline: ...").</param>
    public DialStream(AsyncDuplexStreamingCall<DialMessage, DialMessage> call, CancellationTokenSource life, Action? closed, Func<SiteException> lost)
    {
        _call = call;
        _life = life;
        _closed = closed;
        _lost = lost;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_pending.IsEmpty)
        {
            if (_ended)
            {
                return 0;
            }

            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            bool next;
            try
            {
                next = await _call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is RpcException or OperationCanceledException or InvalidOperationException or ObjectDisposedException)
            {
                throw Ended(e, cancellationToken);
            }

            if (!next)
            {
                _ended = true;
                return 0;
            }

            var message = _call.ResponseStream.Current;
            switch (message.KindCase)
            {
                case DialMessage.KindOneofCase.Data:
                    _pending = message.Data.Memory;
                    break;
                case DialMessage.KindOneofCase.End:
                    _ended = true;
                    return 0;
            }
        }

        var count = Math.Min(buffer.Length, _pending.Length);
        _pending[..count].CopyTo(buffer);
        _pending = _pending[count..];
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
        await _writing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);
            for (var offset = 0; offset < buffer.Length; offset += ChunkSize)
            {
                var chunk = buffer.Slice(offset, Math.Min(ChunkSize, buffer.Length - offset));
                await _call.RequestStream.WriteAsync(new DialMessage { Data = ByteString.CopyFrom(chunk.Span) }, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is RpcException or OperationCanceledException or InvalidOperationException
            || (e is ObjectDisposedException && Volatile.Read(ref _disposed) == 0))
        {
            throw Ended(e, cancellationToken);
        }
        finally
        {
            _writing.Release();
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>What a failed read or write throws: the caller's cancellation, a use after Dispose, or the lost site link.</summary>
    private Exception Ended(Exception error, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new OperationCanceledException("The connection through the site was cancelled.", error, cancellationToken);
        }

        if (Volatile.Read(ref _disposed) == 1)
        {
            return new ObjectDisposedException(nameof(DialStream), error);
        }

        var site = _lost();
        return new IOException(site.Message, site);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                _life.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _call.Dispose();
            _life.Dispose();
            _closed?.Invoke();
        }

        base.Dispose(disposing);
    }
}
