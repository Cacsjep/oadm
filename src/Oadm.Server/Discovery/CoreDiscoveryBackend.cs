using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

using Oadm.Core.Discovery;
using Oadm.Core.Settings;
using Oadm.Core.Vapix;

using CoreDiscoveryService = Oadm.Core.Discovery.DiscoveryService;
using SdkDeviceStatus = Oadm.Sdk.Devices.DeviceStatus;

namespace Oadm.Server.Discovery;

/// <summary><see cref="IDiscoveryBackend"/> over the Core discovery (mDNS browser, range scanner, sessions).</summary>
public sealed class CoreDiscoveryBackend(CoreDiscoveryService discovery, ServerSettingsStore settings, VapixProbe probe) : IDiscoveryBackend
{
    /// <summary>How long a host name of "Add manually" may take to resolve.</summary>
    public static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(5);

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

    public async Task<DiscoverySession> StartAddressProbeAsync(string input, CancellationToken ct)
    {
        if (!EnteredAddress.TryParse(input, out var entered, out var error))
        {
            throw new ArgumentException(error);
        }

        var ip = await ResolveAsync(entered!.Host, ct).ConfigureAwait(false);
        string[]? schemes = entered.Scheme is null ? null : [entered.Scheme];
        return discovery.StartAddressProbe(entered.DeviceAddress, async token =>
        {
            var result = await probe.ProbeAsync(entered.DeviceAddress, token, schemes).ConfigureAwait(false);
            return result is null ? null : ToDiscovery(result, ip);
        });
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

    private static async Task<IPAddress> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var ip))
        {
            return ip;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ResolveTimeout);
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, timeout.Token).ConfigureAwait(false);
            return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                ?? addresses.FirstOrDefault()
                ?? throw new ArgumentException($"The host name '{host}' has no address.");
        }
        catch (Exception ex) when (ex is SocketException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new ArgumentException($"The host name '{host}' could not be resolved.", ex);
        }
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
