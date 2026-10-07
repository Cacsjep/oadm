using System.Net;

namespace Oadm.Plugins.Firmware;

/// <summary>
/// HTTP content that streams an uploaded file in 80 KB chunks without buffering it, reports the
/// bytes sent, and reopens the file each time it is serialized (an authentication retry sends the
/// body again). The length is known up front, so the request carries a Content-Length.
/// </summary>
public sealed class FirmwareStreamContent : HttpContent
{
    public const int ChunkSize = 81920;

    private readonly Func<CancellationToken, Task<Stream>> _open;
    private readonly long _length;
    private readonly Action<long>? _bytesSent;

    public FirmwareStreamContent(Func<CancellationToken, Task<Stream>> open, long length, Action<long>? bytesSent = null)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _open = open;
        _length = length;
        _bytesSent = bytesSent;
    }

    /// <summary>Total bytes written by the last serialization; equals the length once the body was sent completely.</summary>
    public long BytesSent { get; private set; }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        BytesSent = 0;
        var source = await _open(cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            var buffer = new byte[ChunkSize];
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (BytesSent + read > _length)
                {
                    throw new IOException("The firmware file is larger than announced; it changed during the upload.");
                }

                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                BytesSent += read;
                _bytesSent?.Invoke(BytesSent);
            }
        }

        if (BytesSent != _length)
        {
            throw new IOException("The firmware file is shorter than announced; it changed during the upload.");
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _length;
        return true;
    }
}
