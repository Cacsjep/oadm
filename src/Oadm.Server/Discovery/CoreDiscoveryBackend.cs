using System.Collections.Concurrent;
using System.Net;

using Oadm.Core.Discovery;
using Oadm.Core.Settings;

using CoreDiscoveryService = Oadm.Core.Discovery.DiscoveryService;

namespace Oadm.Server.Discovery;

/// <summary><see cref="IDiscoveryBackend"/> over the Core discovery (mDNS browser, range scanner, sessions).</summary>
public sealed class CoreDiscoveryBackend(CoreDiscoveryService discovery, ServerSettingsStore settings) : IDiscoveryBackend
{
    private readonly ConcurrentDictionary<string, DiscoveredDevice> _recent = new(StringComparer.OrdinalIgnoreCase);

    public DiscoverySession StartZeroConf() => discovery.StartZeroConf();

    public async Task<DiscoverySession> StartRangeScanAsync(string fromAddress, string toAddress, CancellationToken ct)
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
        return discovery.StartRangeScan(range!, options);
    }

    public IAsyncEnumerable<DiscoveryEvent> WatchAsync(string sessionId, CancellationToken ct) =>
        discovery.WatchAsync(sessionId, ct);

    public async Task StopAsync(string sessionId)
    {
        Remember(sessionId);
        await discovery.StopAsync(sessionId).ConfigureAwait(false);
    }

    public DiscoveredDevice? FindDiscovered(string sessionId, string discoveredId)
    {
        if (string.IsNullOrWhiteSpace(discoveredId))
        {
            return null;
        }

        Remember(sessionId);
        return _recent.GetValueOrDefault(discoveredId);
    }

    private void Remember(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        try
        {
            foreach (var device in discovery.GetDevices(sessionId))
            {
                if (!Equals(device.Address, IPAddress.None))
                {
                    _recent[device.DiscoveredId] = device;
                }
            }
        }
        catch (KeyNotFoundException)
        {
            // Session already stopped: keep what was remembered before.
        }
    }
}
