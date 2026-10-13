namespace Oadm.Sdk.Vapix;

/// <summary>Authenticated VAPIX access to one device, with the stored login and the pinned certificate. OADM implements it; the password never reaches the plugin.</summary>
public interface IVapixClient
{
    /// <summary>The device's address with its scheme ("https://10.0.0.48/"). Requests use paths relative to it.</summary>
    Uri BaseAddress { get; }
    /// <summary>Reads <c>basicdeviceinfo.cgi getAllProperties</c>: serial number, product, AXIS OS version.</summary>
    Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct);
    /// <summary>
    /// Reads parameters with <c>param.cgi?action=list</c>, e.g. <c>["Network.UPnP.FriendlyName", "HTTPS"]</c> (a parameter or a
    /// whole group). Keys come back without the "root." prefix; unknown groups are left out.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct);
    /// <summary>Restarts the device (<c>restart.cgi</c>). Returns once the device accepted it, not when it is back.</summary>
    Task RestartAsync(CancellationToken ct);

    /// <summary>Fresh API discovery list (<c>apidiscovery.cgi getApiList</c>). Use with <c>Require(...)</c> before writing.</summary>
    Task<IReadOnlyList<DeviceApi>> GetApiListAsync(CancellationToken ct);

    /// <summary>Raw escape hatch for plugins: sends a request relative to the device base address.</summary>
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct);

    /// <summary>
    /// Enabled video sources (view areas, sensors, encoder channels) in camera order, never empty for a video
    /// device; the same list the live view offers (param.cgi <c>Properties.Image</c> and <c>Image</c>).
    /// Read-only. Hosts without it throw <see cref="NotSupportedException"/>.
    /// </summary>
    Task<IReadOnlyList<VideoSource>> GetVideoSourcesAsync(CancellationToken ct) =>
        throw new NotSupportedException("This client cannot list video sources.");

    /// <summary>
    /// Opens a WebSocket to the device (<paramref name="pathAndQuery"/> relative to the device, e.g.
    /// <c>/vapix/ws-data-stream?sources=events</c>): ws for HTTP devices, wss with the pinned certificate for HTTPS
    /// devices. The login is a one-time session token (<c>wssession.cgi</c>) read with the stored credentials, which never
    /// reach the plugin. The socket pings the device every <paramref name="keepAlive"/> and aborts when no pong comes back
    /// within the same time (devices send nothing on a quiet stream). The caller disposes the socket.
    /// Throws like <see cref="SendAsync"/> (unreachable, certificate changed, timeout) and
    /// <see cref="UnauthorizedAccessException"/> when the device refuses the login.
    /// Hosts without it throw <see cref="NotSupportedException"/>.
    /// </summary>
    Task<System.Net.WebSockets.WebSocket> ConnectWebSocketAsync(string pathAndQuery, TimeSpan keepAlive, CancellationToken ct) =>
        throw new NotSupportedException("This client cannot open WebSockets.");
}

/// <summary>Creates logged-in clients for managed devices (<see cref="Plugins.ICorePluginContext.Vapix"/>).</summary>
public interface IVapixClientFactory
{
    /// <summary>
    /// A client for the device with the stored login, scheme and pinned certificate. The factory caches it and replaces it
    /// when the address, the login or the certificate changes: never dispose it, and ask again for each piece of work.
    /// </summary>
    Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct);
}

/// <summary>The answer of <c>basicdeviceinfo.cgi getAllProperties</c>.</summary>
/// <param name="SerialNumber">The serial number (MAC address), upper case hex.</param>
/// <param name="ProdNbr">The product number ("P3265-V").</param>
/// <param name="ProdShortName">The short product name ("AXIS P3265-V").</param>
/// <param name="ProdFullName">The full product name ("AXIS P3265-V Dome Camera").</param>
/// <param name="Version">The AXIS OS version ("12.11.77").</param>
/// <param name="HardwareId">The hardware id.</param>
/// <param name="Architecture">The CPU architecture ("aarch64"), for packages built per architecture.</param>
/// <param name="ProdType">The product type ("Dome Camera"); OADM maps it to a <see cref="Devices.DeviceCategory"/>.</param>
/// <param name="Soc">The system on chip ("Axis Artpec-8").</param>
public sealed record BasicDeviceInfo(
    string SerialNumber,
    string ProdNbr,
    string? ProdShortName,
    string? ProdFullName,
    string Version,
    string? HardwareId,
    string? Architecture,
    string? ProdType = null,
    string? Soc = null);
