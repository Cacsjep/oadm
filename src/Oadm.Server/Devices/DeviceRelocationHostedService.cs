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

    /// <summary>The devices of a site offline for longer than this are looked for elsewhere too. Default 1 hour.</summary>
    public TimeSpan SiteOfflineMove { get; init; } = TimeSpan.FromHours(1);
}

/// <summary>
/// Periodic mDNS re-find: when a managed device does not answer on its route (status Unreachable, or Site offline for
/// longer than <see cref="DeviceRelocationOptions.SiteOfflineMove"/>; address is an IP literal), a short zero-conf browse
/// on the server's network and through every online site looks for its serial number; when the device announces another
/// address or answers through another route, <see cref="DeviceAddressService.TryRelocateAsync(string, string, DeviceRoute, string, bool, CancellationToken)"/>
/// verifies it there and moves the record: route and address together (logged, published as a device change, audited
/// "Device moved" by "system"). A device that answers on its current route never moves; devices addressed by name never
/// move. Nothing is browsed while no device is a candidate.
/// </summary>
public sealed partial class DeviceRelocationHostedService(
    CoreDiscoveryService discovery,
    DeviceRepository devices,
    DeviceAddressService addresses,
    DeviceRelocationOptions options,
    ILogger<DeviceRelocationHostedService> logger,
    ISiteStates? sites = null,
    AuditLog? audit = null,
    TimeProvider? time = null) : BackgroundService
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
        foreach (var (serial, (route, address)) in seen)
        {
            var before = candidates[serial];
            var result = await addresses.TryRelocateAsync(serial, address, route, "found again by mDNS", requireUnreachable: true, ct).ConfigureAwait(false);
            if (result == DeviceAddressChangeResult.Updated)
            {
                moved++;
                LogRelocated(serial, address);
                if (audit is not null)
                {
                    await audit.WriteAsync("system", string.Empty, AuditActions.DeviceMoved, serial,
                        "Found again: " + DeviceMoveText.Of(DeviceConnections.RouteOf(before), before.Address, route, address, sites), ct).ConfigureAwait(false);
                }
            }
        }

        return moved;
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

    private async Task<Dictionary<string, (DeviceRoute Route, string Address)>> BrowseAsync(HashSet<string> serials, CancellationToken ct)
    {
        var found = new Dictionary<string, (DeviceRoute, string)>(StringComparer.OrdinalIgnoreCase);
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
                        found[device.Serial] = (device.Route, device.Address.ToString());
                        if (found.Count == serials.Count)
                        {
                            break;
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

        return found;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Device {Serial} found again by mDNS at {Address}; its address was updated")]
    private partial void LogRelocated(string serial, string address);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Re-finding moved devices failed")]
    private partial void LogRunFailed(Exception ex);
}
