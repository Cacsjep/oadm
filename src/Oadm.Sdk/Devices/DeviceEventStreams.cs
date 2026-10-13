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

    /// <summary>
    /// The RTSP port stored for a device (a port forward to port 554, e.g. a camera behind NAT), null = 554. Every
    /// <see cref="OpenAsync"/> uses it. Hosts without the setting return null.
    /// </summary>
    Task<int?> GetRtspPortAsync(Guid deviceId, CancellationToken ct) => Task.FromResult<int?>(null);

    /// <summary>
    /// Stores the RTSP port of a device (1..65535; null or 554 = the default). Throws <see cref="ArgumentOutOfRangeException"/>
    /// for another value, <see cref="NotSupportedException"/> on hosts without the setting.
    /// </summary>
    Task SetRtspPortAsync(Guid deviceId, int? port, CancellationToken ct) =>
        throw new NotSupportedException("This server cannot store RTSP ports.");

    /// <summary>The stored RTSP ports of many devices in one read; devices on the default port are left out.</summary>
    async Task<IReadOnlyDictionary<Guid, int>> GetRtspPortsAsync(IReadOnlyCollection<Guid> deviceIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        var ports = new Dictionary<Guid, int>();
        foreach (var id in deviceIds)
        {
            if (await GetRtspPortAsync(id, ct).ConfigureAwait(false) is { } port)
            {
                ports[id] = port;
            }
        }

        return ports;
    }

    /// <summary>
    /// Stores one RTSP port for many devices in one write. With <paramref name="managed"/> the ports of devices that are
    /// no longer managed are dropped in the same write.
    /// </summary>
    async Task SetRtspPortsAsync(IReadOnlyCollection<Guid> deviceIds, int? port, IReadOnlyCollection<Guid>? managed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        foreach (var id in deviceIds)
        {
            await SetRtspPortAsync(id, port, ct).ConfigureAwait(false);
        }
    }
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
/// <param name="Xml">One <c>tt:MetadataStream</c> XML document; parse it with <see cref="Vapix.DeviceXml.Parse(string, System.Xml.Linq.LoadOptions)"/>.</param>
/// <param name="ReceivedUtc">When the server received it.</param>
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
    /// <summary>An error of a device stream (<see cref="DeviceStreamError.Protocol"/>).</summary>
    public DeviceStreamException()
    {
    }

    /// <summary>An error of a device stream with its message (<see cref="DeviceStreamError.Protocol"/>).</summary>
    public DeviceStreamException(string message)
        : base(message)
    {
    }

    /// <summary>An error of a device stream with its message and cause (<see cref="DeviceStreamError.Protocol"/>).</summary>
    public DeviceStreamException(string message, Exception inner)
        : base(message, inner)
    {
    }

    /// <summary>An error of a device stream of the given kind; the message is the text for the user.</summary>
    public DeviceStreamException(DeviceStreamError error, string message, Exception? inner = null)
        : base(message, inner)
    {
        Error = error;
    }

    /// <summary>What went wrong: whether a retry can help (Unreachable) or not (Unauthorized, NotSupported).</summary>
    public DeviceStreamError Error { get; } = DeviceStreamError.Protocol;
}
