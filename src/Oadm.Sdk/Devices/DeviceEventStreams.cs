namespace Oadm.Sdk.Devices;

/// <summary>
/// Opens the event stream of a managed device on the server (RTSP metadata,
/// <c>rtsp://&lt;device&gt;/axis-media/media.amp?video=0&amp;audio=0&amp;event=on</c>) with the credentials OADM stores for
/// it. The credentials never reach the plugin. Offered to core plugins through <c>ICorePluginContext.EventStreams</c>.
/// </summary>
public interface IDeviceEventStreams
{
    /// <summary>
    /// Connects, authenticates and starts the stream. Throws <see cref="DeviceStreamException"/> when the device refuses
    /// or does not answer, <see cref="KeyNotFoundException"/> for an unknown device.
    /// </summary>
    Task<IDeviceEventSource> OpenAsync(Guid deviceId, CancellationToken ct);
}

/// <summary>An open event stream; dispose to end it (RTSP TEARDOWN).</summary>
public interface IDeviceEventSource : IAsyncDisposable
{
    /// <summary>
    /// One complete metadata document (one <c>tt:MetadataStream</c> XML text) per item until the device closes the
    /// connection (the sequence completes) or the connection breaks (throws <see cref="DeviceStreamException"/>).
    /// </summary>
    IAsyncEnumerable<DeviceMetadataDocument> ReadAsync(CancellationToken ct);

    /// <summary>Documents dropped so far because packets were lost or a document was too large.</summary>
    int LostDocuments { get; }
}

/// <summary>A complete metadata document as the device sent it, with the server's receive time.</summary>
public sealed record DeviceMetadataDocument(string Xml, DateTimeOffset ReceivedUtc);

/// <summary>Why a device stream could not be opened or broke.</summary>
public enum DeviceStreamError
{
    /// <summary>Anything else the device answered.</summary>
    Protocol,

    /// <summary>The device did not answer or the connection broke; a later try may work.</summary>
    Unreachable,

    /// <summary>The device rejected the stored credentials.</summary>
    Unauthorized,

    /// <summary>The device offers no such stream.</summary>
    NotSupported,
}

/// <summary>A device stream failure with a message the UI can show.</summary>
public sealed class DeviceStreamException : Exception
{
    public DeviceStreamException()
    {
    }

    public DeviceStreamException(string message)
        : base(message)
    {
    }

    public DeviceStreamException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public DeviceStreamException(DeviceStreamError error, string message, Exception? inner = null)
        : base(message, inner)
    {
        Error = error;
    }

    public DeviceStreamError Error { get; } = DeviceStreamError.Protocol;
}
