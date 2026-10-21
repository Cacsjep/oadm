namespace Oadm.Sdk.Client;

/// <summary>
/// Opens the web interface of a managed device for an embedded browser. The host runs a relay per session on its own
/// loopback address (127.1.x.y; macOS 127.0.0.1), so the devices' login cookies never mix between tabs. The server sends
/// every request to the device with the stored login and the pinned certificate, so the page logs in by itself and
/// passwords never reach the client. The server writes "Opened web interface" to the audit log.
/// </summary>
public interface IDeviceWebAccess
{
    /// <summary>A new relay for the device.</summary>
    /// <exception cref="DeviceWebRefusedException">The device cannot be opened now (its status); the message says why.</exception>
    Task<IDeviceWebSession> OpenAsync(Guid deviceId, CancellationToken ct);
}

/// <summary>One relay for one device; dispose it when its browser tab closes or shows another device.</summary>
public interface IDeviceWebSession : IDisposable
{
    /// <summary>The device this session shows.</summary>
    Guid DeviceId { get; }

    /// <summary>The relay's origin, "http://127.1.0.5:51234/". Requests without the session cookie are refused.</summary>
    Uri Origin { get; }

    /// <summary>
    /// The first address to open in the browser for <paramref name="path"/> ("/", "/local/objectanalytics/index.html#/"):
    /// it hands the browser the session cookie once, then redirects to the path. Later navigations use <see cref="Origin"/>.
    /// </summary>
    Uri StartAddress(string path);
}

/// <summary>Pages of the relay itself.</summary>
public static class DeviceWebPages
{
    /// <summary>
    /// The title of the page the relay answers a page request without its session cookie with (the browser lost its
    /// cookies, e.g. after the native browser was created again). The browser then opens
    /// <see cref="IDeviceWebSession.StartAddress"/> with the same path, which hands out the cookie again.
    /// </summary>
    public const string SessionLostTitle = "OADM: session lost";
}

/// <summary>The device cannot be opened now; the message is the shared device status text.</summary>
public sealed class DeviceWebRefusedException : Exception
{
    /// <summary>OADM does not open this device.</summary>
    public DeviceWebRefusedException()
    {
    }

    /// <summary>OADM does not open this device; the message says why.</summary>
    public DeviceWebRefusedException(string message)
        : base(message)
    {
    }

    /// <summary>OADM does not open this device; the message says why.</summary>
    public DeviceWebRefusedException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
