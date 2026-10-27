using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

using Oadm.Core.Discovery;
using Oadm.Core.Settings;
using Oadm.Core.Sites;
using Oadm.Core.Vapix;

using CoreDiscoveryService = Oadm.Core.Discovery.DiscoveryService;
using SdkDeviceStatus = Oadm.Sdk.Devices.DeviceStatus;

namespace Oadm.Server.Discovery;

/// <summary><see cref="IDiscoveryBackend"/> over the Core discovery (mDNS browser, range scanner, sessions).</summary>
/// <remarks>
/// Every mode asks everywhere by itself (docs/spec/sites.md, Discovery and moves at sites): the Core discovery runs
/// zero-conf and range scans on the server's own network and every online site; an address of "Add manually", an imported
/// line and a waiting device go to the server's own network and each online site that reaches the address, a host name to
/// all of them (each resolves it itself).
/// </remarks>
public sealed class CoreDiscoveryBackend(CoreDiscoveryService discovery, ServerSettingsStore settings, VapixProbe probe, RoutedDns? dns = null,
    ISiteNetwork? sites = null, ISiteStates? states = null, TimeProvider? time = null) : IDiscoveryBackend
{
    private readonly RoutedDns _dns = dns ?? RoutedDns.Local;
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>How long a host name of "Add manually" may take to resolve.</summary>
    public static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long the devices of a stopped session stay findable (like the session's login results).</summary>
    public static readonly TimeSpan RecentLifetime = TimeSpan.FromMinutes(30);

    // The devices of stopped sessions, per (session, discovered id): a session never finds another session's devices.
    private readonly ConcurrentDictionary<(string Session, string Id), (DiscoveredDevice Device, DateTimeOffset Seen)> _recent = new();
    private long _lastPruneTicks;

    public async Task<DiscoverySession> StartZeroConfAsync(CancellationToken ct, DiscoveryRoutes? routes = null)
    {
        var current = await settings.GetServerSettingsAsync(ct).ConfigureAwait(false);
        var seconds = Math.Clamp(current.ZeroConfSeconds, ServerSettings.MinZeroConfSeconds, ServerSettings.MaxZeroConfSeconds);
        return discovery.StartZeroConf(duration: TimeSpan.FromSeconds(seconds), routes: routes);
    }

    public void StopScan(string sessionId) => discovery.StopScan(sessionId);

    public async Task<DiscoverySession> StartRangeScanAsync(string fromAddress, string toAddress, CancellationToken ct, DiscoveryRoutes? routes = null)
    {
        if (!Ipv4Range.TryParse(fromAddress, toAddress, out var range, out var error))
        {
            throw new ArgumentException(error ?? "Invalid address range.");
        }

        var current = await settings.GetServerSettingsAsync(ct).ConfigureAwait(false);
        var options = new RangeScanOptions
        {
            Parallelism = current.ScanParallelism,
            Timeout = TimeSpan.FromMilliseconds(current.ScanTimeoutMs),
        };
        return discovery.StartRangeScan(range!, options, routes);
    }

    public async Task<DiscoverySession> StartAddressProbeAsync(string input, bool importLine, CancellationToken ct, Guid? siteId = null, string? expectedSerial = null,
        DiscoveryRoutes? routes = null)
    {
        var allowed = routes ?? DiscoveryRoutes.All;
        if (!EnteredAddress.TryParse(input, out var entered, out var error))
        {
            throw new ArgumentException(error);
        }

        string[]? schemes = entered!.Scheme is null ? null : [entered.Scheme];
        TimeSpan? timeout = null;
        if (importLine)
        {
            var current = await settings.GetServerSettingsAsync(ct).ConfigureAwait(false);
            timeout = TimeSpan.FromMilliseconds(current.ScanTimeoutMs);
        }

        // Where to probe: the chosen site only, else the server's own network and every online site that reaches the
        // address (a name: every online site, each resolves it on its own DNS).
        var literal = IPAddress.TryParse(entered.Host, out var typed) ? typed : null;
        List<DeviceRoute> probeRoutes;
        if (siteId is { } site)
        {
            if (!allowed.Allows(site))
            {
                // Not one of the caller's sites: nothing is probed (the gRPC layer refuses such a site before).
                return discovery.StartAddressProbe(entered.DeviceAddress, [], expectedSerial, allowed);
            }

            if (sites is null || !sites.OnlineSites().Contains(site))
            {
                // The line's site is offline: nothing answers there now ("Site offline: Building B"); the line can wait.
                var offline = SiteException.For(site, states?.NameOf(site) ?? site.ToString(), SiteProblem.Offline);
                return discovery.StartAddressProbe(entered.DeviceAddress, [new RouteProbe(DeviceRoute.ViaSite(site), _ => throw offline)], expectedSerial, allowed);
            }

            probeRoutes = [DeviceRoute.ViaSite(site)];
        }
        else
        {
            probeRoutes = allowed.Local ? [DeviceRoute.Local] : [];
            if (sites is not null)
            {
                probeRoutes.AddRange(sites.OnlineSites().Where(s => allowed.Allows(s) && (literal is null || sites.Reaches(s, literal))).Select(DeviceRoute.ViaSite));
            }
        }

        if (probeRoutes.Count == 0)
        {
            // No route of the caller reaches the address: nothing answers.
            return discovery.StartAddressProbe(entered.DeviceAddress, [], expectedSerial, allowed);
        }

        // Names resolve on each route before the session starts: a name no route knows is the line's problem as before.
        var resolved = await Task.WhenAll(probeRoutes.Select(async route => (Route: route, Address: literal ?? await TryResolveAsync(route, entered.Host, ct).ConfigureAwait(false))))
            .ConfigureAwait(false);
        var usable = resolved.Where(r => r.Address is not null).ToList();
        if (usable.Count == 0)
        {
            throw new ArgumentException($"The host name '{entered.Host}' could not be resolved.");
        }

        var probes = usable.Select(r => new RouteProbe(r.Route, async token =>
        {
            var result = await probe.ProbeAsync(entered.DeviceAddress, token, schemes, timeout, r.Route).ConfigureAwait(false);
            return result is null ? null : ToDiscovery(result, r.Address!);
        })).ToList();
        return discovery.StartAddressProbe(entered.DeviceAddress, probes, expectedSerial, allowed);
    }

    public IAsyncEnumerable<DiscoveryEvent> WatchAsync(string sessionId, CancellationToken ct) =>
        discovery.WatchAsync(sessionId, ct);

    public async Task StopAsync(string sessionId)
    {
        Remember(sessionId);
        await discovery.StopAsync(sessionId).ConfigureAwait(false);
    }

    public bool IsActive(string sessionId) => discovery.HasSession(sessionId);

    public DiscoveredDevice? FindDiscovered(string sessionId, string discoveredId)
    {
        if (string.IsNullOrWhiteSpace(discoveredId))
        {
            return null;
        }

        Remember(sessionId);
        return _recent.TryGetValue((sessionId, discoveredId.ToUpperInvariant()), out var found) ? found.Device : null;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, DiscoveredDevice> FindDiscovered(string sessionId, IReadOnlyCollection<string> discoveredIds)
    {
        ArgumentNullException.ThrowIfNull(discoveredIds);
        var result = new Dictionary<string, DiscoveredDevice>(StringComparer.OrdinalIgnoreCase);
        Remember(sessionId); // once for the whole request: a copy of the session's devices per id would be quadratic
        foreach (var id in discoveredIds)
        {
            if (!string.IsNullOrWhiteSpace(id) && _recent.TryGetValue((sessionId, id.ToUpperInvariant()), out var found))
            {
                result[id] = found.Device;
            }
        }

        return result;
    }

    /// <summary>Maps the add probe's result to a discovery result at the resolved address.</summary>
    internal static DeviceProbeResult ToDiscovery(VapixProbeResult result, IPAddress address) => new(
        address,
        result.Serial,
        result.Scheme,
        result.Model,
        null,
        result.FirmwareVersion,
        result.Status switch
        {
            SdkDeviceStatus.PasswordNotSet => DiscoveredDeviceStatus.PasswordNotSet,
            SdkDeviceStatus.Ok => DiscoveredDeviceStatus.AnonymousAccess,
            _ => DiscoveredDeviceStatus.CredentialsRequired,
        },
        !result.AuthenticationRequired,
        result.ProductType);

    /// <summary>The first address of <paramref name="host"/> on <paramref name="route"/> (IPv4 first); null when that route does not know it.</summary>
    private async Task<IPAddress?> TryResolveAsync(DeviceRoute route, string host, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ResolveTimeout);
        try
        {
            var addresses = await _dns.ResolveAsync(route, host, timeout.Token).ConfigureAwait(false);
            return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
        }
        catch (Exception ex) when (ex is SocketException or SiteException or ArgumentException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    private void Remember(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var now = _time.GetUtcNow();
        Prune(now);
        try
        {
            foreach (var device in discovery.GetDevices(sessionId))
            {
                if (!Equals(device.Address, IPAddress.None))
                {
                    _recent[(sessionId, device.DiscoveredId.ToUpperInvariant())] = (device, now);
                }
            }
        }
        catch (KeyNotFoundException)
        {
            // Session already stopped: keep what was remembered before.
        }
    }

    /// <summary>Forgets the devices of sessions not used for <see cref="RecentLifetime"/> (now and then, not on every call).</summary>
    private void Prune(DateTimeOffset now)
    {
        var last = Interlocked.Read(ref _lastPruneTicks);
        if (now.UtcTicks - last < TimeSpan.TicksPerMinute || Interlocked.CompareExchange(ref _lastPruneTicks, now.UtcTicks, last) != last)
        {
            return;
        }

        foreach (var (key, value) in _recent)
        {
            if (now - value.Seen > RecentLifetime)
            {
                _recent.TryRemove(new KeyValuePair<(string, string), (DiscoveredDevice, DateTimeOffset)>(key, value));
            }
        }
    }
}
