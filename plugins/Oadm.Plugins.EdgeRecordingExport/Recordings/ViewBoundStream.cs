using Oadm.Sdk.Devices;

namespace Oadm.Plugins.EdgeRecordingExport.Recordings;

/// <summary>
/// The export of a camera for a user with a device group: while the client downloads, the stream asks every
/// <paramref name="interval"/> whether the camera is still in the group (<paramref name="stillInView"/>). Once it left (a tag
/// or site change, the group's rules, a removed camera), the next read throws <see cref="DevicesNotAvailableException"/> and
/// the download ends; nothing more of the recording reaches the client.
/// </summary>
internal sealed class ViewBoundStream(Stream inner, Func<CancellationToken, Task<bool>> stillInView, TimeSpan interval, TimeProvider time) : Stream
{
    private long _checked = time.GetTimestamp();
    private bool _left;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await CheckAsync(cancellationToken).ConfigureAwait(false);
        return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        if (!_left && time.GetElapsedTime(_checked) >= interval)
        {
            _left = !await stillInView(ct).ConfigureAwait(false);
            _checked = time.GetTimestamp();
        }

        if (_left)
        {
            throw new DevicesNotAvailableException();
        }
    }
}
