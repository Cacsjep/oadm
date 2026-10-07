using Oadm.Core.Discovery;

namespace Oadm.Server.Discovery;

/// <summary>
/// What the gRPC DiscoveryService and AddDevicesService need from discovery. Keeps the gRPC
/// layer independent of how discovery works; production: <see cref="CoreDiscoveryBackend"/>.
/// </summary>
public interface IDiscoveryBackend
{
    /// <summary>Starts continuous mDNS browsing; runs until <see cref="StopAsync"/>.</summary>
    DiscoverySession StartZeroConf();

    /// <summary>Starts an IP range scan with parallelism and timeout from the server settings.</summary>
    /// <exception cref="ArgumentException">Invalid, reversed or oversized range.</exception>
    Task<DiscoverySession> StartRangeScanAsync(string fromAddress, string toAddress, CancellationToken ct);

    /// <summary>
    /// Starts a session that probes one address the user entered (IP or host name, optional port
    /// and scheme). Finishes like a range scan, with at most one device.
    /// </summary>
    /// <exception cref="ArgumentException">Unusable address or unresolvable host name (message for the user).</exception>
    Task<DiscoverySession> StartAddressProbeAsync(string input, CancellationToken ct);

    /// <summary>
    /// Replays the devices known in the session, then streams changes and range-scan progress.
    /// Ends when a range scan finished, the session is stopped or <paramref name="ct"/> is cancelled.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Unknown session.</exception>
    IAsyncEnumerable<DiscoveryEvent> WatchAsync(string sessionId, CancellationToken ct);

    /// <summary>Stops a session. Unknown ids are ignored.</summary>
    Task StopAsync(string sessionId);

    /// <summary>
    /// A device discovered in the session; falls back to devices recently seen in stopped
    /// sessions so the add wizard still works after the client stopped discovery.
    /// </summary>
    DiscoveredDevice? FindDiscovered(string sessionId, string discoveredId);
}
