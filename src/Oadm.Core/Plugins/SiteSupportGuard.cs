using Oadm.Sdk.Devices;
using Oadm.Sdk.Sites;
using Oadm.Sdk.Vapix;

namespace Oadm.Core.Plugins;

/// <summary>
/// The host's side of <see cref="SiteSupport.None"/> (docs/sites.md, Plugins and SDK: Declaration): a core plugin of such a
/// package gets a VAPIX factory, event streams and a network that refuse devices at a site before any request
/// (<see cref="DeviceNotCompatibleException"/> "Not available for devices at a site"), and no relays into sites, so its
/// pages and actions never reach them whatever the plugin does. The checks cost no extra database read: the server's own
/// factory and event streams decide from the device row they read anyway (the route of the client), other
/// implementations (tests) from the repository.
/// </summary>
public static class SiteSupportGuard
{
    /// <summary>The factory a plugin of a package with <paramref name="support"/> gets.</summary>
    public static IVapixClientFactory Guard(IVapixClientFactory inner, IDeviceRepository devices, SiteSupport support) =>
        support == SiteSupport.Full ? inner : new GuardedVapixFactory(inner, devices);

    /// <summary>The event streams a plugin of a package with <paramref name="support"/> gets.</summary>
    public static IDeviceEventStreams? Guard(IDeviceEventStreams? inner, IDeviceRepository devices, SiteSupport support) =>
        inner is null || support == SiteSupport.Full ? inner : new GuardedEventStreams(inner, devices);

    /// <summary>
    /// The network a plugin of a package with <paramref name="support"/> gets: listeners on the server's own network
    /// only; the network of a device or site at a site is refused.
    /// </summary>
    public static IPluginNetwork? Guard(IPluginNetwork? inner, SiteSupport support) =>
        inner is null || support == SiteSupport.Full ? inner : new LocalOnlyNetwork(inner);

    /// <summary>Throws <see cref="DeviceNotCompatibleException"/> for a device at a site.</summary>
    public static async Task RefuseSiteDeviceAsync(IDeviceRepository devices, Guid deviceId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if (await devices.FindAsync(deviceId, ct).ConfigureAwait(false) is { SiteId: not null })
        {
            throw Refusal();
        }
    }

    private static DeviceNotCompatibleException Refusal() => new(SiteTexts.NotAvailableAtSite);

    private sealed class GuardedVapixFactory(IVapixClientFactory inner, IDeviceRepository devices) : IVapixClientFactory
    {
        public async Task<IVapixClient> CreateAsync(Guid deviceId, CancellationToken ct)
        {
            if (inner is Vapix.VapixClientFactory)
            {
                // The server's factory reads the device once and builds (or reuses) its client on the device's route;
                // creating a client sends nothing, so the route decides before any request.
                var client = await inner.CreateAsync(deviceId, ct).ConfigureAwait(false);
                return client is Vapix.VapixClient { Route.IsLocal: false } ? throw Refusal() : client;
            }

            await RefuseSiteDeviceAsync(devices, deviceId, ct).ConfigureAwait(false);
            return await inner.CreateAsync(deviceId, ct).ConfigureAwait(false);
        }
    }

    private sealed class GuardedEventStreams(IDeviceEventStreams inner, IDeviceRepository devices) : IDeviceEventStreams
    {
        public async Task<IDeviceEventSource> OpenAsync(Guid deviceId, CancellationToken ct)
        {
            if (inner is LiveView.DeviceEventStreams server)
            {
                return await server.OpenAsync(deviceId, refuseSiteDevices: true, ct).ConfigureAwait(false);
            }

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

    private sealed class LocalOnlyNetwork(IPluginNetwork inner) : IPluginNetwork
    {
        public IDeviceNetwork ForDevice(IDeviceInfo device)
        {
            ArgumentNullException.ThrowIfNull(device);
            return device.SiteId is not null ? throw Refusal() : inner.ForDevice(device);
        }

        public IDeviceNetwork ForSite(Guid? siteId) => siteId is not null ? throw Refusal() : inner.ForSite(null);

        public Task<IUdpRelay> ListenUdpAsync(int port, string reason, CancellationToken ct) => inner.ListenUdpAsync(port, reason, ct);

        public Task<ITcpRelay> ListenTcpAsync(int port, string reason, CancellationToken ct) => inner.ListenTcpAsync(port, reason, ct);
    }
}
