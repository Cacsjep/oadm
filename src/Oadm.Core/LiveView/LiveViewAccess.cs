using Oadm.Core.DeviceGroups;
using Oadm.Core.Devices;
using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;

namespace Oadm.Core.LiveView;

/// <summary>
/// The live view of the gRPC LiveViewService, only on <see cref="ViewedDevices"/> (docs/spec/device-scope.md, Tests:
/// Capability type): video sources and streams (<see cref="LiveViewHub"/>), zoom and focus (<see cref="IDeviceOptics"/>), PTZ
/// (<see cref="IDevicePtz"/>) and the RTSP port OADM stores per device. Calls that name one device take a
/// <see cref="ViewedDevices"/> of exactly that device.
/// </summary>
public sealed class LiveViewAccess(
    LiveViewHub hub,
    ILiveVideoSourceFactory sources,
    IDeviceOptics optics,
    IDevicePtz ptz,
    IDeviceEventStreams ports,
    DeviceRepository devices)
{
    /// <summary>The video sources (view areas, sensors, channels) of the device. Read-only on the camera.</summary>
    public Task<LiveViewCapabilities> GetCapabilitiesAsync(ViewedDevices one, CancellationToken ct) =>
        sources.GetCapabilitiesAsync(DeviceActions.Single(one).Id, ct);

    /// <inheritdoc cref="LiveViewHub.SubscribeAsync"/>
    public Task<LiveViewSubscription> SubscribeAsync(ViewedDevices one, int maxWidth, int maxHeight, int fps, IReadOnlyCollection<VideoCodecKind> clientCodecs, int camera, CancellationToken ct) =>
        hub.SubscribeAsync(DeviceActions.Single(one).Id, maxWidth, maxHeight, fps, clientCodecs, ct, camera);

    /// <inheritdoc cref="IDeviceOptics.GetAsync"/>
    public Task<OpticsState> GetOpticsAsync(ViewedDevices one, int sensor, CancellationToken ct) =>
        optics.GetAsync(DeviceActions.Single(one).Id, sensor, ct);

    /// <inheritdoc cref="IDeviceOptics.SetAsync"/>
    public Task<OpticsState> SetOpticsAsync(ViewedDevices one, int sensor, OpticsChange change, CancellationToken ct) =>
        optics.SetAsync(DeviceActions.Single(one).Id, sensor, change, ct);

    /// <inheritdoc cref="IDevicePtz.GetAsync"/>
    public Task<PtzState> GetPtzAsync(ViewedDevices one, int camera, CancellationToken ct) =>
        ptz.GetAsync(DeviceActions.Single(one).Id, camera, ct);

    /// <inheritdoc cref="IDevicePtz.SendAsync"/>
    public Task SendPtzAsync(ViewedDevices one, int camera, PtzCommand command, CancellationToken ct) =>
        ptz.SendAsync(DeviceActions.Single(one).Id, camera, command, ct);

    /// <summary>The stored RTSP ports of the devices; devices on the default port are left out.</summary>
    public Task<IReadOnlyDictionary<Guid, int>> GetRtspPortsAsync(ViewedDevices viewed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(viewed);
        return ports.GetRtspPortsAsync(viewed.Ids, ct);
    }

    /// <summary>
    /// Stores one RTSP port for the devices in one write (null = the default) and gives their later viewers a new camera
    /// connection. The ports of devices that are no longer managed go in the same write.
    /// </summary>
    public async Task SetRtspPortsAsync(ViewedDevices viewed, int? port, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(viewed);
        var managed = await devices.ListDeviceIdsAsync(ct).ConfigureAwait(false);
        await ports.SetRtspPortsAsync(viewed.Ids, port, managed, ct).ConfigureAwait(false);
        foreach (var id in viewed.Ids)
        {
            hub.Invalidate(id);
        }
    }
}
