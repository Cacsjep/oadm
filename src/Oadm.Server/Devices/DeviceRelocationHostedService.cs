using System.Net;

using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Core.Sites;
using Oadm.Core.Tasks;

using CoreDiscoveryService = Oadm.Core.Discovery.DiscoveryService;

namespace Oadm.Server.Devices;

/// <summary>Timing of the periodic re-find of moved devices.</summary>
public sealed class DeviceRelocationOptions
{
    /// <summary>First run after server start. Default 30 s (the first status poll has run by then).</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Pause between runs. Default 5 minutes.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long one run listens to mDNS. Default 15 s.</summary>
    public TimeSpan BrowseDuration { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Once every device looked for was seen, the browse listens this much longer for the other routes it answers through.
    /// Default 2 s (all routes browse at the same time).
    /// </summary>
    public TimeSpan FoundGrace { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The devices of a site offline for longer than this are looked for elsewhere too. Default 1 hour.</summary>
    public TimeSpan SiteOfflineMove { get; init; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Periodic mDNS re-find: when a managed device does not answer on its route (status Unreachable, or Site offline for
/// longer than <see cref="DeviceRelocationOptions.SiteOfflineMove"/>; address is an IP literal), a short zero-conf browse
/// on the server's network and through every online site looks for its serial number; when the device announces another
/// address or answers through another route, <see cref="DeviceAddressService.TryRelocateAsync(string, string, DeviceRoute, string, bool, CancellationToken)"/>
/// verifies it there and moves the record: route and address together (logged, published as a device change, audited
/// "Device moved" by "system"). Every (route, address) a serial was announced at is kept; the routes are tried one after
/// another until one verifies, a site whose networks include the address first. A device that answers on its current
/// route never moves; devices addressed by name never move; a device OADM reaches over HTTP (no pinned certificate) moves
/// only within its own route (<see cref="DeviceAddressChangeResult.NotPinned"/>). Nothing is browsed while no device is a candidate.
/// </summary>
public sealed partial class DeviceRelocationHostedService(
    CoreDiscoveryService discovery,
    DeviceRepository devices,
    DeviceAddressService addresses,
    DeviceRelocationOptions options,
    ILogger<DeviceRelocationHostedService> logger,
    ISiteStates? sites = null,
    AuditLog? audit = null,
    TimeProvider? time = null,
    ISiteNetwork? network = null,
    Core.DeviceGroups.DeviceViewFactory? views = null) : BackgroundService
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<Guid, DateTimeOffset> _offlineSince = [];

    /// <summary>One run: browse for the devices that do not answer and move the ones found elsewhere. Returns how many moved.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var all = await devices.ListDevicesAsync(ct).ConfigureAwait(false);
        var longOffline = SitesOfflineLong(all);
        var candidates = all
            // Computed status: a device of an offline site shows Site offline; it is looked for once its site is offline for long.
            .Where(d => !DeviceAddressService.UsesHostName(d)
                && (d.Status == Sdk.Devices.DeviceStatus.Unreachable
                    || (d.Status == Sdk.Devices.DeviceStatus.SiteOffline && d.SiteId is { } site && longOffline.Contains(site))))
            .ToDictionary(d => d.Serial, StringComparer.OrdinalIgnoreCase);
        if (candidates.Count == 0)
        {
            return 0;
        }

        var seen = await BrowseAsync(candidates.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase), ct).ConfigureAwait(false);
        var moved = 0;
        foreach (var (serial, places) in seen)
        {
            var before = candidates[serial];
            foreach (var (route, address) in Order(before, places))
            {
                var result = await addresses.TryRelocateAsync(serial, address, route, "found again by mDNS", requireUnreachable: true, ct).ConfigureAwait(false);
                if (result == DeviceAddressChangeResult.Updated)
                {
                    moved++;
                    LogRelocated(serial, address);
                    if (audit is not null)
                    {
                        await audit.WriteAsync("system", string.Empty, AuditActions.DeviceMoved, serial,
                            "Found again: " + DeviceMoveText.Of(DeviceConnections.RouteOf(before), before.Address, route, address, sites), ct).ConfigureAwait(false);
                        await AddDevices.MembershipAudit.MovedAsync(views, audit, before, ct, logger).ConfigureAwait(false);
                    }

                    break;
                }

                if (result is not (DeviceAddressChangeResult.NotVerified or DeviceAddressChangeResult.Unchanged or DeviceAddressChangeResult.NotPinned))
                {
                    break; // not managed any more, answering again, addressed by name: no other route changes that
                }
            }
        }

        return moved;
    }

    /// <summary>
    /// The places a device was announced at, in the order to try: never its current route and address; a site whose
    /// networks include the address first, then the server's own network, then the other sites.
    /// </summary>
    internal IEnumerable<(DeviceRoute Route, string Address)> Order(Device device, IReadOnlyList<(DeviceRoute Route, string Address)> places)
    {
        var current = DeviceConnections.RouteOf(device);
        return places
            .Distinct()
            .Where(p => !(p.Route == current && string.Equals(p.Address, device.Address, StringComparison.OrdinalIgnoreCase)))
            .Select((p, i) => (Place: p, Rank: Rank(p), Index: i))
            .OrderBy(p => p.Rank)
            .ThenBy(p => p.Index)
            .Select(p => p.Place);

        int Rank((DeviceRoute Route, string Address) place)
        {
            if (place.Route.SiteId is not { } site)
            {
                return 1;
            }

            return network is not null && IPAddress.TryParse(place.Address, out var ip) && network.Reaches(site, ip) ? 0 : 2;
        }
    }

    /// <summary>
    /// The sites offline for longer than <see cref="DeviceRelocationOptions.SiteOfflineMove"/> (counted from the first run
    /// that saw them offline; a site that comes back starts again).
    /// </summary>
    private HashSet<Guid> SitesOfflineLong(IReadOnlyList<Device> all)
    {
        var now = _time.GetUtcNow();
        var result = new HashSet<Guid>();
        if (sites is null)
        {
            return result;
        }

        var used = all.Select(d => d.SiteId).OfType<Guid>().ToHashSet();
        foreach (var site in _offlineSince.Keys.Where(s => !used.Contains(s) || sites.IsOnline(s)).ToList())
        {
            _offlineSince.Remove(site);
        }

        foreach (var site in used.Where(s => !sites.IsOnline(s) && !sites.IsStarting(s)))
        {
            if (sites.IsRemoved(site))
            {
                result.Add(site); // removed with its devices kept: it never comes back, look for them at once
                continue;
            }

            if (!_offlineSince.TryGetValue(site, out var since))
            {
                _offlineSince[site] = since = now;
            }

            if (now - since >= options.SiteOfflineMove)
            {
                result.Add(site);
            }
        }

        return result;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(options.InitialDelay, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogRunFailed(ex);
                }

                await Task.Delay(options.Interval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Every (route, address) each serial was announced at during the browse window, in the order seen.</summary>
    private async Task<Dictionary<string, IReadOnlyList<(DeviceRoute Route, string Address)>>> BrowseAsync(HashSet<string> serials, CancellationToken ct)
    {
        var found = new Dictionary<string, List<(DeviceRoute, string)>>(StringComparer.OrdinalIgnoreCase);
        var session = discovery.StartZeroConf();
        try
        {
            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(options.BrowseDuration);
            try
            {
                await foreach (var e in discovery.WatchAsync(session.Id, window.Token).ConfigureAwait(false))
                {
                    if (e.Device is { } device && serials.Contains(device.Serial) && !Equals(device.Address, IPAddress.None))
                    {
                        // Every route counts (a device answering through two sites). Discovery shows one row per MAC
                        // address; the other routes it answered through are tried at the same address.
                        if (!found.TryGetValue(device.Serial, out var places))
                        {
                            found[device.Serial] = places = [];
                        }

                        var address = device.Address.ToString();
                        foreach (var route in device.OtherRoutes.Items.Prepend(device.Route))
                        {
                            if (!places.Contains((route, address)))
                            {
                                places.Add((route, address));
                            }
                        }

                        if (found.Count == serials.Count)
                        {
                            window.CancelAfter(options.FoundGrace); // a little longer for the other routes
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Browse window over.
            }
        }
        finally
        {
            await discovery.StopAsync(session.Id).ConfigureAwait(false);
        }

        return found.ToDictionary(p => p.Key, p => (IReadOnlyList<(DeviceRoute, string)>)p.Value, StringComparer.OrdinalIgnoreCase);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Device {Serial} found again by mDNS at {Address}; its address was updated")]
    private partial void LogRelocated(string serial, string address);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Re-finding moved devices failed")]
    private partial void LogRunFailed(Exception ex);
}
