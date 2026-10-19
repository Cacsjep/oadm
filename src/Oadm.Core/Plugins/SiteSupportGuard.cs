using Oadm.Sdk.Devices;
using Oadm.Sdk.Sites;
using Oadm.Sdk.Vapix;

namespace Oadm.Core.Plugins;

/// <summary>
/// The host's side of <see cref="SiteSupport.None"/> (docs/sites.md, Plugins and SDK: Declaration): a core plugin of such a
/// package gets a VAPIX factory and event streams that refuse devices at a site before any request
/// (<see cref="DeviceNotCompatibleException"/> "Not available for devices at a site"), so its pages and actions never reach
/// them whatever the plugin does.
/// </summary>
public static class SiteSupportGuard
{
    /// <summary>The factory a plugin of a package with <paramref name="support"/> gets.</summary>
    public static IVapixClientFactory Guard(IVapixClientFactory inner, IDeviceRepository devices, SiteSupport support) =>
        support == SiteSupport.Full ? inner : new GuardedVapixFactory(inner, devices);

    /// <summary>The event streams a plugin of a package with <paramref name="support"/> gets.</summary>
    public static IDeviceEventStreams? Guard(IDeviceEventStreams? inner, IDeviceRepository devices, SiteSupport support) =>
        inner is null || support == SiteSupport.Full ? inner : new GuardedEventStreams(inner, devices);

    /// <summary>Throws <see cref="DeviceNotCompatibleException"/> for a device at a site.</summary>
    public static async Task RefuseSiteDeviceAsync(IDeviceRepository devices, Guid deviceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (await devices.FindAsync(deviceId, ct).ConfigureAwait(false) is { SiteId: not null })
        {
            throw new DeviceNotCompatibleException(SiteTexts.NotAvailableAtSite);
        }
    }

    private sealed class GuardedVapixFactory(IVapixClientFactory inner, IDeviceRepository devices) : IVapixClientFactory
    {
        public async Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct)
        {
            await RefuseSiteDeviceAsync(devices, deviceId, ct).ConfigureAwait(false);
            return await inner.CreateAsync(deviceId, ct).ConfigureAwait(false);
        }
    }

    private sealed class GuardedEventStreams(IDeviceEventStreams inner, IDeviceRepository devices) : IDeviceEventStreams
    {
        public async Task<IDeviceEventSource> OpenAsync(Guid deviceId, CancellationToken ct)
        {
            await RefuseSiteDeviceAsync(devices, deviceId, ct).ConfigureAwait(false);
            return await inner.OpenAsync(deviceId, ct).ConfigureAwait(false);
        }

        public Task<int?> GetRtspPortAsync(Guid deviceId, CancellationToken ct) => inner.GetRtspPortAsync(deviceId, ct);

        public Task SetRtspPortAsync(Guid deviceId, int? port, CancellationToken ct) => inner.SetRtspPortAsync(deviceId, port, ct);

        public Task<IReadOnlyDictionary<Guid, int>> GetRtspPortsAsync(IReadOnlyCollection<Guid> deviceIds, CancellationToken ct) =>
            inner.GetRtspPortsAsync(deviceIds, ct);

        public Task SetRtspPortsAsync(IReadOnlyCollection<Guid> deviceIds, int? port, IReadOnlyCollection<Guid>? managed, CancellationToken ct) =>
            inner.SetRtspPortsAsync(deviceIds, port, managed, ct);
    }
}
