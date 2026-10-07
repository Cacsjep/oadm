using System.Net;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Devices;
using Oadm.Core.LiveView.Rtsp;
using Oadm.Core.Security;
using Oadm.Core.Vapix;

namespace Oadm.Core.LiveView;

/// <summary>Opens live video streams of managed devices. Tests replace it with fakes.</summary>
public interface ILiveVideoSourceFactory
{
    /// <summary>Codecs and resolutions the device offers.</summary>
    /// <exception cref="KeyNotFoundException">The device does not exist.</exception>
    Task<LiveViewCapabilities> GetCapabilitiesAsync(Guid deviceId, CancellationToken ct);

    /// <summary>Opens the stream; throws (usually <see cref="LiveViewException"/>) when the camera refuses.</summary>
    Task<ILiveVideoSource> OpenAsync(LiveStreamKey key, CancellationToken ct);
}

/// <summary>
/// Real factory: capabilities via param.cgi (Properties.Image) over the device's authenticated
/// VAPIX client, video via RTSP on port 554 with the stored credentials. Credentials never
/// leave the server.
/// </summary>
public sealed class AxisLiveVideoSourceFactory(
    DeviceRepository devices,
    CredentialStore credentials,
    VapixClientFactory clients,
    ILoggerFactory? loggerFactory = null) : ILiveVideoSourceFactory
{
    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;

    public async Task<LiveViewCapabilities> GetCapabilitiesAsync(Guid deviceId, CancellationToken ct)
    {
        var client = await clients.GetClientAsync(deviceId, ct).ConfigureAwait(false);
        var image = await client.GetImageCapabilitiesAsync(ct).ConfigureAwait(false);
        return LiveViewCapabilities.FromImageCapabilities(image);
    }

    public async Task<ILiveVideoSource> OpenAsync(LiveStreamKey key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        var device = await devices.GetAsync(key.DeviceId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Device {key.DeviceId} not found.");
        var stored = await credentials.GetAsync(device.Id, ct).ConfigureAwait(false);
        return await RtspVideoSource.OpenAsync(
            new RtspSourceOptions
            {
                Address = device.Address,
                Credentials = stored is null ? null : new NetworkCredential(stored.UserName, stored.Password),
                Codec = key.Codec,
                Size = key.Size,
                Fps = key.Fps,
                Camera = key.Camera,
            },
            ct,
            _loggerFactory.CreateLogger<RtspVideoSource>()).ConfigureAwait(false);
    }
}
