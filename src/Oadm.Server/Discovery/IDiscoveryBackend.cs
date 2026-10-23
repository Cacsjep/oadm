using Oadm.Core.Discovery;

namespace Oadm.Server.Discovery;

/// <summary>
/// What the gRPC DiscoveryService and AddDevicesService need from discovery. Keeps the gRPC
/// layer independent of how discovery works; production: <see cref="CoreDiscoveryBackend"/>.
/// </summary>
public interface IDiscoveryBackend
{
    /// <summary>
    /// Starts mDNS browsing for the add page. It ends on its own after the server setting
    /// Discovery.ZeroConfSeconds (or on <see cref="StopScan"/>) with a finished event; the devices stay
    /// until <see cref="StopAsync"/>.
    /// </summary>
    /// <param name="ct">Cancellation.</param>
    /// <param name="routes">Where to look (a user with a device group: the group's sites); null = everywhere.</param>
    Task<DiscoverySession> StartZeroConfAsync(CancellationToken ct, DiscoveryRoutes? routes = null);

    /// <summary>Ends the scan of a session early; watchers get the finished event, the devices stay. Unknown ids are ignored.</summary>
    void StopScan(string sessionId);

    /// <summary>Starts an IP range scan with parallelism and timeout from the server settings.</summary>
    /// <exception cref="ArgumentException">Invalid, reversed or oversized range.</exception>
    /// <param name="fromAddress">First address.</param>
    /// <param name="toAddress">Last address.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="routes">Where to scan; null = everywhere.</param>
    Task<DiscoverySession> StartRangeScanAsync(string fromAddress, string toAddress, CancellationToken ct, DiscoveryRoutes? routes = null);

    /// <summary>
    /// Starts a session that probes one address the user entered (IP or host name, optional port
    /// and scheme). Finishes like a range scan, with at most one device.
    /// </summary>
    /// <param name="input">The address as entered or read from the file.</param>
    /// <param name="importLine">A line of an imported file: the first contact waits only the range scan's time (Scan.TimeoutMs),
    /// so addresses that do not answer cost little. "Add manually" keeps the longer time for slow links.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="siteId">The import's Site column: only through this site; null = the server's own network and every
    /// online site that reaches the address (a host name: every online site, each resolves it itself).</param>
    /// <param name="expectedSerial">A line of an ADM export: only the route where this device answers counts.</param>
    /// <param name="routes">The routes that may be probed (a user with a device group: the group's sites); null = every one.</param>
    /// <exception cref="ArgumentException">Unusable address, a host name no route resolves, or the chosen site cannot
    /// probe (offline: the message for the user).</exception>
    Task<DiscoverySession> StartAddressProbeAsync(string input, bool importLine, CancellationToken ct, Guid? siteId = null, string? expectedSerial = null, DiscoveryRoutes? routes = null);

    /// <summary>
    /// Replays the devices known in the session, then streams changes and range-scan progress.
    /// Ends when a range scan finished, the session is stopped or <paramref name="ct"/> is cancelled.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Unknown session.</exception>
    IAsyncEnumerable<DiscoveryEvent> WatchAsync(string sessionId, CancellationToken ct);

    /// <summary>Stops a session. Unknown ids are ignored.</summary>
    Task StopAsync(string sessionId);

    /// <summary>
    /// A device discovered in the session; falls back to the devices the same session found before it was stopped, so
    /// the add still works after the client stopped discovery. Never a device of another session.
    /// </summary>
    DiscoveredDevice? FindDiscovered(string sessionId, string discoveredId);
}
