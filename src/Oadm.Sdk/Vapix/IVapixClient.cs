namespace Oadm.Sdk.Vapix;

/// <summary>Authenticated VAPIX access to one device. Implemented in Oadm.Core.</summary>
public interface IVapixClient
{
    Uri BaseAddress { get; }
    Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct);
    Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct);
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
}

public interface IVapixClientFactory
{
    Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct);
}

public sealed record BasicDeviceInfo(
    string SerialNumber,
    string ProdNbr,
    string? ProdShortName,
    string? ProdFullName,
    string Version,
    string? HardwareId,
    string? Architecture,
    string? ProdType = null);
