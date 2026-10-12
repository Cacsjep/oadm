using Grpc.Core;

using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// The data of a PluginService.Download call as a read-only stream (the header message is read before). Disposing it
/// disposes the call, which cancels the server's copy when the stream was not read to the end.
/// </summary>
internal sealed class DownloadChunkStream(AsyncServerStreamingCall<DownloadChunk> call) : Stream
{
    private ReadOnlyMemory<byte> _pending;
    private bool _ended;
    private long _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_pending.IsEmpty)
        {
            if (_ended || buffer.IsEmpty)
            {
                return 0;
            }

            if (!await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
            {
                _ended = true;
                return 0;
            }

            _pending = call.ResponseStream.Current.Data.Memory;
        }

        var count = Math.Min(buffer.Length, _pending.Length);
        _pending[..count].CopyTo(buffer);
        _pending = _pending[count..];
        _position += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

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
            call.Dispose();
        }

        base.Dispose(disposing);
    }
}
