using Google.Protobuf;

using Grpc.Core;

using Oadm.Contracts.SiteNode;

namespace Oadm.Server.Sites;

/// <summary>
/// One TCP connection to a device through a site node (a <c>Dial</c> call) as a stream: data both ways, the node's end
/// message is the end of the input, disposing ends the call (the node closes the socket). Writes are sent one at a time
/// in chunks of at most 64 KB.
/// </summary>
internal sealed class DialStream : Stream
{
    private const int ChunkSize = 64 * 1024;

    private readonly AsyncDuplexStreamingCall<DialMessage, DialMessage> _call;
    private readonly CancellationTokenSource _life;
    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly Action? _closed;
    private ReadOnlyMemory<byte> _pending;
    private bool _ended;
    private int _disposed;

    public DialStream(AsyncDuplexStreamingCall<DialMessage, DialMessage> call, CancellationTokenSource life, Action? closed)
    {
        _call = call;
        _life = life;
        _closed = closed;
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

            bool next;
            try
            {
                next = await _call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false);
            }
            catch (RpcException e) when (e.StatusCode is StatusCode.Cancelled or StatusCode.Unavailable or StatusCode.Internal or StatusCode.Unknown)
            {
                throw new IOException("The connection through the site ended: " + e.Status.Detail, e);
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
        await _writing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var offset = 0; offset < buffer.Length; offset += ChunkSize)
            {
                var chunk = buffer.Slice(offset, Math.Min(ChunkSize, buffer.Length - offset));
                await _call.RequestStream.WriteAsync(new DialMessage { Data = ByteString.CopyFrom(chunk.Span) }, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (RpcException e)
        {
            throw new IOException("The connection through the site ended: " + e.Status.Detail, e);
        }
        catch (InvalidOperationException e)
        {
            throw new IOException("The connection through the site ended.", e);
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
            _writing.Dispose();
            _closed?.Invoke();
        }

        base.Dispose(disposing);
    }
}
