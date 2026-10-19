using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Discovery.Mdns;
using Oadm.Core.Sites;

namespace Oadm.Core.Discovery;

/// <summary>
/// Runs mDNS browsing and IP range scans as sessions. Each session keeps its own list of
/// discovered devices, deduplicated by serial, and streams changes to any number of watchers.
/// Free of gRPC types; the server maps sessions and events to the DiscoveryService contract.
/// <para>
/// mDNS hits are enriched with the same anonymous <see cref="IDeviceProbe"/> used by the range
/// scan, so model, firmware, scheme and status (e.g. PasswordNotSet) are filled in shortly after
/// the device first appears.
/// </para>
/// <para>
/// With sites (docs/sites.md, "Discovery, everywhere by itself") every session also asks every online site
/// (<see cref="ISiteNetwork"/>): zero-conf browses on each node, a range scan's first contact runs on each node that reaches
/// the range and the devices it finds are probed through the site, an address probe runs on every route the caller gives.
/// Each result carries its route. The same MAC address through several routes is one device, through a site rather than
/// the server's own network (<see cref="DiscoveredDevice.OtherRoutes"/> names the others); different devices at the same
/// address through different routes are separate devices.
/// </para>
/// </summary>
public sealed class DiscoveryService : IAsyncDisposable
{
    private readonly IMdnsBrowser _browser;
    private readonly RangeScanner _scanner;
    private readonly IDeviceProbe _probe;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly IDnsNameLookup? _dnsNames; // null: no DNS names (tests)
    private readonly ISiteNetwork? _sites; // null: the server's own network only

    /// <summary>DNS name lookups at the same time, for every session together.</summary>
    private readonly SemaphoreSlim _dnsLimit = new(16);

    public DiscoveryService(
        IMdnsBrowser browser,
        RangeScanner scanner,
        IDeviceProbe probe,
        ILogger<DiscoveryService>? logger = null,
        TimeProvider? timeProvider = null,
        IDnsNameLookup? dnsNames = null,
        ISiteNetwork? sites = null)
    {
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(probe);
        _browser = browser;
        _scanner = scanner;
        _probe = probe;
        _logger = logger ?? NullLogger<DiscoveryService>.Instance;
        _time = timeProvider ?? TimeProvider.System;
        _dnsNames = dnsNames;
        _sites = sites;
    }

    /// <summary>A site's mDNS browse without a duration of its own (a scan without a limit) asks again after this time.</summary>
    public static readonly TimeSpan SiteBrowseChunk = TimeSpan.FromMinutes(5);

    /// <summary>Devices a site's range scan found that are probed through the site at the same time (per site).</summary>
    public const int SiteProbeParallelism = 16;

    /// <summary>The online sites now (none without sites).</summary>
    private IReadOnlyList<Guid> OnlineSites()
    {
        try
        {
            return _sites?.OnlineSites() ?? [];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DiscoveryLog.SessionFailed(_logger, ex, "sites");
            return [];
        }
    }

    /// <summary>Timeout used when probing addresses seen via mDNS.</summary>
    public TimeSpan MdnsProbeTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Currently running or finished but not yet stopped sessions.</summary>
    public IReadOnlyCollection<DiscoverySession> Sessions => _sessions.Values.Select(s => s.Handle).ToList();

    /// <summary>
    /// Starts mDNS browsing. Without <paramref name="duration"/> it runs until <see cref="StopScan"/> or
    /// <see cref="StopAsync"/>; with one it ends on its own after that time. When browsing ends (time up or
    /// <see cref="StopScan"/>) the probes already started finish, then watchers get a final event with
    /// <see cref="DiscoveryEvent.Finished"/> = true and the devices stay until <see cref="StopAsync"/>.
    /// </summary>
    public DiscoverySession StartZeroConf(MdnsBrowseOptions? options = null, TimeSpan? duration = null)
    {
        if (duration is { } d && d <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "The duration must be positive.");
        }

        var session = CreateSession(DiscoverySessionKind.ZeroConf);
        if (duration is { } limit)
        {
            session.LimitScan(limit, _time);
        }

        session.Duration = duration;

        session.Run = Task.Run(() => RunZeroConfAsync(session, options, session.Scan.Token, session.Stop.Token), CancellationToken.None);
        return session.Handle;
    }

    /// <summary>
    /// Ends the scan of a session early (the add page's Stop button): a zero-conf session stops browsing,
    /// a range scan stops probing new addresses. Watchers get the final event with
    /// <see cref="DiscoveryEvent.Finished"/> = true; the devices found stay until <see cref="StopAsync"/>.
    /// Unknown or already finished sessions are ignored.
    /// </summary>
    public void StopScan(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.CancelScan();
        }
    }

    /// <summary>
    /// Starts a range scan. The session's watchers receive progress events and a final event with
    /// <see cref="DiscoveryEvent.Finished"/> = true, after which their streams end.
    /// </summary>
    /// <exception cref="ArgumentException">Invalid or oversized range.</exception>
    public DiscoverySession StartRangeScan(IPAddress from, IPAddress to, RangeScanOptions? options = null)
        => StartRangeScan(Ipv4Range.Create(from, to), options);

    /// <inheritdoc cref="StartRangeScan(IPAddress, IPAddress, RangeScanOptions?)"/>
    public DiscoverySession StartRangeScan(Ipv4Range range, RangeScanOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(range);
        var session = CreateSession(DiscoverySessionKind.RangeScan);
        session.Run = Task.Run(() => RunRangeScanAsync(session, range, options, session.Scan.Token, session.Stop.Token), CancellationToken.None);
        return session.Handle;
    }

    /// <summary>
    /// Starts a session that probes one address the user entered ("Add manually"). The session
    /// behaves like a range scan: at most one device, then a final event with
    /// <see cref="DiscoveryEvent.Finished"/> = true. <paramref name="probe"/> does the anonymous
    /// probe (the caller knows ports, schemes and host names); null means nothing answered.
    /// </summary>
    /// <param name="enteredAddress">The address as entered (host[:port]), kept on the device as <see cref="DiscoveredDevice.EnteredAddress"/>.</param>
    public DiscoverySession StartAddressProbe(string enteredAddress, Func<CancellationToken, Task<DeviceProbeResult?>> probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        return StartAddressProbe(enteredAddress, [new RouteProbe(DeviceRoute.Local, probe)]);
    }

    /// <summary>
    /// Probes one entered address on several routes at once (the server's own network and the sites that reach it). Each
    /// device found carries its route; the session finishes when every route answered. With
    /// <paramref name="expectedSerial"/> (a line of an export of AXIS Device Manager) only the route where that device
    /// answers counts: other devices are shown only when it answered nowhere.
    /// </summary>
    public DiscoverySession StartAddressProbe(string enteredAddress, IReadOnlyList<RouteProbe> probes, string? expectedSerial = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(enteredAddress);
        ArgumentNullException.ThrowIfNull(probes);
        var session = CreateSession(DiscoverySessionKind.Manual);
        session.Run = Task.Run(() => RunAddressProbeAsync(session, enteredAddress, probes, expectedSerial, session.Scan.Token, session.Stop.Token), CancellationToken.None);
        return session.Handle;
    }

    /// <summary>Snapshot of the devices discovered so far in a session.</summary>
    /// <exception cref="KeyNotFoundException">Unknown session.</exception>
    public IReadOnlyList<DiscoveredDevice> GetDevices(string sessionId) => GetSession(sessionId).Snapshot().Devices;

    /// <summary>
    /// Streams a session: first the devices already known (replay), then live changes. Ends when
    /// the session finishes (range scan) or is stopped, or when the token is cancelled.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Unknown session.</exception>
    public IAsyncEnumerable<DiscoveryEvent> WatchAsync(string sessionId, CancellationToken cancellationToken = default)
        => WatchSessionAsync(GetSession(sessionId), cancellationToken);

    private static async IAsyncEnumerable<DiscoveryEvent> WatchSessionAsync(Session session, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<DiscoveryEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        session.Subscribe(channel.Writer);
        try
        {
            await foreach (var e in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return e;
            }
        }
        finally
        {
            session.Unsubscribe(channel.Writer);
        }
    }

    /// <summary>Stops a session, ends all its watch streams and forgets it. Unknown ids are ignored.</summary>
    public async Task StopAsync(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
        {
            return;
        }

        await session.Stop.CancelAsync().ConfigureAwait(false);
        session.CancelScan();
        try
        {
            await session.Run.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        session.Complete(finishedEvent: null);
        session.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _sessions.Keys.ToList())
        {
            await StopAsync(id).ConfigureAwait(false);
        }
        _dnsLimit.Dispose();
    }

    private Session CreateSession(DiscoverySessionKind kind)
    {
        var session = new Session(new DiscoverySession(Guid.NewGuid().ToString("N"), kind));
        _sessions[session.Handle.Id] = session;
        DiscoveryLog.SessionStarted(_logger, session.Handle.Id, kind);
        return session;
    }

    private Session GetSession(string sessionId)
        => _sessions.TryGetValue(sessionId, out var s) ? s : throw new KeyNotFoundException($"Unknown discovery session '{sessionId}'.");

    /// <param name="scan">Ends browsing (time limit, <see cref="StopScan"/> or <see cref="StopAsync"/>).</param>
    /// <param name="ct">Ends everything (<see cref="StopAsync"/>); probes already started finish otherwise.</param>
    private async Task RunZeroConfAsync(Session session, MdnsBrowseOptions? options, CancellationToken scan, CancellationToken ct)
    {
        using var enrichLimit = new SemaphoreSlim(8);
        var enrichments = new List<Task>();
        var enrichLock = new Lock();
        var probed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Probe each (route, address) once per session (model, status, scheme; serial if TXT lacked it).
        void Seen(DeviceRoute route, IPAddress address, string? hostName)
        {
            lock (enrichLock)
            {
                if (probed.Add(route + "|" + address))
                {
                    enrichments.RemoveAll(t => t.IsCompleted);
                    enrichments.Add(EnrichAsync(session, route, address, hostName, enrichLimit, ct));
                }
            }
        }

        var browsers = new List<Task> { BrowseLocalAsync(session, options, Seen, scan) };
        browsers.AddRange(OnlineSites().Select(site => Task.Run(() => BrowseSiteAsync(session, site, Seen, scan), CancellationToken.None)));
        try
        {
            await Task.WhenAll(browsers).ConfigureAwait(false);
        }
        finally
        {
            Task[] pending;
            lock (enrichLock)
            {
                pending = [.. enrichments];
            }

            await Task.WhenAll(pending).ConfigureAwait(false);
        }

        if (!ct.IsCancellationRequested)
        {
            // Time limit or StopScan: the probes finished, the devices stay for the add page.
            var found = session.Snapshot().Devices.Count;
            DiscoveryLog.SessionFinished(_logger, session.Handle.Id, found);
            session.Complete(new DiscoveryEvent(null, 100, true));
        }
    }

    private async Task BrowseLocalAsync(Session session, MdnsBrowseOptions? options, Action<DeviceRoute, IPAddress, string?> seen, CancellationToken scan)
    {
        try
        {
            await foreach (var instance in _browser.BrowseAsync(options, scan).ConfigureAwait(false))
            {
                var address = instance.Addresses.Count > 0 ? instance.Addresses[0] : null;
                if (address is null)
                {
                    continue;
                }

                var hostName = string.IsNullOrEmpty(instance.HostName) ? null : instance.HostName;
                if (instance.Serial is { } serial)
                {
                    Publish(session, new DeviceObservation(serial, address, hostName, null, null, null, null, DiscoverySources.Mdns));
                }

                seen(DeviceRoute.Local, address, hostName);
            }
        }
        catch (OperationCanceledException) when (scan.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            DiscoveryLog.SessionFailed(_logger, ex, session.Handle.Id);
        }
    }

    /// <summary>mDNS on a site's node for the session's time (a session without a limit asks again every <see cref="SiteBrowseChunk"/>).</summary>
    private async Task BrowseSiteAsync(Session session, Guid site, Action<DeviceRoute, IPAddress, string?> seen, CancellationToken scan)
    {
        var route = DeviceRoute.ViaSite(site);
        try
        {
            var first = true;
            do
            {
                if (!first)
                {
                    // A browse without a limit asks the node again; never in a tight loop when a node ends early.
                    await Task.Delay(TimeSpan.FromSeconds(1), _time, scan).ConfigureAwait(false);
                }

                first = false;
                var duration = session.Duration ?? SiteBrowseChunk;
                await foreach (var found in _sites!.BrowseAsync(site, duration, scan).ConfigureAwait(false))
                {
                    var address = found.Addresses.FirstOrDefault(a => !IsLinkLocal(a)) ?? (found.Addresses.Count > 0 ? found.Addresses[0] : null);
                    if (address is null)
                    {
                        continue;
                    }

                    var hostName = string.IsNullOrEmpty(found.HostName) ? null : found.HostName.TrimEnd('.');
                    if (hostName?.EndsWith(".local", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        hostName = hostName[..^".local".Length];
                    }

                    if (SerialNumber.Normalize(found.Serial) is { } serial)
                    {
                        Publish(session, new DeviceObservation(serial, address, hostName, null, null, null, null, DiscoverySources.Mdns) { Route = route });
                    }

                    seen(route, address, hostName);
                }
            }
            while (session.Duration is null && !scan.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (scan.IsCancellationRequested)
        {
        }
        catch (SiteException ex)
        {
            DiscoveryLog.SiteSkipped(_logger, session.Handle.Id, ex.Message);
        }
        catch (Exception ex)
        {
            DiscoveryLog.SessionFailed(_logger, ex, session.Handle.Id);
        }
    }

    private static bool IsLinkLocal(IPAddress address) =>
        address.IsIPv6LinkLocal || (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && address.GetAddressBytes() is [169, 254, ..]);

    private async Task EnrichAsync(Session session, DeviceRoute route, IPAddress address, string? hostName, SemaphoreSlim limit, CancellationToken ct)
    {
        try
        {
            await limit.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var result = await _probe.ProbeAsync(address, route, MdnsProbeTimeout, ct).ConfigureAwait(false);
                if (result is not null)
                {
                    Publish(session, DeviceObservation.FromProbe(result, DiscoverySources.Mdns, route) with { HostName = hostName });
                }
                else
                {
                    session.MarkUnreachable(route, address, _time.GetUtcNow(), out var changed);
                    if (changed is not null)
                    {
                        session.Broadcast(new DiscoveryEvent(changed, 0, false));
                    }
                }
            }
            finally
            {
                limit.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (SiteException ex)
        {
            DiscoveryLog.SiteSkipped(_logger, session.Handle.Id, ex.Message);
        }
        catch (Exception ex)
        {
            DiscoveryLog.SessionFailed(_logger, ex, session.Handle.Id);
        }
    }

    private async Task RunRangeScanAsync(Session session, Ipv4Range range, RangeScanOptions? options, CancellationToken scan, CancellationToken ct)
    {
        // The server's own network and every online site that reaches the range: one progress over all of them.
        var sites = OnlineSites().Where(site => Reaches(site, range)).ToList();
        var parts = new int[1 + sites.Count];
        var progressLock = new Lock();
        void Report(int part, int percent)
        {
            int overall;
            lock (progressLock)
            {
                if (percent <= parts[part])
                {
                    return;
                }

                parts[part] = percent;
                overall = (int)parts.Average();
                if (overall <= session.Progress)
                {
                    return;
                }

                session.Progress = overall;
            }

            if (overall < 100)
            {
                session.Broadcast(new DiscoveryEvent(null, overall, false));
            }
        }

        var scans = new List<Task> { ScanLocalAsync(session, range, options, p => Report(0, p), scan) };
        scans.AddRange(sites.Select((site, i) => ScanSiteAsync(session, site, range, options ?? new RangeScanOptions(), p => Report(i + 1, p), scan)));
        try
        {
            await Task.WhenAll(scans).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            var count = session.Snapshot().Devices.Count;
            DiscoveryLog.SessionFinished(_logger, session.Handle.Id, count);
            session.Complete(new DiscoveryEvent(null, scan.IsCancellationRequested ? session.Progress : 100, true));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            DiscoveryLog.SessionFailed(_logger, ex, session.Handle.Id);
            session.Complete(new DiscoveryEvent(null, session.Progress, true));
        }
    }

    /// <summary>True when the site reaches an address of the range (first, last or any between).</summary>
    private bool Reaches(Guid site, Ipv4Range range)
    {
        try
        {
            return range.Any(address => _sites!.Reaches(site, address));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private async Task ScanLocalAsync(Session session, Ipv4Range range, RangeScanOptions? options, Action<int> progress, CancellationToken scan)
    {
        try
        {
            await foreach (var result in _scanner.ScanAsync(range, options, new SyncProgress(progress), scan).ConfigureAwait(false))
            {
                Publish(session, DeviceObservation.FromProbe(result, DiscoverySources.RangeScan, DeviceRoute.Local));
            }

            progress(100);
        }
        catch (OperationCanceledException) when (scan.IsCancellationRequested)
        {
            // StopScan or StopAsync: the addresses probed so far count.
        }
    }

    /// <summary>The first contact on the site's node (thousands of short connects stay at the site), then the probe of each answer through the site.</summary>
    private async Task ScanSiteAsync(Session session, Guid site, Ipv4Range range, RangeScanOptions options, Action<int> progress, CancellationToken scan)
    {
        var route = DeviceRoute.ViaSite(site);
        using var limit = new SemaphoreSlim(SiteProbeParallelism);
        var probes = new List<Task>();
        try
        {
            await foreach (var found in _sites!.RangeScanAsync(site, range.From, range.To, options.Parallelism, options.Timeout, scan).ConfigureAwait(false))
            {
                if (found.Total > 0)
                {
                    // The probes of the answers belong to the progress too: the site's part ends at 95 % until they finished.
                    progress((int)((long)found.Tried * 95 / found.Total));
                }

                if (found.Address is { } address)
                {
                    probes.RemoveAll(t => t.IsCompleted);
                    probes.Add(ProbeFoundAsync(session, route, address, options.Timeout, limit, scan));
                }
            }
        }
        catch (OperationCanceledException) when (scan.IsCancellationRequested)
        {
        }
        catch (SiteException ex)
        {
            DiscoveryLog.SiteSkipped(_logger, session.Handle.Id, ex.Message);
        }
        finally
        {
            await Task.WhenAll(probes).ConfigureAwait(false);
        }

        progress(100);
    }

    private async Task ProbeFoundAsync(Session session, DeviceRoute route, IPAddress address, TimeSpan timeout, SemaphoreSlim limit, CancellationToken scan)
    {
        try
        {
            await limit.WaitAsync(scan).ConfigureAwait(false);
            try
            {
                if (await _probe.ProbeAsync(address, route, timeout, scan).ConfigureAwait(false) is { } result)
                {
                    Publish(session, DeviceObservation.FromProbe(result, DiscoverySources.RangeScan, route));
                }
            }
            finally
            {
                limit.Release();
            }
        }
        catch (OperationCanceledException) when (scan.IsCancellationRequested)
        {
        }
        catch (SiteException ex)
        {
            DiscoveryLog.SiteSkipped(_logger, session.Handle.Id, ex.Message);
        }
    }

    private async Task RunAddressProbeAsync(Session session, string enteredAddress, IReadOnlyList<RouteProbe> probes, string? expectedSerial,
        CancellationToken scan, CancellationToken ct)
    {
        try
        {
            var problems = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var held = new System.Collections.Concurrent.ConcurrentQueue<DeviceObservation>();
            var expected = string.IsNullOrWhiteSpace(expectedSerial) ? null : expectedSerial.Trim().ToUpperInvariant();
            await Task.WhenAll(probes.Select(async probe =>
            {
                DeviceProbeResult? result;
                try
                {
                    result = await probe.Probe(scan).ConfigureAwait(false);
                }
                catch (SiteException ex)
                {
                    problems.Enqueue(ex.Message);
                    return;
                }

                if (result is null)
                {
                    return;
                }

                var observation = DeviceObservation.FromProbe(result, DiscoverySources.Manual, probe.Route) with { EnteredAddress = enteredAddress };
                if (expected is not null && !string.Equals(result.Serial, expected, StringComparison.OrdinalIgnoreCase))
                {
                    held.Enqueue(observation); // shown only when the expected device answered on no route
                    return;
                }

                Publish(session, observation);
            })).ConfigureAwait(false);

            if (expected is not null && !session.Snapshot().Devices.Any(d => string.Equals(d.Serial, expected, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var observation in held)
                {
                    Publish(session, observation);
                }
            }

            session.Progress = 100;
            var count = session.Snapshot().Devices.Count;
            DiscoveryLog.SessionFinished(_logger, session.Handle.Id, count);
            session.Complete(new DiscoveryEvent(null, 100, true) { Problem = count == 0 && problems.TryPeek(out var problem) ? problem : null });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException) when (scan.IsCancellationRequested)
        {
            session.Complete(new DiscoveryEvent(null, 100, true));
        }
        catch (Exception ex)
        {
            DiscoveryLog.SessionFailed(_logger, ex, session.Handle.Id);
            session.Complete(new DiscoveryEvent(null, 100, true));
        }
    }

    private void Publish(Session session, DeviceObservation observation)
    {
        var changed = session.Merge(observation, _time.GetUtcNow());
        if (changed is not null)
        {
            session.Broadcast(new DiscoveryEvent(changed, session.Handle.Kind == DiscoverySessionKind.ZeroConf ? 0 : session.Progress, false));
            if (_dnsNames is null || changed.EnteredAddress is not null || changed.DnsName is not null)
            {
                return;
            }

            if (!session.ClaimDnsLookup(changed, out var known))
            {
                // Looked up before (the device came back to this address): the name found then, without asking DNS again.
                if (known is not null)
                {
                    PublishDnsName(session, changed.Serial, changed.Address, changed.Route, known);
                }

                return;
            }

            _ = LookupDnsNameAsync(session, changed.Serial, changed.Address, changed.Route);
        }
    }

    /// <summary>The DNS name of a found device's IP on its route, once per device, route and address (manual entries keep what was typed).</summary>
    private async Task LookupDnsNameAsync(Session session, string serial, IPAddress address, DeviceRoute route)
    {
        var ct = session.Stop.Token;
        try
        {
            await _dnsLimit.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var name = SystemDnsNameLookup.Normalize(await _dnsNames!.LookupAsync(address, route, ct).ConfigureAwait(false));
                session.SetDnsName(serial, address, route, name);
                if (name is not null)
                {
                    PublishDnsName(session, serial, address, route, name);
                }
            }
            finally
            {
                _dnsLimit.Release();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // session stopped
        }
    }

    private void PublishDnsName(Session session, string serial, IPAddress address, DeviceRoute route, string name) =>
        Publish(session, new DeviceObservation(serial, address, null, null, null, null, null, DiscoverySources.None) { DnsName = name, Route = route });

    /// <summary>A partial view of a device from one source; null fields mean "not known by this source".</summary>
    internal sealed record DeviceObservation(
        string Serial,
        IPAddress Address,
        string? HostName,
        string? Model,
        string? FirmwareVersion,
        string? Scheme,
        DiscoveredDeviceStatus? Status,
        DiscoverySources Source,
        string? ProductType = null,
        string? EnteredAddress = null)
    {
        /// <summary>The verified DNS name of <see cref="Address"/>, from the lookup.</summary>
        public string? DnsName { get; init; }

        /// <summary>Where the source saw the device.</summary>
        public DeviceRoute Route { get; init; }

        public static DeviceObservation FromProbe(DeviceProbeResult r, DiscoverySources source, DeviceRoute route = default)
            => new(r.Serial, r.Address, null, r.Model, r.FirmwareVersion, r.Scheme, r.Status, source, r.ProductType) { Route = route };
    }

    /// <summary>
    /// The route a device answering through both is shown through: a site rather than the server's own network (the site's
    /// node reaches the address, the server's own network may only reach it through a router); between two sites the first
    /// one found stays.
    /// </summary>
    internal static bool Prefer(DeviceRoute candidate, DeviceRoute current) => !candidate.IsLocal && current.IsLocal;

    /// <summary>
    /// Merges an observation into an existing device (or creates it). Returns the new device when
    /// anything visible changed, otherwise null. Later non-null values win; the address follows
    /// the latest observation on the device's route; sources accumulate. An observation through another route
    /// either takes the device over (<see cref="Prefer"/>: its route, address, scheme and status) or only fills what is
    /// still unknown; the other route is remembered in <see cref="DiscoveredDevice.OtherRoutes"/>.
    /// </summary>
    internal static DiscoveredDevice? Merge(DiscoveredDevice? existing, DeviceObservation o, DateTimeOffset now)
    {
        if (existing is null)
        {
            return new DiscoveredDevice(o.Serial, o.Serial, o.Address, o.HostName, o.Model, o.FirmwareVersion, o.Status ?? DiscoveredDeviceStatus.Unknown, o.Scheme, o.Source, now, o.ProductType, o.EnteredAddress, o.DnsName)
            {
                Route = o.Route,
            };
        }

        DiscoveredDevice merged;
        if (o.Route != existing.Route && !Prefer(o.Route, existing.Route))
        {
            // Another route that does not win: what it knows fills the gaps, the device stays where it is.
            merged = existing with
            {
                HostName = existing.HostName ?? o.HostName,
                Model = existing.Model ?? o.Model,
                FirmwareVersion = existing.FirmwareVersion ?? o.FirmwareVersion,
                ProductType = existing.ProductType ?? o.ProductType,
                Sources = existing.Sources | o.Source,
                OtherRoutes = o.Source == DiscoverySources.None ? existing.OtherRoutes : existing.OtherRoutes.With(o.Route),
                LastSeenUtc = existing.LastSeenUtc,
            };
        }
        else if (o.Route != existing.Route)
        {
            // A site takes the device over from the server's own network: route, address, scheme and status are the site's.
            merged = existing with
            {
                Address = o.Address,
                Route = o.Route,
                OtherRoutes = existing.OtherRoutes.With(existing.Route).Without(o.Route),
                HostName = o.HostName ?? existing.HostName,
                Model = o.Model ?? existing.Model,
                FirmwareVersion = o.FirmwareVersion ?? existing.FirmwareVersion,
                ProductType = o.ProductType ?? existing.ProductType,
                Scheme = o.Scheme,
                Status = o.Status ?? DiscoveredDeviceStatus.Unknown,
                Sources = existing.Sources | o.Source,
                EnteredAddress = o.EnteredAddress ?? existing.EnteredAddress,
                DnsName = o.DnsName,
                LastSeenUtc = existing.LastSeenUtc,
            };
        }
        else
        {
            merged = existing with
            {
                Address = o.Address,
                HostName = o.HostName ?? existing.HostName,
                Model = o.Model ?? existing.Model,
                FirmwareVersion = o.FirmwareVersion ?? existing.FirmwareVersion,
                ProductType = o.ProductType ?? existing.ProductType,
                Scheme = o.Scheme ?? existing.Scheme,
                Status = o.Status ?? existing.Status,
                Sources = existing.Sources | o.Source,
                EnteredAddress = o.EnteredAddress ?? existing.EnteredAddress,

                // A name belongs to the address it was looked up for: a new address drops it (and is looked up again).
                DnsName = o.DnsName is { } name && o.Address.Equals(existing.Address) ? name
                    : o.Address.Equals(existing.Address) ? existing.DnsName : null,
                LastSeenUtc = existing.LastSeenUtc,
            };
        }

        return merged == existing ? null : merged with { LastSeenUtc = now };
    }

    private sealed class Session(DiscoverySession handle) : IDisposable
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<string, DiscoveredDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<ChannelWriter<DiscoveryEvent>> _subscribers = [];
        private DiscoveryEvent? _finished;
        private bool _completed;

        public DiscoverySession Handle { get; } = handle;

        private CancellationTokenSource? _limit;

        public CancellationTokenSource Stop { get; } = new();

        /// <summary>Ends the scan only (time limit, StopScan); also cancelled by StopAsync.</summary>
        public CancellationTokenSource Scan { get; } = new();

        public Task Run { get; set; } = Task.CompletedTask;

        /// <summary>The time limit of a zero-conf scan (null: until stopped); the sites browse as long.</summary>
        public TimeSpan? Duration { get; set; }

        /// <summary>Called once before the run starts.</summary>
        public void LimitScan(TimeSpan duration, TimeProvider time)
        {
            _limit = new CancellationTokenSource(duration, time);
            _limit.Token.Register(CancelScan);
        }

        public void CancelScan()
        {
            try
            {
                Scan.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // session already stopped
            }
        }

        public void Dispose()
        {
            _limit?.Dispose();
            Scan.Dispose();
            Stop.Dispose();
        }

        public int Progress { get; set; }

        public (IReadOnlyList<DiscoveredDevice> Devices, bool Completed) Snapshot()
        {
            lock (_lock)
            {
                return (_devices.Values.ToList(), _completed);
            }
        }

        // Per device and address: the DNS name found (null: none, or the lookup still runs).
        private readonly ConcurrentDictionary<string, string?> _dnsLookups = new(StringComparer.Ordinal);

        /// <summary>True once per device and address (the caller starts the lookup); later <paramref name="known"/> is the name it found.</summary>
        public bool ClaimDnsLookup(DiscoveredDevice device, out string? known)
        {
            var key = DnsKey(device.Serial, device.Address, device.Route);
            if (_dnsLookups.TryAdd(key, null))
            {
                known = null;
                return true;
            }

            known = _dnsLookups.GetValueOrDefault(key);
            return false;
        }

        /// <summary>The result of the lookup <see cref="ClaimDnsLookup"/> started.</summary>
        public void SetDnsName(string serial, IPAddress address, DeviceRoute route, string? name) => _dnsLookups[DnsKey(serial, address, route)] = name;

        private static string DnsKey(string serial, IPAddress address, DeviceRoute route) => serial + "|" + route + "|" + address;

        public DiscoveredDevice? Merge(DeviceObservation observation, DateTimeOffset now)
        {
            lock (_lock)
            {
                _devices.TryGetValue(observation.Serial, out var existing);
                var merged = DiscoveryService.Merge(existing, observation, now);
                if (merged is not null)
                {
                    _devices[observation.Serial] = merged;
                }

                return merged;
            }
        }

        public void MarkUnreachable(DeviceRoute route, IPAddress address, DateTimeOffset now, out DiscoveredDevice? changed)
        {
            changed = null;
            lock (_lock)
            {
                var device = _devices.Values.FirstOrDefault(d => d.Route == route && d.Address.Equals(address) && d.Status == DiscoveredDeviceStatus.Unknown);
                if (device is not null)
                {
                    changed = device with { Status = DiscoveredDeviceStatus.Unreachable, LastSeenUtc = now };
                    _devices[device.Serial] = changed;
                }
            }
        }

        public void Subscribe(ChannelWriter<DiscoveryEvent> writer)
        {
            lock (_lock)
            {
                foreach (var d in _devices.Values)
                {
                    writer.TryWrite(new DiscoveryEvent(d, Handle.Kind == DiscoverySessionKind.ZeroConf ? 0 : Progress, false));
                }

                if (_completed)
                {
                    if (_finished is not null)
                    {
                        writer.TryWrite(_finished);
                    }

                    writer.TryComplete();
                    return;
                }

                _subscribers.Add(writer);
            }
        }

        public void Unsubscribe(ChannelWriter<DiscoveryEvent> writer)
        {
            lock (_lock)
            {
                _subscribers.Remove(writer);
            }
        }

        public void Broadcast(DiscoveryEvent e)
        {
            lock (_lock)
            {
                if (_completed)
                {
                    return;
                }

                foreach (var s in _subscribers)
                {
                    s.TryWrite(e);
                }
            }
        }

        public void Complete(DiscoveryEvent? finishedEvent)
        {
            lock (_lock)
            {
                if (_completed)
                {
                    foreach (var s in _subscribers)
                    {
                        s.TryComplete();
                    }

                    _subscribers.Clear();
                    return;
                }

                _completed = true;
                _finished = finishedEvent;
                foreach (var s in _subscribers)
                {
                    if (finishedEvent is not null)
                    {
                        s.TryWrite(finishedEvent);
                    }

                    s.TryComplete();
                }

                _subscribers.Clear();
            }
        }
    }

    /// <summary>IProgress that invokes synchronously (Progress&lt;T&gt; posts to the thread pool and may reorder).</summary>
    private sealed class SyncProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
